using System.Text.Json;
using ComfyUiRocmLauncher.Core.Services;

namespace ComfyUiRocmLauncher.Core.Test;

public class OpenCodeClientTests
{
    private const string ModelListJson = """
        {"location":{"directory":"C:\\\\Users\\\\me"},"data":[
          {"id":"claude-haiku-5-5","modelID":"claude-haiku-5-5","providerID":"opencode","name":"Claude Haiku 5.5","enabled":true,"status":"active"},
          {"id":"gpt-6-sol","modelID":"gpt-6-sol","providerID":"openai","name":"GPT-6 Sol","enabled":true},
          {"id":"retired-model","modelID":"retired-model","providerID":"opencode","name":"Retired","enabled":false},
          {"id":"no-name","modelID":"no-name","providerID":"opencode"}
        ]}
        """;

    [Fact]
    public void ParseModels_SkipsDisabledAndKeepsQualifiedId()
    {
        var models = OpenCodeClient.ParseModels(ModelListJson);

        Assert.Equal(3, models.Count);
        Assert.Equal("opencode/claude-haiku-5-5", models[0].QualifiedId);
        Assert.Contains("Claude Haiku 5.5", models[0].DisplayName);
        Assert.DoesNotContain(models, m => m.ModelId == "retired-model");
        // 无 name 时回落为 id
        Assert.Equal("no-name", models[2].DisplayName);
    }

    [Fact]
    public void ParseModels_EmptyDataArray_ReturnsEmpty() =>
        Assert.Empty(OpenCodeClient.ParseModels("""{"data":[]}"""));

    [Fact]
    public void ParseRunOutput_TextEvents_AreJoinedInOrder()
    {
        var stdout = string.Join('\n',
            """{"type":"step_start","sessionID":"ses_1","part":{"type":"step-start"}}""",
            """{"type":"text","sessionID":"ses_1","part":{"type":"text","text":"结论：显存不足"}}""",
            """{"type":"text","sessionID":"ses_1","part":{"type":"text","text":"步骤：加 --lowvram"}}""");

        var answer = OpenCodeClient.ParseRunOutput(stdout);

        Assert.True(answer.Success);
        Assert.Equal("结论：显存不足\n\n步骤：加 --lowvram", answer.Text);
    }

    [Fact]
    public void ParseRunOutput_ErrorEvent_IsReportedAsFailure()
    {
        var stdout = """{"type":"error","sessionID":"ses_2","error":{"type":"provider.no-route","message":"Model unavailable: bad/id"}}""";

        var answer = OpenCodeClient.ParseRunOutput(stdout);

        Assert.False(answer.Success);
        Assert.Equal("Model unavailable: bad/id", answer.Text);
    }

    [Fact]
    public void ParseRunOutput_NonJsonLines_AreIgnored()
    {
        var stdout = "Loading...\nnot json at all\n\n";

        var answer = OpenCodeClient.ParseRunOutput(stdout);

        Assert.False(answer.Success);
        Assert.Contains("没有返回可解析的回答", answer.Text);
    }

    [Fact]
    public void BuildAskArguments_UsesStandaloneAndQuotedCustomAgent()
    {
        var args = OpenCodeClient.BuildAskArguments("openai/gpt-6-sol");

        Assert.StartsWith("run --standalone --format json --agent ", args);
        Assert.Contains("--agent \"comfyui-error-analyst\"", args);
        Assert.Contains("--model \"openai/gpt-6-sol\"", args);
        Assert.Contains("--title \"ComfyUI 错误日志分析\"", args);
        Assert.Contains("stdin 附加", args);
    }

    [Fact]
    public void ParseRunOutput_ErrorEvent_TakesPrecedenceOverPartialText()
    {
        var stdout = string.Join('\n',
            """{"type":"text","sessionID":"ses_3","part":{"type":"text","text":"部分回答"}}""",
            """{"type":"error","sessionID":"ses_3","error":{"type":"provider.transport","message":"provider timeout"}}""");

        var answer = OpenCodeClient.ParseRunOutput(stdout);

        Assert.False(answer.Success);
        Assert.Contains("provider timeout", answer.Text);
        Assert.Contains("部分回答", answer.Text);
    }

    [Fact]
    public void TryFormatRunEvent_ConvertsUsefulEvents()
    {
        Assert.Equal("结论：显存不足", OpenCodeClient.TryFormatRunEvent(
            """{"type":"text","part":{"type":"text","text":"结论：显存不足"}}"""));
        Assert.Equal("opencode 已开始处理请求。", OpenCodeClient.TryFormatRunEvent(
            """{"type":"step_start","part":{"type":"step-start"}}"""));
        Assert.Equal("Model unavailable: bad/id", OpenCodeClient.TryFormatRunEvent(
            """{"type":"error","error":{"type":"provider.no-route","message":"Model unavailable: bad/id"}}"""));
        Assert.Null(OpenCodeClient.TryFormatRunEvent(
            """{"type":"unknown","part":{"type":"unknown"}}"""));
    }

    [Fact]
    public void AnalystAgentResource_IsAvailableForTemporaryProjectConfig()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Agents", "comfyui-error-analyst.json");

        Assert.True(File.Exists(path));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var agent = doc.RootElement.GetProperty("agent").GetProperty("comfyui-error-analyst");
        var permission = agent.GetProperty("permission");

        Assert.Equal("primary", agent.GetProperty("mode").GetString());
        Assert.Equal("deny", permission.GetProperty("*").GetString());
        Assert.Equal("allow", permission.GetProperty("read").GetString());
        Assert.Equal("allow", permission.GetProperty("webfetch").GetString());
        Assert.Equal("allow", permission.GetProperty("websearch").GetString());
        Assert.DoesNotContain(agent.GetProperty("system").GetString() ?? string.Empty, "不访问网络", StringComparison.Ordinal);
    }

    [Fact]
    public void Locate_FindsInstalledOpenCodeOrNull()
    {
        // 纯本机探测：有则应为已存在的 exe，无则 null（不抛异常即符合契约）
        var path = OpenCodeClient.Locate();
        if (path is not null)
        {
            Assert.True(File.Exists(path));
            Assert.Contains("opencode", Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Null(ToolLocator.FindOpenCode());
        }
    }
}
