using AiDiagnostics.Runtime;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 目录外置链接（SRS FR-1.5）：把 &lt;ComfyUI目录&gt;\models、&lt;ComfyUI目录&gt;\output 变为指向
/// 整合包外数据目录的 NTFS 目录联接（junction，无需管理员/开发者模式特权）。
/// 数据从此存放在整合包之外——删除或重装整合包时模型与输出保留在外部目录，不会随之丢失。
/// 建立链接时把目录内已有内容迁入外部目录（同盘为目录改名、瞬时完成；跨盘 robocopy 复制移动，
/// 耗时与体积成正比）；解除链接（还原）时只删联接本身并把内容搬回，恢复为普通目录。
/// </summary>
public static class DirLinkService
{
    /// <summary>支持外置的目录名（相对 ComfyUI 目录）；UI 展示顺序即数组顺序。</summary>
    public static readonly string[] LinkableDirs = ["models", "output"];

    /// <summary>
    /// 单个目录的链接现状。文件系统是唯一真相，config 里的记录只用于回显与默认值。
    /// </summary>
    public sealed record DirLinkInfo(bool Exists, bool IsLink, string? TargetPath, bool TargetExists, bool HasContent);

    /// <summary>链接点路径（&lt;ComfyUI目录&gt;\&lt;name&gt;）。</summary>
    public static string LinkPath(string comfyUiDir, string name) => Path.Combine(comfyUiDir, name);

    /// <summary>外部目标路径（&lt;外部数据根&gt;\&lt;name&gt;）。</summary>
    public static string TargetPath(string externalRoot, string name) =>
        Path.Combine(Path.GetFullPath(externalRoot), name);

    /// <summary>读取目录现状：不存在 / 普通目录（是否有内容）/ 联接（目标是否存在）。</summary>
    public static DirLinkInfo Inspect(string comfyUiDir, string name)
    {
        var path = LinkPath(comfyUiDir, name);
        DirectoryInfo info;
        try { info = new DirectoryInfo(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return new DirLinkInfo(false, false, null, false, false);
        }

        if (!info.Exists) return new DirLinkInfo(false, false, null, false, false);

        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            var target = ResolveTarget(path);
            return new DirLinkInfo(true, true, target, target is not null && Directory.Exists(target), false);
        }

        return new DirLinkInfo(true, false, null, false, HasAnyEntry(path));
    }

    /// <summary>
    /// 建立外置链接（FR-1.5）：&lt;ComfyUI目录&gt;\name → &lt;externalRoot&gt;\name。
    /// 目录中的现有内容先迁入外部目录，随后原位置变为 junction。已指向同一目标时为幂等空操作。
    /// 返回值为提示性文字（换绑时旧目标数据未搬动等），失败抛异常且不破坏数据。
    /// </summary>
    public static async Task<string?> LinkAsync(
        TaskRunner runner, string comfyUiDir, string name, string externalRoot, Action<string>? onProgress = null)
    {
        if (!Directory.Exists(comfyUiDir))
            throw new InvalidOperationException("ComfyUI 目录不存在，请先在主页「路径设置」中填写。");

        var linkPath = LinkPath(comfyUiDir, name);
        var target = TargetPath(externalRoot, name);
        if (IsInside(target, comfyUiDir))
            throw new InvalidOperationException(
                $"外部数据目录 {target} 位于 ComfyUI 目录内——删除整合包时数据仍会被一并删除，请另选整合包外的位置。");

        var state = Inspect(comfyUiDir, name);
        if (state.IsLink && PathsEqual(state.TargetPath, target))
            return $"「{name}」已外置到 {target}，无需重复操作。";

        string? warning = null;
        // 先建外部目标（含数据根目录链）：目录本不存在时也能外置，且联接不会悬空
        Directory.CreateDirectory(target);
        if (state.IsLink)
        {
            // 换绑：只删联接本身（不动旧目标里的数据），再按普通流程重建
            onProgress?.Invoke($"移除现有链接（{state.TargetPath} 中的数据原地保留）…");
            Directory.Delete(linkPath);
            warning = $"原链接目标 {state.TargetPath} 中的数据未搬动，如不再需要请自行处理。";
        }
        else if (state.Exists && state.HasContent)
        {
            onProgress?.Invoke("迁移现有内容（同盘瞬时完成，跨盘耗时与体积成正比）…");
            if (SameVolume(linkPath, target) && !HasAnyEntry(target))
            {
                // 同盘且外部目录为空：整目录改名，无论多大都是瞬时完成
                Directory.Delete(target, recursive: false);
                Directory.Move(linkPath, target);
            }
            else
            {
                await MoveTreeAsync(runner, linkPath, target).ConfigureAwait(false);
            }
        }

        // 清出链接点：正常情况下目录已被迁走/本就不存在；有文件被占用迁不出去时中止，数据保持原样
        if (Directory.Exists(linkPath))
        {
            try { Directory.Delete(linkPath, recursive: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"「{name}」中有文件未能迁出（可能被其他程序占用），链接未建立、数据保持原样。关闭相关程序后重试。");
            }
        }

        onProgress?.Invoke($"建立目录联接 {linkPath} → {target} …");
        // mklink 是 cmd 内建命令，必须经 cmd /c 调用；/J 联接不要求特权
        var code = await runner.Run(
            "cmd.exe", $"/c mklink /J {CommandLine.Quote(linkPath)} {CommandLine.Quote(TrimSeparators(target))}",
            comfyUiDir).ConfigureAwait(false);
        if (code != 0)
            throw new InvalidOperationException($"建立目录联接失败（退出码 {code}）。");

        var verify = Inspect(comfyUiDir, name);
        if (!verify.IsLink || !PathsEqual(verify.TargetPath, target))
            throw new InvalidOperationException("链接建立后校验失败，请在文件资源管理器中确认目录状态。");

        return warning;
    }

