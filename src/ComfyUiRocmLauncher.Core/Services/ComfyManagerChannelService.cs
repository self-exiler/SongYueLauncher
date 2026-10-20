using System.Text;
using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// ComfyUI-Manager 数据通道镜像接管（SRS FR-8.9）：把 channels.list 与 config.ini 里指向
/// ltdrdata 官方 raw 的通道改写到 GitHub 镜像前缀，选直连时还原。
/// </summary>
/// <remarks>
/// 背景见 docs/评估-ComfyUI-Manager通道镜像接管.md：Manager 只把 GITHUB_ENDPOINT 用在
/// <c>https://github.com/</c> 开头的 url 上，raw 数据通道必须改文件才能接管。
/// <para>
/// 三条硬规则：① 只改「同时存在」的 Manager 目录，绝不预创建目录；② config.ini 只在已存在时改写
/// （凭空新建会伪造「首次升级」场景，触发 pip install + 目录搬迁）；③ 任何 IO/权限异常都降级为
/// 警告字符串，不抛进启动流程。
/// </para>
/// </remarks>
public static class ComfyManagerChannelService
{
    /// <summary>被接管的上游 raw 根地址。只有含该根地址的通道才会被改写，用户自建通道逐字保留。</summary>
    public const string UpstreamChannelRoot = "https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager";

    private const string ChannelListFile = "channels.list";
    private const string ConfigFile = "config.ini";
    private const string BackupSuffix = ".launcher.bak";

    /// <summary>channels.list 缺失时用于预创建的模板（与上游 channels.list.template 逐行一致）。</summary>
    private static readonly string[] ChannelListTemplate =
    [
        "default::" + UpstreamChannelRoot + "/main",
        "recent::" + UpstreamChannelRoot + "/main/node_db/new",
        "legacy::" + UpstreamChannelRoot + "/main/node_db/legacy",
        "forked::" + UpstreamChannelRoot + "/main/node_db/forked",
        "dev::" + UpstreamChannelRoot + "/main/node_db/dev",
        "tutorial::" + UpstreamChannelRoot + "/main/node_db/tutorial",
    ];

    // ================= 目录定位 =================

    /// <summary>
    /// user 目录：<c>--user-directory</c> → <c>--base-directory</c>/user → <c>&lt;ComfyUI 目录&gt;/user</c>。
    /// 与 ComfyUI 的取值规则一致（user-directory 覆盖 base-directory；相对路径按 ComfyUI 目录解析）。
    /// </summary>
    /// <remarks>
    /// 只认 Enabled 的原因：关闭开关只会把它移出 Enabled，Values 里的旧值仍留着（`SetValue` 仅在写空时删）。
    /// 若只看有没有值，用户关掉该参数后启动器会去改写一个 ComfyUI 根本不用的目录。
    /// </remarks>
    public static string ResolveUserDirectory(AppConfig config)
    {
        var userOverride = EnabledValue(config, "user-directory");
        if (userOverride.Length > 0) return ResolveAgainstComfyDir(config, userOverride);

        var baseOverride = EnabledValue(config, "base-directory");
        if (baseOverride.Length > 0)
            return Path.Combine(ResolveAgainstComfyDir(config, baseOverride), "user");

        return Path.GetFullPath(Path.Combine(config.ComfyUiDir, "user"));
    }

    private static string EnabledValue(AppConfig config, string name) =>
        config.Args is { } args && args.IsEnabled(name)
            ? args.Value(name).Trim().Trim('"')
            : string.Empty;

    private static string ResolveAgainstComfyDir(AppConfig config, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(config.ComfyUiDir, path));

    /// <summary>
    /// 已初始化的 Manager 目录（0–2 个）：新版 <c>&lt;user&gt;/__manager</c>（ComfyUI ≥ 0.3.76）与
    /// 旧版 <c>&lt;user&gt;/default/ComfyUI-Manager</c> 共存时两个都返回。
    /// 返回空列表表示 Manager 尚未跑过——此时不做任何写入，等它初始化后下次启动接管。
    /// </summary>
    public static IReadOnlyList<string> ResolveManagerDirs(AppConfig config)
    {
        var userDir = ResolveUserDirectory(config);
        var dirs = new List<string>();
        var modern = Path.Combine(userDir, "__manager");
        if (Directory.Exists(modern)) dirs.Add(modern);
        var legacy = Path.Combine(userDir, "default", "ComfyUI-Manager");
        if (Directory.Exists(legacy)) dirs.Add(legacy);
        return dirs;
    }

    // ================= 纯函数改写 =================

