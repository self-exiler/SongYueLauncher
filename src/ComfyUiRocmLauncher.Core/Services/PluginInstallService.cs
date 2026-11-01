using AiDiagnostics.Runtime;
using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 插件安装（SRS FR-10 方案 B）：git clone 到 custom_nodes/&lt;仓库名&gt; + 自动装依赖。
/// 纯解析函数（地址净化 / 镜像 / 目录名）与执行分离，前者可单测、后者走 TaskRunner 单任务槽。
/// </summary>
public static class PluginInstallService
{
    /// <summary>可作为插件仓库的地址：http(s) 或 ssh 形式的 git URL。</summary>
    public static bool IsClonableUrl(string url)
    {
        var u = url.Trim();
        return u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || (u.Contains('@') && u.Contains(':') && !u.Contains(' '));
    }

    /// <summary>
    /// 从仓库地址取本地目录名：末段去 `.git`，非法字符与点号结尾一律清洗。
    /// ssh 形式（git@host:author/repo.git）与 https 形式同样处理。
    /// </summary>
    public static string RepoName(string url)
    {
        var u = url.Trim().TrimEnd('/', '\\');
        var slash = Math.Max(u.LastIndexOf('/'), u.LastIndexOf('\\'));
        var name = slash >= 0 ? u[(slash + 1)..] : u;
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

        // 分隔符已由取末段排除，这里只处理其余非法字符与保留名；净化后为空则回退 plugin
        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        name = name.TrimEnd('.', ' ');
        return string.IsNullOrEmpty(name) || name is "." or ".." ? "plugin" : name;
    }

    /// <summary>GitHub 镜像只作用于 github.com 地址（三分法：其他站点直连，不套前缀）。</summary>
    public static string ApplyMirror(string url, string mirror) =>
        !string.IsNullOrWhiteSpace(mirror) && url.Contains("github.com", StringComparison.OrdinalIgnoreCase)
            ? MirrorPolicy.Github(url, mirror)
            : url;

