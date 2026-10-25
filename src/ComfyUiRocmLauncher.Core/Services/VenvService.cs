using AiDiagnostics.Runtime;
using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// Python 虚拟环境创建（SRS FR-8.3）：uv venv --python 3.12（版本钉死），随后补装 setuptools / wheel / mpmath。
/// 全程经 TaskRunner；输出流式回 GUI。
/// </summary>
public static class VenvService
{
    /// <summary>venv 内 python.exe 路径。</summary>
    public static string VenvPython(string comfyUiDir) =>
        Path.Combine(comfyUiDir, ".venv", "Scripts", "python.exe");

    public static bool VenvExists(string comfyUiDir) => File.Exists(VenvPython(comfyUiDir));

    public static async Task<bool> CreateAsync(TaskRunner runner, AppConfig config, Action<string>? onOutput = null)
    {
        var uv = ToolLocator.ResolveUv(config);
        if (string.IsNullOrWhiteSpace(config.ComfyUiDir))
        {
            onOutput?.Invoke("[错误] 未设置 ComfyUI 目录（主页）");
            return false;
        }

        var dir = config.ComfyUiDir;

        onOutput?.Invoke("[1/2] uv venv --python 3.12 …");
        var code = await runner.Run(uv, $"venv --python 3.12 {CommandLine.Quote(Path.Combine(dir, ".venv"))}", dir, onOutput)
            .ConfigureAwait(false);
        if (code != 0)
        {
            onOutput?.Invoke($"[错误] 创建 venv 失败（退出码 {code}）");
            return false;
        }

        var python = VenvPython(dir);
        onOutput?.Invoke("[2/2] 补装构建工具链 setuptools / wheel / mpmath …");
        var arg = $"pip install setuptools wheel mpmath --python {CommandLine.Quote(python)}";
        if (!string.IsNullOrEmpty(config.EnvUpdate.PypiMirror))
            arg += " " + MirrorPolicy.PypiIndexArg(config.EnvUpdate.PypiMirror).TrimEnd();
        code = await runner.Run(uv, arg, dir, onOutput).ConfigureAwait(false);

        // venv 本体已创建（python.exe 存在）就必须写入配置（审查 #17）：
        // setuptools 失败（镜像抖动常见）时不写 PythonExe，后续所有轮子操作都会报「venv 未就绪」，
        // 重试永远卡在同一步——构建工具链可以之后单独补装
        if (File.Exists(python)) config.PythonExe = python;

        if (code == 0)
        {
            onOutput?.Invoke($"√ venv 就绪：{python}");
        }
        else
        {
            onOutput?.Invoke($"[警告] 构建工具链安装失败（退出码 {code}），venv 已创建；解释器已写入配置，可稍后重试");
        }
        return File.Exists(python);
    }
}
