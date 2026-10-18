using System.Text;
using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 把当前配置导出为可直接运行的 PowerShell 启动脚本（配置管理页「导出启动脚本」）。
/// 与真实启动同源：固定注入层、自定义层、GitHub 镜像注入、缓存目录、结构化参数 + 额外参数。
/// 与启动器有意不同之处：
/// <list type="bullet">
/// <item>PATH 不烘焙成固定值，而是脚本内把 venv 与 Scripts 前置到当前 PATH，换机器/换盘符仍可用；</item>
/// <item>HIP_PATH / ROCM_PATH 由脚本自己执行 rocm-sdk 推导，而不是写字面量。</item>
/// </list>
/// </summary>
public static class Ps1ScriptBuilder
{
    /// <summary>导出文件名建议值（不含目录）。</summary>
    public static string SuggestedFileName => "comfyui-launch.ps1";

    /// <summary>生成脚本正文。args 为空时自动按当前配置解析（ArgsBuilder + 内置参数清单）。</summary>
    public static string Build(AppConfig config, DateTime generatedAt, IReadOnlyList<string>? args = null)
    {
        var tokens = args ?? ArgsBuilder.Build(config.Args, CliArgsCatalog.Instance, config.ExtraArgs);
        var env = LaunchEnvBuilder.Build(config, rocmRoot: null, out var conflicts);

        var comfyDir = config.ComfyUiDir;
        var venvDir = LaunchEnvBuilder.VenvDirFrom(config.PythonExe);
        var scriptsDir = Path.GetDirectoryName(config.PythonExe) ?? string.Empty;
        var rocmSdk = LaunchEnvBuilder.RocmSdkExeFrom(config.PythonExe);

        var sb = new StringBuilder();
        sb.AppendLine("# =====================================================================");
        sb.AppendLine("#  由「松月启动器」导出 —— " + generatedAt.ToString("yyyy-MM-dd HH:mm"));
        sb.AppendLine("#  内容与导出时的启动器配置一致：路径 / 环境变量 / 启动参数。");
        sb.AppendLine("#  用法：右键「使用 PowerShell 运行」，或在 PowerShell 里执行本文件。");
        sb.AppendLine("# =====================================================================");
        sb.AppendLine();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine();
        sb.AppendLine("$ComfyDir  = " + Ps(comfyDir));
        sb.AppendLine("$PythonExe = " + Ps(config.PythonExe));
        sb.AppendLine("$RocmSdk   = " + Ps(rocmSdk));
        sb.AppendLine();
        sb.AppendLine("if (-not (Test-Path -LiteralPath $PythonExe)) { throw \"未找到 Python 解释器：$PythonExe\" }");
        sb.AppendLine("if (-not (Test-Path -LiteralPath (Join-Path $ComfyDir 'main.py'))) { throw \"未找到 main.py：$ComfyDir\" }");
        sb.AppendLine();
        sb.AppendLine("Set-Location -LiteralPath $ComfyDir");
        sb.AppendLine();
        sb.AppendLine("# ---------- 固定注入层（自定义环境变量不允许覆盖） ----------");
        sb.AppendLine("$env:PYTHONUTF8 = '1'");
        sb.AppendLine("$env:PYTHONNOUSERSITE = '1'");
        sb.AppendLine("$env:PATH = " + Ps(Join(venvDir, scriptsDir)) + " + $env:PATH");
        sb.AppendLine();
        sb.AppendLine("# ---------- rocm-sdk：初始化与路径推导（失败仅警告，不阻断） ----------");
        sb.AppendLine("if (Test-Path -LiteralPath $RocmSdk) {");
        sb.AppendLine("    & $RocmSdk init 2>&1 | Out-Null");
        sb.AppendLine("    if ($LASTEXITCODE -ne 0) { Write-Warning 'rocm-sdk init 失败（忽略，继续启动）' }");
        sb.AppendLine();
        sb.AppendLine("    $rocmRoot = (& $RocmSdk path --root) -join ''");
        sb.AppendLine("    if ($LASTEXITCODE -eq 0 -and $rocmRoot) {");
        sb.AppendLine("        $env:HIP_PATH = $rocmRoot");
        sb.AppendLine("        $env:ROCM_PATH = $rocmRoot");
        sb.AppendLine("    }");
        sb.AppendLine("    else { Write-Warning 'rocm-sdk path --root 失败（忽略，继续启动）' }");
        sb.AppendLine("}");
        sb.AppendLine("else { Write-Warning \"未找到 $RocmSdk，跳过 rocm-sdk 初始化\" }");

        var custom = env
            .Where(kv => !IsFixedLayer(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (custom.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# ---------- 自定义环境变量（GPU 模板变量在应用模板时已展开写入） ----------");
            foreach (var kv in custom)
                sb.AppendLine(EnvAssignment(kv.Key, kv.Value));
        }

        sb.AppendLine();
        sb.AppendLine("# ---------- 缓存目录（未自定义时落到 ComfyUI 目录下） ----------");
        sb.AppendLine("if (-not $env:TRITON_CACHE_DIR) { $env:TRITON_CACHE_DIR = Join-Path $ComfyDir 'triton-cache' }");
        sb.AppendLine("if (-not $env:PYTORCH_TUNABLEOP_CACHE_DIR) { $env:PYTORCH_TUNABLEOP_CACHE_DIR = Join-Path $ComfyDir 'tunableop-cache' }");
        sb.AppendLine("foreach ($cacheDir in @($env:TRITON_CACHE_DIR, $env:PYTORCH_TUNABLEOP_CACHE_DIR)) {");
        sb.AppendLine("    if (-not (Test-Path -LiteralPath $cacheDir)) { New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("# ---------- 启动参数（结构化参数 + 额外参数，顺序与启动器一致） ----------");
        if (tokens.Count == 0)
        {
            sb.AppendLine("$launchArgs = @()");
        }
        else
        {
            sb.AppendLine("$launchArgs = @(");
            for (var i = 0; i < tokens.Count; i++)
            {
                // 末项不能带逗号：PowerShell 数组字面量里 `'x',\n)` 会报「',' 后面缺少表达式」
                sb.AppendLine("    " + Ps(tokens[i]) + (i == tokens.Count - 1 ? string.Empty : ","));
            }
            sb.AppendLine(")");
        }
        sb.AppendLine();
        sb.AppendLine("& $PythonExe main.py @launchArgs");
        sb.AppendLine("exit $LASTEXITCODE");

        if (conflicts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# 注意：以下自定义环境变量与固定注入层冲突，已按启动器规则忽略：");
            sb.AppendLine("#   " + string.Join(", ", conflicts));
        }

        return sb.ToString();
    }

    /// <summary>固定注入层由脚本显式处理（PATH 需前置、HIP/ROCM 需运行时推导），不走字面量赋值。</summary>
    /// <summary>固定层键（由 <see cref="LaunchEnvBuilder"/> 写入，导出时跳过以免重复）。</summary>
    private static bool IsFixedLayer(string key) =>
        LaunchEnvBuilder.FixedKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>venv 与 Scripts 前置到现有 PATH。</summary>
    private static string Join(string venvDir, string scriptsDir) =>
        string.Join(';', new[] { venvDir, scriptsDir }.Where(p => !string.IsNullOrWhiteSpace(p))) + ";";

    /// <summary>PowerShell 单引号字面量：内部单引号写成两个。</summary>
    private static string Ps(string value) => "'" + (value ?? string.Empty).Replace("'", "''") + "'";

    /// <summary>`$env:NAME = '...'`；名字含非标识符字符时用 ${env:NAME} 形式。</summary>
    private static string EnvAssignment(string name, string value)
    {
        var safe = name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c == '_');
        return (safe ? "$env:" + name : "${env:" + name + "}") + " = " + Ps(value);
    }
}
