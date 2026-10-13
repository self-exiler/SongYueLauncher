using System.Diagnostics;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 外部终端集成：在 Windows Terminal 中打开 ComfyUI 工作目录并激活当前 .venv。
/// 用 cmd + activate.bat（PowerShell 执行策略可能拦截 Activate.ps1）。
/// </summary>
public static class TerminalService
{
    public static bool OpenVenvTerminal(string comfyUiDir, Action<string>? onOutput = null)
    {
        var wt = ToolLocator.FindWindowsTerminal();
        if (wt is null)
        {
            onOutput?.Invoke("[错误] 未找到 Windows Terminal（wt.exe），请从 Microsoft Store 安装后重试");
            return false;
        }
        if (!VenvService.VenvExists(comfyUiDir))
        {
            onOutput?.Invoke("[错误] 尚未创建 .venv，请先创建虚拟环境");
            return false;
        }

        var activate = Path.Combine(comfyUiDir, ".venv", "Scripts", "activate.bat");
        if (!File.Exists(activate))
        {
            onOutput?.Invoke($"[错误] 未找到激活脚本：{activate}");
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = wt,
                Arguments = $"-d \"{comfyUiDir}\" cmd /k call \"{activate}\"",
                UseShellExecute = false,
            });
            return true;
        }
        catch (Exception ex)
        {
            onOutput?.Invoke($"[错误] 启动 Windows Terminal 失败：{ex.Message}");
            return false;
        }
    }
}
