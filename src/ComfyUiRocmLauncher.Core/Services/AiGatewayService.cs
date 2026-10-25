using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>网关就绪状态（SRS v2.0 FR-5.6）。</summary>
public enum AiGatewayState
{
    /// <summary>服务进程健康（/health 200）。</summary>
    Running,

    /// <summary>已安装但未运行。</summary>
    NotRunning,

    /// <summary>未安装（无 cli 入口，安装目录里也没有待解压的源码 zip，需要引导手动安装）。</summary>
    NotInstalled,

    /// <summary>本机缺少满足版本要求的 Node.js。</summary>
    NodeMissing,
}

/// <summary>EnsureReady 的结果。</summary>
public sealed record AiGatewayReady(bool Ok, string Message, string BaseUrl, string ApiKey);

/// <summary>
/// dsh-our-free-model 独立本地服务的安装与生命周期（SRS v2.0 FR-5.6）：
/// 首次进入 AI 智能分析时引导用户在浏览器下载源码 zip 并放入安装目录，这里负责检测、解压与拉起，不内置下载。
/// </summary>
/// <remarks>
/// 制品为上游 tag 源码 zip（运行时零依赖，<c>node packages/standalone/cli.mjs</c> 直接运行）；
/// 服务首次启动后在数据目录 settings.json 生成 forwardKey，这里自动读取回填，用户无需手工配置。
/// </remarks>
public sealed class AiGatewayService
{
    /// <summary>钉住的上游版本（发版时随 launcher 更新）。</summary>
    public const string UpstreamVersion = "2.5.9";

    private static readonly string[] NodeVersionArgs = ["--version"];

    private readonly LogService _log;
    private readonly string _dataDir;
    private readonly Func<int> _port;
    private readonly Action<string> _persistApiKey;
    private Process? _process;