    /// <summary>
    /// 解除外置链接（还原）：删除 junction（只删联接本身，不影响外部目录中的数据），
    /// 在原位置恢复普通目录，并把外部目录中的内容搬回；外部目录中搬不动的文件保留原处。
    /// </summary>
    public static async Task<string?> UnlinkAsync(
        TaskRunner runner, string comfyUiDir, string name, Action<string>? onProgress = null)
    {
        if (!Directory.Exists(comfyUiDir))
            throw new InvalidOperationException("ComfyUI 目录不存在，请先在主页「路径设置」中填写。");

        var linkPath = LinkPath(comfyUiDir, name);
        var state = Inspect(comfyUiDir, name);
        if (!state.IsLink || state.TargetPath is null)
            throw new InvalidOperationException($"「{name}」目前不是目录链接。");

        onProgress?.Invoke("移除链接（外部目录中的数据不受影响）…");
        Directory.Delete(linkPath);

        string? warning = null;
        if (state.TargetExists)
        {
            onProgress?.Invoke("把内容搬回整合包目录（同盘瞬时完成，跨盘耗时与体积成正比）…");
            if (SameVolume(linkPath, state.TargetPath))
            {
                // 同盘：外部目录整体改名为原位置，瞬时完成
                Directory.Move(state.TargetPath, linkPath);
            }
            else
            {
                Directory.CreateDirectory(linkPath);
                await MoveTreeAsync(runner, state.TargetPath, linkPath).ConfigureAwait(false);
                if (Directory.Exists(state.TargetPath))
                    warning = $"部分文件未能搬回（可能被占用），仍保留在 {state.TargetPath}。";
            }
        }

        if (!Directory.Exists(linkPath)) Directory.CreateDirectory(linkPath);

        var verify = Inspect(comfyUiDir, name);
        if (verify.Exists && verify.IsLink)
            throw new InvalidOperationException("链接解除后校验失败，请在文件资源管理器中确认目录状态。");

        return warning;
    }

    /// <summary>robocopy /E /MOVE 迁移整棵目录树；/R:1 /W:1 让被占用文件快速跳过（默认重试 100 万次会挂死）。</summary>
    private static async Task MoveTreeAsync(TaskRunner runner, string source, string target)
    {
        var code = await runner.Run(
            "robocopy",
            $"{CommandLine.Quote(source)} {CommandLine.Quote(target)} /E /MOVE /R:1 /W:1 /NFL /NDL /NP",
            Environment.CurrentDirectory).ConfigureAwait(false);
        if (!RepoService.IsRobocopySuccess(code))
            throw new InvalidOperationException(
                $"内容迁移失败（robocopy 退出码 {code}）。已迁移的文件保留在外部目录，未迁移的仍在原目录，可重试。");
    }

    private static string? ResolveTarget(string path)
    {
        try { return new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: false)?.FullName; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>目录里是否还有任何条目（只取首个枚举项，不遍历整棵树）。读不到时按「有内容」处理，走更稳的 robocopy。</summary>
    private static bool HasAnyEntry(string dir)
    {
        try { return Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>child 是否位于 ancestor 内部（不含相等；Windows 路径不区分大小写）。</summary>
    internal static bool IsInside(string child, string ancestor)
    {
        var c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ancestor)) + Path.DirectorySeparatorChar;
        return c.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>两个路径是否在同一卷（同卷才能用目录改名瞬时迁移）。</summary>
    internal static bool SameVolume(string x, string y) =>
        string.Equals(
            Path.GetPathRoot(Path.GetFullPath(x)),
            Path.GetPathRoot(Path.GetFullPath(y)),
            StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string? a, string? b)
    {
        if (a is null || b is null) return false;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