    /// <summary>
    /// 安装一个插件：校验 → clone（浅克隆 → 完整克隆 → 免 sslVerify 三级降级）→ 结构提示 → 装依赖。
    /// 返回 false 表示失败，原因已逐行写进 <paramref name="onOutput"/>；失败时清掉残留目录，不留半成品。
    /// </summary>
    public static async Task<bool> InstallAsync(
        TaskRunner runner, AppConfig config, string url, bool installDeps, Action<string>? onOutput = null)
    {
        url = url.Trim();
        if (!IsClonableUrl(url))
        {
            onOutput?.Invoke("[错误] 地址无效：请粘贴 https:// 开头的仓库地址（或 git@host:author/repo.git）");
            return false;
        }

        var comfyDir = config.ComfyUiDir;
        if (string.IsNullOrWhiteSpace(comfyDir) || !Directory.Exists(comfyDir))
        {
            onOutput?.Invoke("[错误] ComfyUI 目录不存在，请先在主页「路径设置」中填写");
            return false;
        }

        var git = ToolLocator.FindGit();
        if (git is null)
        {
            onOutput?.Invoke("[错误] 未找到 git，请先在「环境与更新」页安装（winget install Git.Git）");
            return false;
        }

        var target = Path.Combine(PluginService.CustomNodesDir(config), RepoName(url));
        if (Directory.Exists(target) || Directory.Exists(target + ".disabled"))
        {
            onOutput?.Invoke($"[错误] 插件目录已存在：{target}\n       如需重装，请先在插件列表里卸载（或禁用后由「卸载备份」清理）");
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var cloneUrl = ApplyMirror(url, config.EnvUpdate.GithubMirror);
        if (!ReferenceEquals(cloneUrl, url) && cloneUrl.Length > url.Length)
            onOutput?.Invoke($"[i] 走 GitHub 镜像：{cloneUrl}");

        if (!await CloneWithFallbackAsync(runner, git, cloneUrl, target, onOutput).ConfigureAwait(false))
        {
            TryRemoveTree(target);
            return false;
        }

        // 克隆完把 origin 改回真实仓库地址：镜像只代理对象下载，留下镜像地址会让后续的
        // ls-remote / fetch 卡到超时（真机实测 gh-proxy 不服务 info/refs），界面也会显示成镜像 URL
        if (!ReferenceEquals(cloneUrl, url) && cloneUrl.Length > url.Length)
        {
            onOutput?.Invoke($"[i] 还原 origin 为真实地址：{url}");
            await runner.Run(git, $"remote set-url origin {CommandLine.Quote(url)}", target, onOutput).ConfigureAwait(false);
        }

        if (!LooksLikePlugin(target))
            onOutput?.Invoke("[警告] 目录里没有 __init__.py / 顶层 .py / src，可能不是 ComfyUI 插件；已保留，若启动报错请在列表里禁用或卸载");

        if (!installDeps)
        {
            onOutput?.Invoke("√ 安装完成（已跳过依赖安装）");
            return true;
        }

        var depsOk = await InstallDepsAsync(runner, config, target, onOutput).ConfigureAwait(false);
        onOutput?.Invoke(depsOk ? "√ 安装完成，重启 ComfyUI 后生效" : "√ 代码已就位，但依赖安装失败——节点可能加载不了，见上方输出");
        return true;
    }

    /// <summary>
    /// 装插件依赖：有 requirements.txt 走 <c>uv pip install -r</c>（PyPI 镜像适用）；
    /// 否则有 install.py 走 <c>python install.py</c>；都没有则视为免依赖。
    /// </summary>
    public static async Task<bool> InstallDepsAsync(
        TaskRunner runner, AppConfig config, string pluginDir, Action<string>? onOutput = null)
    {
        var requirements = Path.Combine(pluginDir, "requirements.txt");
        var installScript = Path.Combine(pluginDir, "install.py");

        if (File.Exists(requirements))
        {
            var uv = ToolLocator.FindUv();
            if (uv is null)
            {
                onOutput?.Invoke("[错误] 未找到 uv，无法安装依赖（依赖清单：requirements.txt）");
                return false;
            }
            if (!VenvService.VenvExists(config.ComfyUiDir))
            {
                onOutput?.Invoke("[错误] .venv 不存在，请先在「环境与更新」页创建 Python 环境");
                return false;
            }

            onOutput?.Invoke($"[依赖] uv pip install -r requirements.txt …（{Path.GetFileName(pluginDir)}）");
            var args = $"pip install -r {CommandLine.Quote(requirements)} " +
                $"--python {CommandLine.Quote(config.PythonExe)} " +
                MirrorPolicy.PypiIndexArg(config.EnvUpdate.PypiMirror);
            var code = await runner.Run(uv, args, PathHelpers.ExistingDirOrCurrent(config.ComfyUiDir), onOutput)
                .ConfigureAwait(false);
            if (code != 0) onOutput?.Invoke($"[错误] 依赖安装失败（退出码 {code}）");
            return code == 0;
        }

        if (File.Exists(installScript))
        {
            if (!File.Exists(config.PythonExe))
            {
                onOutput?.Invoke("[错误] 插件自带 install.py，但 Python 解释器不可用");
                return false;
            }
            onOutput?.Invoke($"[依赖] python install.py …（{Path.GetFileName(pluginDir)}）");
            var code = await runner.Run(config.PythonExe, CommandLine.Quote(installScript), pluginDir, onOutput)
                .ConfigureAwait(false);
            if (code != 0) onOutput?.Invoke($"[错误] install.py 执行失败（退出码 {code}）");
            return code == 0;
        }

        onOutput?.Invoke("[依赖] 无 requirements.txt / install.py，跳过");
        return true;
    }

    /// <summary>
    /// 三级降级：浅克隆（最快、默认够用）→ 完整克隆（浅克隆被镜像/协议卡住时）→ 关 sslVerify。
    /// LFS 一律跳过拉取（GIT_LFS_SKIP_SMUDGE=1）：插件仓库里的示例模型动辄数 GB，加载并不需要它们。
    /// </summary>
    private static async Task<bool> CloneWithFallbackAsync(
        TaskRunner runner, string git, string url, string target, Action<string>? onOutput)
    {
        var env = new Dictionary<string, string> { ["GIT_LFS_SKIP_SMUDGE"] = "1" };
        var quotedUrl = CommandLine.Quote(url);
        var quotedTarget = CommandLine.Quote(target);
        var attempts = new (string Label, string Args)[]
        {
            ("浅克隆 --depth 1", $"clone --depth 1 {quotedUrl} {quotedTarget}"),
            ("完整克隆", $"clone {quotedUrl} {quotedTarget}"),
            ("完整克隆（跳过 SSL 校验）", $"-c http.sslVerify=false clone {quotedUrl} {quotedTarget}"),
        };

        var workDir = Path.GetDirectoryName(target)!;
        for (var i = 0; i < attempts.Length; i++)
        {
            var (label, args) = attempts[i];
            onOutput?.Invoke($"[{i + 1}/{attempts.Length}] git clone {label} …");
            TryRemoveTree(target);
            try
            {
                var code = await runner.Run(git, args, workDir, onOutput, env).ConfigureAwait(false);
                if (code == 0 && Directory.Exists(target)) return true;
                onOutput?.Invoke($"      退出码 {code}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
            {
                onOutput?.Invoke($"      无法执行：{ex.Message}");
            }

            if (i < attempts.Length - 1) onOutput?.Invoke("      失败，换下一种方式重试…");
        }

        onOutput?.Invoke("[错误] 三种克隆方式都失败：请检查地址是否存在、网络或 GitHub 镜像设置（「环境与更新」页可切换镜像）");
        return false;
    }

    private static bool LooksLikePlugin(string dir)
    {
        try
        {
            if (File.Exists(Path.Combine(dir, "__init__.py"))) return true;
            if (Directory.Exists(Path.Combine(dir, "src"))) return true;
            return Directory.EnumerateFiles(dir, "*.py").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void TryRemoveTree(string dir) => PathHelpers.TryDeleteDirectory(dir);
}
