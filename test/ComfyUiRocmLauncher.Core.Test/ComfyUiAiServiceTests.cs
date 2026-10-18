using ComfyUiRocmLauncher.Core.Ai;

namespace ComfyUiRocmLauncher.Core.Test;

/// <summary>
/// 错误日志抓取与提问组装（SRS FR-5.7）。全部经 <see cref="ComfyUiAiService"/> 门面断言——
/// 界面层看到的就是这些契约，Core.Test 不需要（也不应该）引用 AiDiagnostics。
/// </summary>
public class ComfyUiAiServiceTests
{
    [Fact]
    public void ExtractErrors_ErrorLineAndTracebackBlock_AreGrouped()
    {
        var lines = new[]
        {
            "[INFO] Loading model",
            "\u001b[31m[ERROR] HIP error: invalid device\u001b[0m",
            "Traceback (most recent call last):",
            "  File \"comfy/model_management.py\", line 10, in f",
            "    x = 1",
            "RuntimeError: HIP out of memory. Tried to allocate 2.00 GiB",
            "[INFO] normal output continues",
        };

        var result = ComfyUiAiService.ExtractErrors(lines);

        Assert.False(result.Truncated);
        Assert.Single(result.Blocks);
        var block = result.Blocks[0];
        Assert.Contains("[ERROR] HIP error: invalid device", block);
        Assert.Contains("Traceback (most recent call last):", block);
        Assert.Contains("RuntimeError: HIP out of memory", block);
        Assert.DoesNotContain("normal output continues", block);
        // ANSI 码必须被剥离（保留原始 ESC 与去色后的文本两种写法都判掉）
        Assert.DoesNotContain("\u001b[31m", block);
    }

    [Fact]
    public void ExtractErrors_NoError_ReturnsHintText()
    {
        var lines = new[] { "[INFO] Starting server", "To see the GUI go to: http://127.0.0.1:8188" };

        var result = ComfyUiAiService.ExtractErrors(lines);

        Assert.Empty(result.Blocks);
        Assert.Equal(0, result.ErrorLineCount);
        Assert.StartsWith("未在当前日志缓冲区", result.ToPreviewText());
    }

    [Fact]
    public void ExtractErrors_SeparateErrors_BecomeDistinctBlocks()
    {
        var lines = new[]
        {
            "[ERROR] first failure",
            "[INFO] unrelated heartbeat",
            "ValueError: second failure",
        };

        var result = ComfyUiAiService.ExtractErrors(lines);

        Assert.Equal(2, result.Blocks.Count);
        Assert.Equal(2, result.ErrorLineCount);
    }

    [Fact]
    public void ExtractErrors_ExceedsByteBudget_MarksTruncated()
    {
        // 每个错误行之间插入普通行，使其成为独立块，才能触发按块预算截断
        var lines = new List<string>();
        for (var i = 0; i < 400; i++)
        {
            lines.Add($"[ERROR] line {i} " + new string('x', 60));
            lines.Add("[INFO] heartbeat ok");
        }

        var result = ComfyUiAiService.ExtractErrors(lines, maxBytes: 2_000);

        Assert.True(result.Truncated);
        Assert.NotEmpty(result.Blocks);
        Assert.True(string.Join("\n", result.Blocks).Length < 400 * 75);
    }

    [Theory]
    [InlineData("[ERROR] boom", true)]
    [InlineData("Traceback (most recent call last):", true)]
    [InlineData("torch.OutOfMemoryError: CUDA out of memory", true)]
    [InlineData("Fatal Python error: Segmentation fault", true)]
    // 精简 ErrorMarkers 后仍需命中的项（由 "ERROR" 大小写不敏感匹配覆盖）
    [InlineData("ModuleNotFoundError: No module named 'torch'", true)]
    [InlineData("ImportError: cannot import name 'x' from 'y'", true)]
    [InlineData("[CRITICAL] out of memory", true)]
    // StopIteration 是唯一不含 error/exception 的保留关键字
    [InlineData("StopIteration", true)]
    [InlineData("To see the GUI go to: http://127.0.0.1:8188", false)]
    [InlineData("[INFO] Loaded 12 nodes", false)]
    public void LooksLikeError_ClassifiesMarkers(string line, bool expected) =>
        Assert.Equal(expected, ComfyUiAiService.LooksLikeError(line));

    [Fact]
    public void BuildPrompt_ContainsContextAndStructure()
    {
        var prompt = ComfyUiDiagnosis.BuildPrompt(
            "RuntimeError: HIP out of memory", @"C:\pack", @"C:\pack\.venv\Scripts\python.exe");

        Assert.Contains("ROCm", prompt);
        Assert.Contains(@"C:\pack", prompt);
        Assert.Contains("RuntimeError: HIP out of memory", prompt);
        Assert.Contains("```log", prompt);
        Assert.Contains("解决步骤", prompt);
    }
}