    public AiGatewayService(
        LogService log,
        string dataDir,
        Func<int> port,
        Action<string> persistApiKey)
    {
        _log = log;
        _dataDir = dataDir;
        _port = port;
        _persistApiKey = persistApiKey;
        // 安装目录放在用户配置目录下：安装版的程序目录位于 Program Files，普通用户不可写，
        // 引导流程的 CreateDirectory/解压会抛 UnauthorizedAccessException 并穿出 async void 崩溃（审查 A1）
        InstallRoot = Path.Combine(
            Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir)))
                ?? Path.GetFullPath(dataDir),
            "tools", "our-free-model");
    }

    /// <summary>安装根目录（用户配置目录下的 tools\our-free-model，普通用户可写）。</summary>
    public string InstallRoot { get; }

    /// <summary>当前状态（供 GUI 决定走引导流程还是直接开窗）。</summary>
    public AiGatewayState Current
    {
        get
        {
            if (FindNode() is null) return AiGatewayState.NodeMissing;
            if (FindCliEntry() is null) return AiGatewayState.NotInstalled;
            return _process is { HasExited: false } ? AiGatewayState.Running : AiGatewayState.NotRunning;
        }
    }

    /// <summary>上游源码 zip 下载地址；传入镜像前缀（如 gh-proxy）时返回镜像地址，供引导弹窗弹到浏览器。</summary>
    public static string ArchiveUrl(string? mirror = null)
    {
        var path = $"https://github.com/Ebony-Vinyl/dsh-our-free-model/archive/refs/tags/v{UpstreamVersion}.zip";
        var prefix = mirror?.Trim();
        return string.IsNullOrEmpty(prefix) ? path : prefix!.TrimEnd('/') + "/" + path;
    }

    /// <summary>安装根目录里是否有用户放置、等待解压的源码 zip（GUI 据此区分「引导下载」与「直接解压安装」）。</summary>
    public bool HasPendingArchive => FindPlacedArchive() is not null;

    /// <summary>安装根目录下的源码 zip（取最新一个；浏览器下载的默认名为 dsh-our-free-model-<版本>.zip）。</summary>
    private string? FindPlacedArchive()
    {
        if (!Directory.Exists(InstallRoot)) return null;
        return Directory.EnumerateFiles(InstallRoot, "*.zip")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// 解压用户放置的源码 zip 完成安装：校验 cli 入口后才算成功，并清理 zip；
    /// 包不完整（浏览器还在下载/中断）会在打开压缩包时失败，折算成可读的错误信息。
    /// </summary>
    private void InstallFromArchive(string zipPath)
    {
        try
        {
            ExtractSafely(zipPath, InstallRoot);
            if (FindCliEntry() is null)
            {
                throw new InvalidOperationException(
                    $"安装包解压完成，但未找到 packages/standalone/cli.mjs（请确认放置的是 v{UpstreamVersion} 源码 zip）。");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"无法使用 {Path.GetFileName(zipPath)}（可能尚未下载完整或格式不对）：{ex.Message}");
        }
        // 清理只是收尾：解压已校验成功，zip 暂时被占用（杀软扫描等）不影响安装结果
        try { File.Delete(zipPath); } catch (IOException) { }
        _log.Append($"[AI网关] 已从手动放置的安装包完成安装：{Path.GetFileName(zipPath)}。");
    }

    /// <summary>
    /// 确保网关可用：检测 Node → 未安装时解压用户放置的源码 zip（没有则提示走引导）→ 拉起并等 /health → 自动回填 forwardKey。
    /// </summary>
    public async Task<AiGatewayReady> EnsureReadyAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var node = FindNode();
        if (node is null)
        {
            return new AiGatewayReady(false,
                "未检测到 Node.js（需要 22.19+ 或 24+）。\n请先安装：winget install OpenJS.NodeJS.LTS",
                BaseUrl, string.Empty);
        }

        if (FindCliEntry() is null)
        {
            var archive = FindPlacedArchive();
            if (archive is null)
            {
                return new AiGatewayReady(false,
                    "尚未安装 AI 分析服务：请按引导在浏览器下载源码 zip，放入安装目录后点击「我已放置好」重试。",
                    BaseUrl, string.Empty);
            }
            try
            {
                progress?.Report("正在解压安装源码包…");
                InstallFromArchive(archive);
            }
            catch (InvalidOperationException ex)
            {
                return new AiGatewayReady(false, $"安装失败：{ex.Message}", BaseUrl, string.Empty);
            }
        }

        var cli = FindCliEntry()!;
        if (_process is null or { HasExited: true })
        {
            progress?.Report("正在启动 AI 分析服务…");
            try
            {
                Start(node, cli);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                return new AiGatewayReady(false, $"服务启动失败：{ex.Message}", BaseUrl, string.Empty);
            }
        }

        progress?.Report("正在等待服务就绪…");
        if (!await WaitHealthyAsync(cancellationToken).ConfigureAwait(false))
        {
            return new AiGatewayReady(false, "服务已启动但未在时限内就绪（/health 无响应）。", BaseUrl, string.Empty);
        }

        // 首启竞态：/health 可能先于 settings.json 落盘——轮询几秒再判失败（审查 #12c）
        progress?.Report("正在读取 API Key…");
        string? key = null;
        for (var i = 0; i < 5 && string.IsNullOrEmpty(key); i++)
        {
            key = ReadForwardKey();
            if (string.IsNullOrEmpty(key)) await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        if (key is { Length: > 0 })
        {
            _persistApiKey(key);
            return new AiGatewayReady(true, "就绪。", BaseUrl, key);
        }
        // 无 Key 时必须 Ok=false：Ok=true 的告警消息会被 GUI 丢弃，用户只能看到下游 401 的症状（审查 #12c）
        return new AiGatewayReady(false,
            "服务已就绪，但尚未读到 API Key（数据目录 settings.json 的 forwardKey）。\n请稍后重试，或在网关管理页手工复制。", BaseUrl, string.Empty);
    }

    /// <summary>退出启动器时回收服务进程（仅回收由启动器拉起的实例）。</summary>
    public void Shutdown()
    {
        if (_process is { HasExited: false } process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                _log.Append("[AI网关] 服务进程已停止。");
            }
            catch (InvalidOperationException) { }
        }
    }

    public string BaseUrl => $"http://127.0.0.1:{_port()}";

    private void Start(string nodePath, string cliPath)
    {
        Directory.CreateDirectory(_dataDir);
        var port = _port();
        var psi = new ProcessStartInfo(nodePath)
        {
            Arguments = $"\"{cliPath}\" --port {port} --data-dir \"{_dataDir}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(cliPath)!,
        };
        var process = Process.Start(psi) ?? throw new InvalidOperationException("进程创建失败。");
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) _log.Append($"[AI网关] {e.Data}"); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) _log.Append($"[AI网关] {e.Data}"); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        _log.Append($"[AI网关] 服务已拉起（pid {process.Id}，端口 {port}）。");
    }

    private async Task<bool> WaitHealthyAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await http.GetAsync(BaseUrl + "/health", cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>从数据目录 settings.json 读取 forwardKey（服务首次启动后生成）。</summary>
    private string? ReadForwardKey()
    {
        var path = Path.Combine(_dataDir, "settings.json");
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("forwardKey", out var key) && key.ValueKind == JsonValueKind.String
                ? key.GetString()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.Append($"[AI网关] 读取 forwardKey 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>定位 Node.js 并校验版本（^22.19.0 || >=24.0.0，与上游 engines 一致）；不满足视为未安装。</summary>
    private static string? FindNode()
    {
        var path = ToolLocator.FindNode();
        if (path is null) return null;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, string.Join(' ', NodeVersionArgs))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5_000);

            // vMAJOR.MINOR.PATCH → 允许 22.x（x>=19）与 24+
            var version = output.StartsWith('v') ? output[1..] : output;
            var parts = version.Split('.');
            if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) return null;
            return major > 24 || (major == 24) || (major == 22 && minor >= 19) ? path : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>在安装根目录下定位 packages/standalone/cli.mjs（源码 zip 解压后根目录带版本号）。</summary>
    private string? FindCliEntry()
    {
        if (!Directory.Exists(InstallRoot)) return null;
        return Directory.EnumerateFiles(InstallRoot, "cli.mjs", SearchOption.AllDirectories)
            .FirstOrDefault(p => p.Contains($"packages{Path.DirectorySeparatorChar}standalone", StringComparison.OrdinalIgnoreCase)
                                 || p.Contains("packages/standalone", StringComparison.OrdinalIgnoreCase));
    }

    private static void ExtractSafely(string zipPath, string targetRoot)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var rootFull = Path.GetFullPath(targetRoot) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            var targetPath = Path.GetFullPath(Path.Combine(targetRoot, entry.FullName));
            if (!targetPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                continue; // zip slip 防护
            }
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }
}
