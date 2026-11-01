using System.Text.RegularExpressions;
using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 插件管理（SRS FR-10 方案 A）：custom_nodes 目录扫描与启用/禁用/卸载/备份恢复。
/// 扫描口径与上游加载器逐条对齐（nodes.py load_custom_nodes / main.py prestartup）：
/// 跳过 __pycache__ 与非 .py 散文件；<c>.disabled</c> 后缀即禁用（上游原生约定，无需配置文件）；
/// 同名 foo 与 foo.disabled 并存时上游只加载 foo——被遮蔽的禁用项不显示。
/// git 元数据直读 .git 内文件（HEAD/refs/packed-refs/config），不起子进程，百级插件也是毫秒级。
/// </summary>
public sealed partial class PluginService
{
    public sealed record PluginInfo(
        string Name, string Path, bool IsDirectory, bool IsDisabled,
        bool HasGit, string? Branch, string? Commit, string? RemoteUrl,
        bool HasRequirements, DateTimeOffset UpdatedAt);

    public sealed record BackupInfo(string BackupDir, string PluginName, DateTimeOffset UninstalledAt);

    /// <summary>插件根目录：走 ComfyDirResolver，base-directory 外置后自动跟随（FR-1.4 同源）。</summary>
    public static string CustomNodesDir(AppConfig config) => ComfyDirResolver.Resolve(config, "custom_nodes");