    /// <summary>
    /// 改写 channels.list：逐行 <c>name::url</c>，仅当 url 含 <see cref="UpstreamChannelRoot"/>
    /// 时替换该段（mirror 为空则截断还原）。其他行、注释、空行、行尾空白与 CRLF/LF 风格逐字保留。
    /// </summary>
    public static string RewriteChannelList(string content, string mirror)
    {
        var normalized = NormalizeMirror(mirror);
        var lines = ReadLines(content);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Text;
            var sep = line.IndexOf("::", StringComparison.Ordinal);
            if (sep < 0) continue;
            var head = line[..(sep + 2)];
            var tail = line[(sep + 2)..];
            var rewritten = RewriteUrl(tail, normalized);
            if (rewritten != tail) lines[i] = lines[i] with { Text = head + rewritten };
        }
        return WriteLines(lines);
    }

    /// <summary>
    /// 改写 config.ini：只维护 <c>[default]</c> 段内的 <c>channel_url</c>，值规则同
    /// <see cref="RewriteChannelList"/>。缺失该键时在段内新增（mirror 为空则不写）；缺失
    /// <c>[default]</c> 段且 mirror 非空时追加该段。其他段、其他键、注释与换行风格不动。
    /// </summary>
    public static string RewriteConfigIni(string content, string mirror)
    {
        var normalized = NormalizeMirror(mirror);
        var lines = ReadLines(content);
        var newLine = NewLineOf(lines);

        var sectionStart = -1;
        var keyIndex = -1;
        var inDefault = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i].Text;
            var header = SectionName(text);
            if (header is not null)
            {
                var isDefault = string.Equals(header, "default", StringComparison.OrdinalIgnoreCase);
                if (isDefault && sectionStart < 0) sectionStart = i;
                inDefault = isDefault;
                continue;
            }
            if (inDefault && keyIndex < 0 && IsChannelUrlKey(text)) keyIndex = i;
        }

        if (keyIndex >= 0)
        {
            var text = lines[keyIndex].Text;
            var eq = text.IndexOf('=');
            var head = text[..(eq + 1)];
            var value = text[(eq + 1)..];
            var rewritten = RewriteUrl(value, normalized);
            if (rewritten != value) lines[keyIndex] = lines[keyIndex] with { Text = head + rewritten };
        }
        else if (normalized.Length > 0)
        {
            var entry = "channel_url = " + normalized + "/" + UpstreamChannelRoot + "/main";
            if (sectionStart >= 0)
            {
                if (lines[sectionStart].Terminator.Length == 0)
                    lines[sectionStart] = lines[sectionStart] with { Terminator = newLine };
                lines.Insert(sectionStart + 1, new TextLine(entry, newLine));
            }
            else
            {
                if (lines.Count > 0 && lines[^1].Terminator.Length == 0)
                    lines[^1] = lines[^1] with { Terminator = newLine };
                if (lines.Count > 0) lines.Add(new TextLine(string.Empty, newLine));
                lines.Add(new TextLine("[default]", newLine));
                lines.Add(new TextLine(entry, newLine));
            }
        }
        return WriteLines(lines);
    }

    // ================= 编排 =================

    /// <summary>
    /// 同步一次（幂等）：读 → 改写 → 内容没变不落盘 → 首次改写前留 <c>.launcher.bak</c>。
    /// 结果只用于日志，任何失败都不影响启动。
    /// </summary>
    public static ChannelSyncReport Sync(AppConfig config)
    {
        var mirror = NormalizeMirror(config.EnvUpdate?.GithubMirror);
        var changed = new List<string>();
        var warnings = new List<string>();

        IReadOnlyList<string> dirs;
        try
        {
            dirs = ResolveManagerDirs(config);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            warnings.Add($"定位 ComfyUI-Manager 目录失败：{ex.Message}");
            return new ChannelSyncReport(SummaryOf(mirror, 0), changed, warnings);
        }

        if (dirs.Count == 0)
        {
            return new ChannelSyncReport("ComfyUI-Manager 尚未初始化，下次启动自动接管", changed, warnings);
        }

        // 兜底：本方法跑在启动流程里，调用方没有 try/catch，任何未预料的文件系统异常都不能穿出去
        try
        {
            foreach (var dir in dirs)
            {
                // channels.list 可以预创建（Manager 只会读，缺了它自己也会从模板复制）；mirror 为空时
                // 不建——上游模板本身就是直连，凭空造文件只是噪音。
                var channelListPath = Path.Combine(dir, ChannelListFile);
                if (File.Exists(channelListPath))
                {
                    SyncFile(channelListPath, createSeed: null, RewriteChannelList, mirror, changed, warnings);
                }
                else if (mirror.Length > 0)
                {
                    SyncFile(channelListPath, createSeed: string.Join("\r\n", ChannelListTemplate) + "\r\n",
                        RewriteChannelList, mirror, changed, warnings);
                }

                // config.ini 绝不在缺失时新建（见类型注释的硬规则 ②）。
                var configPath = Path.Combine(dir, ConfigFile);
                if (File.Exists(configPath)) SyncFile(configPath, null, RewriteConfigIni, mirror, changed, warnings);
            }
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            warnings.Add($"通道接管中断：{ex.Message}");
        }

        return new ChannelSyncReport(SummaryOf(mirror, changed.Count), changed, warnings);
    }

    /// <summary>
    /// 值得降级为警告的异常（文件系统/权限类）。
    /// 不算的：进程级致命异常与真正的编程错误，它们该炸就炸，被吞掉只会掩盖 bug。
    /// </summary>
    private static bool IsFileSystemFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or PathTooLongException or System.Security.SecurityException;

    private static string SummaryOf(string mirror, int changedCount)
    {
        if (mirror.Length > 0)
        {
            return changedCount > 0
                ? $"ComfyUI-Manager 数据通道已指向镜像 {mirror}（{changedCount} 个文件）"
                : "ComfyUI-Manager 数据通道已指向镜像，无需变更";
        }
        return changedCount > 0
            ? $"ComfyUI-Manager 数据通道已还原为直连（{changedCount} 个文件）"
            : "ComfyUI-Manager 数据通道为直连，无需变更";
    }

    private static void SyncFile(
        string path,
        string? createSeed,
        Func<string, string, string> rewrite,
        string mirror,
        List<string> changed,
        List<string> warnings)
    {
        var name = Path.GetFileName(path);
        var temp = path + ".tmp";
        try
        {
            var exists = File.Exists(path);
            if (!exists && createSeed is null) return;
            var original = exists ? File.ReadAllText(path) : createSeed!;
            var updated = rewrite(original, mirror);
            if (exists && updated == original) return;

            if (!exists) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            BackupOnce(path);
            File.WriteAllText(temp, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);
            changed.Add(path);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            warnings.Add($"改写 {name} 失败：{ex.Message}");
            // 写一半失败别把 .tmp 留在 Manager 目录里（它自己不认这个文件，但会污染目录）
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception cleanupEx) when (IsFileSystemFailure(cleanupEx)) { }
        }
    }

    /// <summary>首次改写前留一份原始文件；已存在备份（用户手改过的第一版）不覆盖。</summary>
    private static void BackupOnce(string path)
    {
        if (!File.Exists(path)) return;
        var backup = path + BackupSuffix;
        if (File.Exists(backup)) return;
        try
        {
            File.Copy(path, backup, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 备份失败不阻断改写：Manager 自己也会写这个文件，且 mirror 清空即可还原
        }
    }

    // ================= 文本工具 =================

    /// <summary>镜像前缀规范化：去空白与结尾斜杠。空串 = 直连。</summary>
    private static string NormalizeMirror(string? mirror) =>
        (mirror ?? string.Empty).Trim().TrimEnd('/');

    /// <summary>
    /// 只替换 URL 里的上游根地址片段：保留镜像前缀之外的一切（空白、查询串、用户自建仓库）。
    /// 前后字符不是 '/' 时不认（避免 ComfyUI-Manager-fork 这类撞名被误改）。
    /// </summary>
    private static string RewriteUrl(string url, string mirror)
    {
        var core = url.Trim();
        if (core.Length == 0) return url;
        var idx = core.IndexOf(UpstreamChannelRoot, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return url;
        var after = idx + UpstreamChannelRoot.Length;
        if (after < core.Length && core[after] != '/') return url;

        // 从上游根地址截断，丢掉旧镜像前缀后按当前选择重建；两侧空白原样保留
        var upstream = core[idx..];
        var rewritten = mirror.Length == 0 ? upstream : mirror + "/" + upstream;
        var lead = url[..(url.Length - url.TrimStart().Length)];
        var trail = url[url.TrimEnd().Length..];
        return lead + rewritten + trail;
    }

    private static bool IsChannelUrlKey(string line)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith("channel_url", StringComparison.OrdinalIgnoreCase)) return false;
        var rest = trimmed["channel_url".Length..].TrimStart();
        return rest.StartsWith('=');
    }

    /// <summary>段头行返回段名，非段头返回 null。</summary>
    private static string? SectionName(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 3 || trimmed[0] != '[' || trimmed[^1] != ']') return null;
        return trimmed[1..^1].Trim();
    }

    private readonly record struct TextLine(string Text, string Terminator);

    /// <summary>按 '\n' 拆行并保留原行尾（CRLF / LF / 末行无行尾），以便逐字还原。</summary>
    private static List<TextLine> ReadLines(string content)
    {
        var lines = new List<TextLine>();
        var start = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n') continue;
            var bodyEnd = i > start && content[i - 1] == '\r' ? i - 1 : i;
            lines.Add(new TextLine(content[start..bodyEnd], content[bodyEnd..(i + 1)]));
            start = i + 1;
        }
        if (start < content.Length) lines.Add(new TextLine(content[start..], string.Empty));
        return lines;
    }

    private static string WriteLines(List<TextLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines) sb.Append(line.Text).Append(line.Terminator);
        return sb.ToString();
    }

    private static string NewLineOf(List<TextLine> lines)
    {
        foreach (var line in lines)
        {
            if (line.Terminator.Length > 0) return line.Terminator;
        }
        return "\r\n";
    }
}

/// <summary>一次通道同步的结果：<paramref name="Summary"/> 直接进日志，警告不阻断启动。</summary>
public sealed record ChannelSyncReport(
    string Summary,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> Warnings);
