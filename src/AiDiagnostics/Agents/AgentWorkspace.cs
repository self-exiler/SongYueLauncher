namespace AiDiagnostics.Agents;

using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// 每次分析用的临时 opencode 项目目录：写出一份只含自定义 agent 的 <c>opencode.json</c>，
/// 并把它作为子进程工作目录。
/// </summary>
/// <remarks>
/// OpenCode v2 用 <c>process.env.PWD ?? process.cwd()</c> 解析项目根，父进程遗留的 PWD 会让
/// WorkingDirectory 失效，故必须同时通过环境变量覆盖 <c>PWD</c> 与 <c>OPENCODE_CONFIG</c>。
/// </remarks>
internal sealed class AgentWorkspace : IDisposable
{
    private const string SchemaUrl = "https://opencode.ai/config.json";

    /// <summary>opencode agent 模式：primary 表示可直接被 <c>run --agent</c> 选中。</summary>
    private const string AgentMode = "primary";

    /// <summary>采样温度：故障排查要稳定复现，取低值。</summary>
    private const double AgentTemperature = 0.1;

    /// <summary>
    /// 权限矩阵：默认拒绝一切工具，仅放行读文件、抓网页与搜索。
    /// 分析任务只给结论与步骤，不改代码、不执行命令——这条边界固定写在库内，不开放覆写。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AgentPermissions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["*"] = "deny",
            ["read"] = "allow",
            ["webfetch"] = "allow",
            ["websearch"] = "allow",
        };

    private AgentWorkspace(string root, string configPath)
    {
        Root = root;
        ConfigPath = configPath;
    }

    /// <summary>临时项目根（子进程工作目录）。</summary>
    public string Root { get; }

    /// <summary>临时 opencode.json 的完整路径（写入 OPENCODE_CONFIG）。</summary>
    public string ConfigPath { get; }

    public static AgentWorkspace Create(AiAgentProfile profile)
    {
        var root = Path.Combine(Path.GetTempPath(), "ai_diagnostics", Guid.NewGuid().ToString("N"));
        var opencodeDirectory = Path.Combine(root, ".opencode");
        Directory.CreateDirectory(opencodeDirectory);
        var configPath = Path.Combine(opencodeDirectory, "opencode.json");
        File.WriteAllText(configPath, BuildConfigJson(profile));
        return new AgentWorkspace(root, configPath);
    }

    /// <summary>把 agent 画像序列化成 opencode 配置（顶层 <c>agent</c>、agent 内 <c>permission</c>，均为单数）。</summary>
    internal static string BuildConfigJson(AiAgentProfile profile)
    {
        var permissions = new JsonObject();
        foreach (var (key, value) in AgentPermissions) permissions[key] = value;

        var agent = new JsonObject
        {
            ["description"] = profile.Description,
            ["mode"] = AgentMode,
            ["temperature"] = AgentTemperature,
            ["system"] = profile.SystemPrompt,
            ["permission"] = permissions,
        };

        var root = new JsonObject
        {
            ["$schema"] = SchemaUrl,
            ["agent"] = new JsonObject { [profile.Id] = agent },
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