    public static IReadOnlyList<PluginInfo> Scan(string customNodesDir)
    {
        if (!Directory.Exists(customNodesDir)) return Array.Empty<PluginInfo>();

        var entries = Directory.EnumerateFileSystemEntries(customNodesDir)
            .Where(p => Path.GetFileName(p) != "__pycache__")
            .Where(p => Directory.Exists(p) || string.Equals(Path.GetExtension(p), ".py", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var liveNames = new HashSet<string>(
            entries.Where(p => !p.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                .Select(p => Path.GetFileName(p)!),
            StringComparer.OrdinalIgnoreCase);

        var list = new List<PluginInfo>();
        foreach (var path in entries)
        {
            var fileName = Path.GetFileName(path);
            var disabled = fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            // 被同名启用项遮蔽：上游根本不加载它，列出来只会误导
            if (disabled && liveNames.Contains(fileName[..^".disabled".Length])) continue;

            var name = disabled ? fileName[..^".disabled".Length] : fileName;
            var isDir = Directory.Exists(path);
            // 散 .py 插件的模块名不含扩展名（上游 get_module_name），显示与改名都以它为准
            if (!isDir) name = Path.GetFileNameWithoutExtension(name);
            list.Add(Build(name, path, disabled, isDir));
        }

        return list
            .OrderByDescending(p => p.IsDisabled)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        static PluginInfo Build(string name, string path, bool disabled, bool isDir)
        {
            var (hasGit, branch, commit, remote) = isDir ? ReadGitMeta(path) : (false, null, null, null);
            DateTimeOffset updated;
            try
            {
                updated = isDir
                    ? new DirectoryInfo(path).LastWriteTimeUtc
                    : new FileInfo(path).LastWriteTimeUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                updated = DateTimeOffset.MinValue;
            }

            return new PluginInfo(name, path, isDir, disabled, hasGit, branch,
                commit is { Length: > 7 } full ? full[..7] : commit, remote,
                isDir && File.Exists(Path.Combine(path, "requirements.txt")), updated);
        }
    }

    /// <summary>启用 ⇄ 禁用：整目录（或散 .py）改名加减 .disabled 后缀，与上游约定一致。</summary>
    public static void Toggle(PluginInfo plugin)
    {
        var disabled = plugin.Path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        if (disabled)
        {
            var target = plugin.Path[..^".disabled".Length];
            if (ExistsExact(target))
                throw new InvalidOperationException($"已存在同名启用项 {Path.GetFileName(target)}，请先处理重复目录。");
            Move(plugin.Path, target);
        }
        else
        {
            if (ExistsExact(plugin.Path + ".disabled"))
                throw new InvalidOperationException($"已存在同名禁用项 {plugin.Name}.disabled，请先删除或恢复该备份目录。");
            Move(plugin.Path, plugin.Path + ".disabled");
        }
    }

    /// <summary>
    /// 卸载：移入 <c>backupRoot/&lt;时间戳&gt;_&lt;名称&gt;/&lt;名称&gt;</c>。
    /// 外层容器目录是必需的——散 `.py` 插件的源是文件，直接落成文件会让 ListBackups（只枚举目录）漏掉它。
    /// </summary>
    public static string Uninstall(PluginInfo plugin, string backupRoot)
    {
        var name = Path.GetFileName(plugin.Path);
        var container = Path.Combine(backupRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}_{name}");
        for (var n = 2; Directory.Exists(container); n++) container = Path.Combine(backupRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}_{name}_{n}");
        Directory.CreateDirectory(container);
        try
        {
            Move(plugin.Path, Path.Combine(container, name));
        }
        catch
        {
            TryDeleteTree(container);
            throw;
        }
        return container;
    }

    public static IReadOnlyList<BackupInfo> ListBackups(string backupRoot)
    {
        if (!Directory.Exists(backupRoot)) return Array.Empty<BackupInfo>();
        var list = new List<BackupInfo>();
        foreach (var dir in Directory.EnumerateDirectories(backupRoot))
        {
            var m = BackupNameRegex().Match(Path.GetFileName(dir));
            if (!m.Success) continue;
            list.Add(new BackupInfo(dir, m.Groups["name"].Value,
                DateTimeOffset.TryParseExact(m.Groups["ts"].Value, "yyyyMMdd-HHmmss", null,
                    System.Globalization.DateTimeStyles.None, out var ts) ? ts : DateTimeOffset.MinValue));
        }
        return list.OrderByDescending(b => b.UninstalledAt).ToList();
    }

    /// <summary>从备份恢复：custom_nodes 里已有同名项时加 _restored 后缀落回，不覆盖现有目录。</summary>
    public static void Restore(BackupInfo backup, string customNodesDir)
    {
        Directory.CreateDirectory(customNodesDir);
        var payload = Directory.EnumerateFileSystemEntries(backup.BackupDir).FirstOrDefault()
            ?? throw new InvalidOperationException($"备份「{backup.PluginName}」是空目录，没有可恢复的内容。");

        var name = Path.GetFileName(payload);
        var target = Path.Combine(customNodesDir, name);
        for (var n = 2; ExistsAny(target); n++) target = Path.Combine(customNodesDir, $"{name}_restored{n}");
        Move(payload, target);
        TryDeleteTree(backup.BackupDir);
    }

    /// <summary>
    /// 读插件目录的 git 头信息（完整 40 位 hash，供更新检测比对）；非 git 检出返回 null。
    /// </summary>
    public static (string? Branch, string? HeadHash, string? RemoteUrl)? ReadGitHead(string dir)
    {
        var (hasGit, branch, commit, remote) = ReadGitMeta(dir);
        return hasGit ? (branch, commit, remote) : null;
    }

    private static (bool HasGit, string? Branch, string? Commit, string? Remote) ReadGitMeta(string dir)
    {
        var git = Path.Combine(dir, ".git");
        if (!Directory.Exists(git)) return (false, null, null, null);

        string? branch = null, commit = null;
        try
        {
            var head = ReadFirstLine(Path.Combine(git, "HEAD"));
            if (head is not null)
            {
                if (head.StartsWith("ref:", StringComparison.Ordinal))
                {
                    var refName = head["ref:".Length..].Trim();
                    // refs/heads/main → main（保留 feature/x 这类多级分支名）；非 heads 的 ref 原样显示
                    branch = refName.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
                        ? refName["refs/heads/".Length..]
                        : refName;
                    commit = ResolveRef(git, refName);
                }
                else if (head.Length >= 7)
                {
                    branch = "(detached)";
                    commit = head;   // HEAD 里就是完整 hash
                }
            }

            string? remote = null;
            var config = ReadAllText(Path.Combine(git, "config"));
            if (config is not null)
            {
                var m = RemoteUrlRegex().Match(config);
                if (m.Success) remote = m.Groups["url"].Value;
            }

            return (true, branch, commit, remote);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (true, branch, commit, null);
        }
    }

    /// <summary>解析 ref 为完整 40 位 hash（松散 ref 优先，缺失回落 packed-refs）；显示侧再截短。</summary>
    private static string? ResolveRef(string gitDir, string refName)
    {
        // refName 已是完整形式（refs/heads/main），直接接在 .git 下拼松散 ref 路径
        var loose = ReadFirstLine(Path.Combine(gitDir, refName.Replace('/', Path.DirectorySeparatorChar)));
        if (loose is { Length: >= 7 } l) return l;
        var packed = ReadAllText(Path.Combine(gitDir, "packed-refs"));
        return packed?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => !line.StartsWith('#') && line.EndsWith(" " + refName, StringComparison.Ordinal))
            ?.Split(' ')[0] is { Length: >= 7 } hash ? hash : null;
    }

    private static string? ReadFirstLine(string file) => ReadAllText(file)?.Split('\n')[0].Trim();

    private static string? ReadAllText(string file)
    {
        try { return File.Exists(file) ? File.ReadAllText(file) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool ExistsExact(string path) => Directory.Exists(path) || File.Exists(path);

    /// <summary>目标名是否被占用：含「同名禁用项」——恢复/卸载落点要避开 .disabled 遮蔽歧义。</summary>
    private static bool ExistsAny(string path) =>
        ExistsExact(path) || ExistsExact(path + ".disabled");

    private static void Move(string source, string target)
    {
        if (Directory.Exists(source)) MoveDirectory(source, target);
        else File.Move(source, target);   // File.Move 自 .NET Core 3.0 起支持跨卷
    }

    /// <summary>
    /// 同卷改名瞬时完成；跨卷退化为「先复制、后删源」——插件在外置数据盘而备份目录在系统盘是常见布局，
    /// Directory.Move 在这种情况下会直接抛「must have identical roots」。
    /// 复制失败清掉半份副本（源未动）；删源失败则保留两份并明确告知，绝不出现两头都没有的中间态。
    /// </summary>
    private static void MoveDirectory(string source, string target)
    {
        if (SameVolume(source, target))
        {
            Directory.Move(source, target);
            return;
        }

        try
        {
            CopyTree(source, target);
        }
        catch
        {
            TryDeleteTree(target);
            throw;
        }

        try
        {
            PathHelpers.DeleteDirectoryTree(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"已复制到 {target}，但原目录删不掉（可能被占用）：{source}。两处内容都在，请手动清理其中一处。");
        }
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyTree(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    private static void TryDeleteTree(string dir)
    {
        try { if (Directory.Exists(dir)) PathHelpers.DeleteDirectoryTree(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool SameVolume(string x, string y) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(x)), Path.GetPathRoot(Path.GetFullPath(y)),
            StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\[remote ""origin""\][\s\S]*?url\s*=\s*(?<url>\S+)", RegexOptions.Multiline)]
    private static partial Regex RemoteUrlRegex();

    [GeneratedRegex(@"^(?<ts>\d{8}-\d{6})_(?<name>.+)$")]
    private static partial Regex BackupNameRegex();
}
