namespace AiDiagnostics.Agents;

/// <summary>常用权限矩阵预设（opencode <c>permission</c> 字段的取值）。</summary>
public static class AiPermissions
{
    /// <summary>
    /// 只读 + 联网查证：默认拒绝一切工具，仅允许读取文件、抓取网页与搜索。
    /// 适合「分析日志、给建议，但不改代码、不执行命令」的咨询型任务。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ReadOnlyWeb =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["*"] = "deny",
            ["read"] = "allow",
            ["webfetch"] = "allow",
            ["websearch"] = "allow",
        };

    /// <summary>完全只读：连联网能力也不开放，适合离线/内网环境。</summary>
    public static readonly IReadOnlyDictionary<string, string> ReadOnly =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["*"] = "deny",
            ["read"] = "allow",
        };
}
