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
        foreach (var (key, value) in profile.Permissions) permissions[key] = value;

        var agent = new JsonObject
        {
            ["description"] = profile.Description,
            ["mode"] = profile.Mode,
            ["temperature"] = profile.Temperature,
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
