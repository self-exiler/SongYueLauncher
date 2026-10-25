using ComfyUiRocmLauncher.Core.Ai;

namespace ComfyUiRocmLauncher.Core.Test;

/// <summary>
/// 错误日志抓取、三类归并与提问组装（SRS v2.0 FR-5.7/5.11）。全部经 <see cref="ComfyUiAiService"/> 门面断言——
/// 界面层看到的就是这些契约，Core.Test 不需要（也不应该）引用 AiDiagnostics。
/// </summary>
public class ComfyUiAiServiceTests
{
    [Fact]
    public void ExtractErrors_ErrorLineAndTraceback_AreDistinctGroups()
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
        Assert.Equal(2, result.Groups.Count);
        // 分组顺序 = 扫描器优先序：解释器 → error → warning
        Assert.Equal(ErrorGroupKind.PythonInterpreter, result.Groups[0].Kind);
        Assert.Equal(ErrorGroupKind.Error, result.Groups[1].Kind);
        Assert.Contains("RuntimeError: HIP out of memory", result.Groups[0].RepresentativeText);
        Assert.DoesNotContain("normal output continues", result.Groups[0].RepresentativeText);
        // ANSI 码必须被剥离
        Assert.DoesNotContain("\u001b[31m", result.Groups[1].RepresentativeText);
        Assert.Equal(5, result.ErrorLineCount); // 1 error 行 + 4 行 Traceback 块
    }

    [Fact]
    public void ExtractErrors_WarningsAreClassifiedSeparately()
    {
        var lines = new[]
        {
            "WARNING: error sending statistics",
            "[ERROR] real failure",
        };

        var result = ComfyUiAiService.ExtractErrors(lines);

        Assert.Equal(2, result.Groups.Count);
        // 分组顺序 = 扫描器优先序：error 在 warning 前
        Assert.Equal(ErrorGroupKind.Error, result.Groups[0].Kind);
        Assert.Equal(ErrorGroupKind.Warning, result.Groups[1].Kind);
        Assert.Equal(1, result.WarningLineCount);
        Assert.Equal(1, result.ErrorLineCount);
    }

    [Fact]
    public void ExtractErrors_NoError_ReturnsHintText()
    {
        var lines = new[] { "[INFO] Starting server", "To see the GUI go to: http://127.0.0.1:8188" };

        var result = ComfyUiAiService.ExtractErrors(lines);

        Assert.Empty(result.Groups);
        Assert.Equal(0, result.ErrorLineCount);
        Assert.Contains("未识别到错误材料", result.ToSummaryText());
    }

    [Fact]
    public void ExtractErrors_RepeatedErrors_MergeIntoOneGroup()
    {
        var lines = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            lines.Add($"[ERROR] HIP error: invalid device function at node {i}");
            lines.Add("[INFO] unrelated heartbeat");
        }

        var result = ComfyUiAiService.ExtractErrors(lines);

        var group = Assert.Single(result.Groups);
        Assert.Equal(10, group.Count);
        Assert.Equal(0, group.FirstLine);
    }

    [Fact]
    public void ExtractErrors_ExceedsByteBudget_MarksTruncated()
    {
        // 互不同型的 error（归一后指纹各异），才能撑满预算触发按组丢弃
        var lines = new List<string>();
        for (var i = 0; i < 400; i++)
        {
            var tag = $"{(char)('a' + i / 26)}{(char)('a' + i % 26)}";
            lines.Add($"[ERROR] line {tag} " + new string('x', 60));
            lines.Add("[INFO] heartbeat ok");
        }

        var result = ComfyUiAiService.ExtractErrors(lines, maxBytes: 2_000);

        Assert.True(result.Truncated);
        Assert.NotEmpty(result.Groups);
        Assert.True(result.DroppedBlockCount > 0);
        Assert.True(result.Groups.Sum(g => g.RepresentativeText.Length) < 400 * 75);
    }

    [Fact]
    public void BuildPrompt_SectionsByKindAndContainsContext()
    {
        var extraction = ComfyUiAiService.ExtractErrors(new[]
        {
            "WARNING: benign signal 1",
            "[ERROR] HIP error: invalid device",
            "Traceback (most recent call last):",
            "RuntimeError: HIP out of memory",
        });

        var prompt = ComfyUiDiagnosis.BuildPrompt(extraction, @"C:\pack", @"C:\pack\.venv\Scripts\python.exe");

        Assert.Contains("ROCm", prompt);
        Assert.Contains(@"C:\pack", prompt);
        Assert.Contains("HIP out of memory", prompt);
        Assert.Contains("HIP error: invalid device", prompt);
        // 分节结构（SRS v2.0 FR-5.7）：解释器 / error / warning 摘要
        Assert.Contains("## Python 解释器报错", prompt);
        Assert.Contains("## error 级日志", prompt);
        Assert.Contains("## warning 级日志摘要", prompt);
        Assert.Contains("```log", prompt);
        Assert.Contains("解决步骤", prompt);
    }
}
