using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 启动环境变量合成（SRS FR-3）：固定注入层 + 自定义层；GPU 模板变量在应用时已展开写入自定义层。
/// </summary>
public static class LaunchEnvBuilder
{
    /// <summary>固定注入层变量，自定义层不得覆盖。</summary>
    public static readonly string[] FixedKeys = { "PYTHONUTF8", "PYTHONNOUSERSITE", "PATH", "HIP_PATH", "ROCM_PATH" };

    /// <summary>venv 目录：python.exe 位于 &lt;venv&gt;\Scripts\python.exe。</summary>
    public static string VenvDirFrom(string pythonExe)
    {
        var scripts = Path.GetDirectoryName(pythonExe) ?? string.Empty;
        return Path.GetDirectoryName(scripts) ?? scripts;
    }

    /// <summary>&lt;venv&gt;\Scripts\rocm-sdk.exe</summary>
    public static string RocmSdkExeFrom(string pythonExe) =>
        Path.Combine(Path.GetDirectoryName(pythonExe) ?? string.Empty, "rocm-sdk.exe");

    /// <summary>
    /// 合成子进程环境。rocmRoot 为 rocm-sdk path --root 的输出，可为 null（缺失时跳过推导）。
    /// </summary>
    public static Dictionary<string, string> Build(
        AppConfig config,
        string? rocmRoot,
        out IReadOnlyList<string> conflicts)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var venvDir = VenvDirFrom(config.PythonExe);
        var scriptsDir = Path.GetDirectoryName(config.PythonExe) ?? string.Empty;

        env["PYTHONUTF8"] = "1";
        env["PYTHONNOUSERSITE"] = "1";

        var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        env["PATH"] = string.Join(';', new[] { venvDir, scriptsDir, existingPath }
            .Where(p => !string.IsNullOrWhiteSpace(p)));

        if (!string.IsNullOrWhiteSpace(rocmRoot))
        {
            env["HIP_PATH"] = rocmRoot;
            env["ROCM_PATH"] = rocmRoot;
        }

        var conflictList = new List<string>();
        foreach (var kv in config.CustomEnvVars)
        {
            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
            if (FixedKeys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                conflictList.Add(kv.Key);
                continue;
            }
            env[kv.Key] = kv.Value;
        }

        conflicts = conflictList;
        return env;
    }

    /// <summary>确保 TRITON_CACHE_DIR / PYTORCH_TUNABLEOP_CACHE_DIR 目录存在（FR-3.4 步骤 3）。</summary>
    public static IReadOnlyList<string> EnsureCacheDirs(string comfyUiDir, IDictionary<string, string> env)
    {
        var created = new List<string>();
        if (string.IsNullOrWhiteSpace(comfyUiDir)) return created;

        if (!env.TryGetValue("TRITON_CACHE_DIR", out var triton) || string.IsNullOrWhiteSpace(triton))
        {
            triton = Path.Combine(comfyUiDir, "triton-cache");
            env["TRITON_CACHE_DIR"] = triton;
        }
        if (!env.TryGetValue("PYTORCH_TUNABLEOP_CACHE_DIR", out var tunable) || string.IsNullOrWhiteSpace(tunable))
        {
            tunable = Path.Combine(comfyUiDir, "tunableop-cache");
            env["PYTORCH_TUNABLEOP_CACHE_DIR"] = tunable;
        }

        foreach (var dir in new[] { triton, tunable })
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                created.Add(dir);
            }
        }
        return created;
    }
}
