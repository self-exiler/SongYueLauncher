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
