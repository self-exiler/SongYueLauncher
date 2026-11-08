using System.Text.Json;
using AiDiagnostics;
using AiDiagnostics.Agents;
using AiDiagnostics.Parsing;
using AiDiagnostics.Runtime;

namespace AiDiagnostics.Test;

/// <summary>
/// opencode CLI 客户端契约测试（SRS v3.4 FR-5.8/5.9）：用内存命令桩模拟
/// <c>api model.list</c> 与 <c>run --format json</c> 的 NDJSON，不出网、不真的拉起 opencode。
/// </summary>
public class AiDiagnosticsClientTests
{
    private readonly StubRunner _runner = new();

    private AiDiagnosticsClient CreateClient(int modelListAttempts = 1) =>
        new(() => new OpenCodeOptions
        {
            // 桩注入后仍要显式给路径：ResolveExecutable 不应依赖测试机是否装了 opencode
            ExecutablePath = "opencode.exe",
            ModelListAttempts = modelListAttempts,
            ModelListRetryDelayMs = 0,
        }, _runner);

    private static AiAgentProfile Analyst => new()
    {
        Id = "analyst",
        Description = "Read-only analyst.",
        SystemPrompt = "只读分析日志。",
    };

    private static AiRequest Request(string modelId = "opencode/gpt", bool useTools = true) => new()
    {
        Prompt = "分析这段日志。",
        ModelId = modelId,
        Agent = Analyst,
        UseTools = useTools,
        Title = "日志分析",
        Instruction = "请直接作答。",
    };

    // ---------- api model.list ----------

    [Fact]
    public async Task ListModels_ParsesOpenCodeFormatAndFiltersDisabled()
    {
        _runner.OutputLines =
        [
            """{"location":{"directory":"C:\\tmp"},"data":[{"providerID":"opencode","id":"gpt","name":"GPT","enabled":true},{"providerID":"x","id":"off","enabled":false},{"providerID":"y","id":"inactive","status":"inactive"},{"providerID":"z","id":"plain"}]}""",
        ];

        var models = await CreateClient().ListModelsAsync();

        Assert.Equal(2, models.Count);
        Assert.Equal("opencode/gpt", models[0].QualifiedId);
        Assert.Equal("GPT (opencode/gpt)", models[0].DisplayName);
        Assert.Equal("z/plain", models[1].QualifiedId);
        Assert.Contains("api model.list", Assert.Single(_runner.Calls).Arguments);
    }

    [Fact]
    public async Task ListModels_EmptyList_RetriesUntilBudget()
    {
        _runner.Handler = _ => ([@"{""data"":[]}"], 0, string.Empty);

        var models = await CreateClient(modelListAttempts: 3).ListModelsAsync();

        Assert.Empty(models);
        Assert.Equal(3, _runner.Calls.Count);
    }

    [Fact]
    public async Task ListModels_StopsRetryingOnceProvidersLoaded()
    {
        _runner.Handler = call => call == 1
            ? ([@"{""data"":[]}"], 0, string.Empty)
            : ([@"{""data"":[{""providerID"":""p"",""id"":""m""}]}"], 0, string.Empty);

        var models = await CreateClient(modelListAttempts: 3).ListModelsAsync();

        Assert.Single(models);
        Assert.Equal(2, _runner.Calls.Count);
    }

    [Fact]
    public async Task ListModels_NonZeroExit_ThrowsWithDetail()
    {
        _runner.ExitCode = 2;
        _runner.StdErr = "provider not configured";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateClient().ListModelsAsync());

