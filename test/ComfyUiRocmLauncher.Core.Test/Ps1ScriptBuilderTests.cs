using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;

namespace ComfyUiRocmLauncher.Core.Test;

public class Ps1ScriptBuilderTests
{
    private static AppConfig Config() => new()
    {
        ComfyUiDir = @"D:\comfy",
        PythonExe = @"D:\comfy\.venv\Scripts\python.exe",
        CustomEnvVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MIOPEN_LOG_LEVEL"] = "0",
        },
    };

    [Fact]
    public void Build_EmitsPathsAndFixedLayer()
    {
        var ps1 = Ps1ScriptBuilder.Build(Config(), new DateTime(2026, 10, 17, 22, 0, 0), Array.Empty<string>());

        Assert.Contains("$ComfyDir  = 'D:\\comfy'", ps1);
        Assert.Contains("$PythonExe = 'D:\\comfy\\.venv\\Scripts\\python.exe'", ps1);
        Assert.Contains("$env:PYTHONUTF8 = '1'", ps1);
        Assert.Contains("$env:PYTHONNOUSERSITE = '1'", ps1);
        // venv 与 Scripts 前置到现有 PATH，而不是烘焙当前机器的完整 PATH
        Assert.Contains("$env:PATH = 'D:\\comfy\\.venv;D:\\comfy\\.venv\\Scripts;' + $env:PATH", ps1);
        Assert.Contains("2026-10-17 22:00", ps1);
    }

    [Fact]
    public void Build_EmitsRocmSdkStepOnlyAsRuntimeDerivation()
    {
        var ps1 = Ps1ScriptBuilder.Build(Config(), DateTime.Now, Array.Empty<string>());

        Assert.Contains("rocm-sdk", ps1);
        Assert.Contains("path --root", ps1);
        Assert.Contains("$env:HIP_PATH = $rocmRoot", ps1);
        // 不得把 HIP_PATH / ROCM_PATH 写成字面量
        Assert.DoesNotContain("$env:HIP_PATH = '", ps1);
    }

    [Fact]
    public void Build_EmitsCustomEnvAndEscapesSingleQuotes()
    {
        var cfg = Config();
        cfg.CustomEnvVars["PATH_EXTRA"] = "a'b";
        cfg.CustomEnvVars["MY-VAR"] = "1"; // 非标识符字符 → 用 ${env:...} 形式

        var ps1 = Ps1ScriptBuilder.Build(cfg, DateTime.Now, Array.Empty<string>());

        Assert.Contains("$env:MIOPEN_LOG_LEVEL = '0'", ps1);
        Assert.Contains("$env:PATH_EXTRA = 'a''b'", ps1);
        Assert.Contains("${env:MY-VAR} = '1'", ps1);
    }

    [Fact]
    public void Build_EmitsArgsArrayInOrder()
    {
        var ps1 = Ps1ScriptBuilder.Build(
            Config(), DateTime.Now, new[] { "--listen", "127.0.0.1", "--port", "8188" });

        Assert.Contains("$launchArgs = @(", ps1);
        Assert.Contains("    '--listen',", ps1);
        Assert.Contains("    '127.0.0.1',", ps1);
        Assert.Contains("    '--port',", ps1);
        Assert.Contains("    '8188'", ps1); // 末项无逗号，由 LastArgHasNoTrailingComma 单独断言
        Assert.Contains("& $PythonExe main.py @launchArgs", ps1);
    }

    [Fact]
    public void Build_LastArgHasNoTrailingComma()
    {
        // 末项带逗号会让 PowerShell 报「',' 后面缺少表达式」——实测解析器抓到的真缺陷
        var ps1 = Ps1ScriptBuilder.Build(Config(), DateTime.Now, new[] { "--port", "8188" })
            .Replace("\r\n", "\n");

        Assert.Contains("    '8188'\n)", ps1);
        Assert.DoesNotContain("    '8188',\n)", ps1);
    }

    [Fact]
    public void Build_NoArgs_EmitsEmptyArray()
    {
        var ps1 = Ps1ScriptBuilder.Build(Config(), DateTime.Now, Array.Empty<string>());

        Assert.Contains("$launchArgs = @()", ps1);
        Assert.Contains("& $PythonExe main.py @launchArgs", ps1);
    }

    [Fact]
    public void Build_ConflictingCustomVar_IsDroppedAndNoted()
    {
        var cfg = Config();
        cfg.CustomEnvVars["HIP_PATH"] = @"C:\fake";

        var ps1 = Ps1ScriptBuilder.Build(cfg, DateTime.Now, Array.Empty<string>());

        Assert.DoesNotContain(@"C:\fake", ps1);
        Assert.Contains("已按启动器规则忽略", ps1);
    }

    [Fact]
    public void Build_GithubMirror_InjectsEndpoint()
    {
        var cfg = Config();
        cfg.EnvUpdate.GithubMirror = "https://ghproxy.example/";

        var ps1 = Ps1ScriptBuilder.Build(cfg, DateTime.Now, Array.Empty<string>());

        Assert.Contains("GITHUB_ENDPOINT", ps1);
    }

    [Fact]
    public void Build_CacheDirsFallBackToComfyDir()
    {
        var ps1 = Ps1ScriptBuilder.Build(Config(), DateTime.Now, Array.Empty<string>());

        Assert.Contains("$env:TRITON_CACHE_DIR = Join-Path $ComfyDir 'triton-cache'", ps1);
        Assert.Contains("$env:PYTORCH_TUNABLEOP_CACHE_DIR = Join-Path $ComfyDir 'tunableop-cache'", ps1);
        Assert.Contains("New-Item -ItemType Directory", ps1);
    }
}