        Assert.Contains("退出码 2", ex.Message);
        Assert.Contains("provider not configured", ex.Message);
    }

    [Fact]
    public async Task ListModels_NonJsonOutput_ThrowsReadableError()
    {
        _runner.OutputLines = ["not json at all"];

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateClient().ListModelsAsync());

        Assert.Contains("非 JSON 输出", ex.Message);
    }

    // ---------- run --format json（NDJSON 流式） ----------

    [Fact]
    public async Task AskAsync_TextPartsAreJoinedAndStatusesAreSeparated()
    {
        _runner.OutputLines =
        [
            """{"type":"step_start"}""",
            """{"type":"text","part":{"text":"结论：显存不足"}}""",
            """{"type":"tool_use","part":{"tool":"websearch"}}""",
            """{"type":"text","part":{"text":"。步骤：加 --lowvram"}}""",
            """{"type":"step_finish"}""",
        ];

        var deltas = new List<string>();
        var statuses = new List<string>();
        var answer = await CreateClient().AskAsync(Request(), deltas.Add, statuses.Add);

        Assert.True(answer.Success);
        const string expected = "结论：显存不足\n\n。步骤：加 --lowvram";
        Assert.Equal(expected, answer.Text);
        Assert.Equal(expected, string.Concat(deltas)); // onDelta 拼接即最终正文
        Assert.Contains("模型思考中…", statuses);
        Assert.Contains("正在生成回答…", statuses);
        Assert.Contains("正在调用 websearch …", statuses);
        Assert.DoesNotContain("websearch", answer.Text);
    }

    [Fact]
    public async Task AskAsync_CumulativeTextEvents_OnlyEmitNewTail()
    {
        _runner.OutputLines =
        [
            """{"type":"text","part":{"text":"A"}}""",
            """{"type":"text","part":{"text":"AB"}}""",
            """{"type":"text","part":{"text":"ABC"}}""",
        ];

        var deltas = new List<string>();
        var answer = await CreateClient().AskAsync(Request(), deltas.Add);

        Assert.True(answer.Success);
        Assert.Equal("ABC", answer.Text);
        Assert.Equal("ABC", string.Concat(deltas));
    }

    [Fact]
    public async Task AskAsync_InStreamError_IsReportedAsFailure()
    {
        _runner.OutputLines =
        [
            """{"type":"text","part":{"text":"部分结论"}}""",
            """{"type":"error","error":{"message":"Model is unavailable"}}""",
        ];

        var answer = await CreateClient().AskAsync(Request());

        Assert.False(answer.Success);
        Assert.Contains("Model is unavailable", answer.Text);
        Assert.Contains("部分结论", answer.Text);
    }

    [Fact]
    public async Task AskAsync_NonJsonLogLines_NeverEnterAnswer()
    {
        _runner.OutputLines =
        [
            "INFO  spawning private server",
            """{"type":"text","part":{"text":"正文"}}""",
            """{"type":"tex""", // 被截断的半行
        ];

        var answer = await CreateClient().AskAsync(Request());

        Assert.True(answer.Success);
        Assert.Equal("正文", answer.Text);
    }

    [Fact]
    public async Task AskAsync_NonZeroExit_KeepsPartialAndTimeoutHint()
    {
        _runner.OutputLines = ["""{"type":"text","part":{"text":"部分结论"}}"""];
        _runner.ExitCode = -1;
        _runner.StdErr = "操作超时（限时 600s）。";

        var answer = await CreateClient().AskAsync(Request());

        Assert.False(answer.Success);
        Assert.Contains("退出码 -1", answer.Text);
        Assert.Contains("超时", answer.Text);
        Assert.Contains("部分结论", answer.Text);
        Assert.Contains("opencode models", answer.Text);
    }

    [Fact]
    public async Task AskAsync_EmptyOutput_FailsWithProviderHint()
    {
        var answer = await CreateClient().AskAsync(Request());

        Assert.False(answer.Success);
        Assert.Contains("没有返回可解析的回答内容", answer.Text);
        Assert.Contains("opencode models", answer.Text);
    }

    [Fact]
    public async Task AskAsync_NoModelSelected_FailsFast()
    {
        var answer = await CreateClient().AskAsync(Request(string.Empty));

        Assert.False(answer.Success);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task AskAsync_PromptGoesToStdin_AndWorkspaceIsIsolated()
    {
        await CreateClient().AskAsync(Request());

        var call = Assert.Single(_runner.Calls);
        Assert.Equal("分析这段日志。", call.StandardInput);
        Assert.StartsWith("run --standalone --format json", call.Arguments);
        Assert.EndsWith(".opencode", System.IO.Path.GetDirectoryName(call.Environment!["OPENCODE_CONFIG"])!);
        Assert.True(call.Environment!.ContainsKey("PWD"));
    }

    // ---------- 命令行与临时 agent 配置 ----------

    [Fact]
    public void BuildAskArguments_QuotesEveryToken()
    {
        var request = Request("provider/model with space");
        var args = AiDiagnosticsClient.BuildAskArguments(request);

        Assert.StartsWith("run --standalone --format json --agent \"analyst\"", args);
        Assert.Contains("--model \"provider/model with space\"", args);
        Assert.Contains("--title \"日志分析\"", args);
        Assert.EndsWith("\"请直接作答。\"", args);
    }

    [Fact]
    public void AgentConfig_DeniesEverythingExceptOptionalWebTools()
    {
        using var online = JsonDocument.Parse(AgentWorkspace.BuildConfigJson(Analyst, useTools: true));
        var permissions = online.RootElement.GetProperty("agent").GetProperty("analyst").GetProperty("permission");
        Assert.Equal("deny", permissions.GetProperty("*").GetString());
        Assert.Equal("deny", permissions.GetProperty("read").GetString());
        Assert.Equal("allow", permissions.GetProperty("websearch").GetString());
        Assert.Equal("allow", permissions.GetProperty("webfetch").GetString());
        Assert.Equal("只读分析日志。", online.RootElement.GetProperty("agent").GetProperty("analyst").GetProperty("system").GetString());

        using var offline = JsonDocument.Parse(AgentWorkspace.BuildConfigJson(Analyst, useTools: false));
        var offlinePermissions = offline.RootElement.GetProperty("agent").GetProperty("analyst").GetProperty("permission");
        Assert.Equal("deny", offlinePermissions.GetProperty("websearch").GetString());
        Assert.Equal("deny", offlinePermissions.GetProperty("webfetch").GetString());
    }

    [Fact]
    public void ParseAnswer_DropsTruncatedJsonLine()
    {
        var answer = RunEventParser.ParseAnswer(
            """{"type":"text","part":{"text":"OK"}}""" + "\n" + """{"type":"tex""");

        Assert.True(answer.Success);
        Assert.Equal("OK", answer.Text);
    }

    // ---------- 内存命令桩 ----------

    /// <summary>按调用序号返回输出行 / 退出码 / stderr，并记录每次调用的参数与 stdin / 环境变量。</summary>
    private sealed class StubRunner : ICommandRunner
    {
        public string[] OutputLines { get; set; } = [];
        public int ExitCode { get; set; }
        public string StdErr { get; set; } = string.Empty;
        public Func<int, (string[] Lines, int Code, string Err)>? Handler { get; set; }

        public List<Call> Calls { get; } = [];

        public Task<CommandResult> RunAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            int timeoutMs,
            Action<string>? onOutputLine,
            CancellationToken cancellationToken,
            string? standardInput = null,
            IDictionary<string, string>? environment = null)
        {
            Calls.Add(new Call(arguments, workingDirectory, standardInput, environment));
            var (lines, code, err) = Handler?.Invoke(Calls.Count) ?? (OutputLines, ExitCode, StdErr);
            if (onOutputLine is not null)
            {
                foreach (var line in lines) onOutputLine(line);
            }
            var stdout = lines.Length > 0 ? string.Join('\n', lines) + "\n" : string.Empty;
            return Task.FromResult(new CommandResult(code, stdout, err));
        }

        internal sealed record Call(
            string Arguments,
            string WorkingDirectory,
            string? StandardInput,
            IDictionary<string, string>? Environment);
    }
}
