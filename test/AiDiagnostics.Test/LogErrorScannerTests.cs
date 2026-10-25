using AiDiagnostics.Logs;

namespace AiDiagnostics.Test;

/// <summary>三类化扫描与指纹归并（SRS v2.0 FR-5.7/5.11）。</summary>
public class LogErrorScannerTests
{
    private static LogBlock[] Scan(params string[] lines) => LogErrorScanner.Scan(lines).Blocks.ToArray();

    // ---------- 三类分类 ----------

    [Fact]
    public void Scan_TracebackBlock_IsPythonInterpreter()
    {
        var blocks = Scan(
            "Traceback (most recent call last):",
            "  File \"comfy/main.py\", line 10, in f",
            "RuntimeError: HIP out of memory");

        var block = Assert.Single(blocks);
        Assert.Equal(LogErrorKind.PythonInterpreter, block.Kind);
        Assert.Equal(0, block.StartLine);
        Assert.Contains("RuntimeError", block.Text);
    }

    [Fact]
    public void Scan_BareExceptionTail_IsPythonInterpreter()
    {
        var blocks = Scan("torch.OutOfMemoryError: CUDA out of memory", "[INFO] ok");

        var block = Assert.Single(blocks);
        Assert.Equal(LogErrorKind.PythonInterpreter, block.Kind);
    }

    [Fact]
    public void Scan_WarningPrefix_IsWarning_NotError()
    {
        var blocks = Scan("WARNING: error sending statistics", "[ERROR] real failure");

        Assert.Equal(2, blocks.Length);
        // 输出按「解释器 → error → warning」优先序排列（FR-5.7），不按行序
        Assert.Equal(LogErrorKind.Error, blocks[0].Kind);
        Assert.Equal(LogErrorKind.Warning, blocks[1].Kind);
    }

    [Fact]
    public void Scan_WarningPrefixRequiresColonOrSpace()
    {
        // "WARNINGX" 不是前缀命中；句中含 ERROR 的 warning 行按 warning 归类
        Assert.DoesNotContain(Scan("WARNINGX something"), b => b.Kind == LogErrorKind.Warning);
        var blocks = Scan("WARNING: error sending stats");
        Assert.All(blocks, b => Assert.Equal(LogErrorKind.Warning, b.Kind));
    }

    [Fact]
    public void Scan_ErrorMarker_IsError()
    {
        var blocks = Scan("[CRITICAL] out of memory");
        var block = Assert.Single(blocks);
        Assert.Equal(LogErrorKind.Error, block.Kind);
    }

    [Fact]
    public void Scan_NoiseHint_IsSkipped()
    {
        Assert.Empty(Scan("To see the GUI go to: http://127.0.0.1:8188"));
    }

    // ---------- 优先级预算 ----------

    [Fact]
    public void Scan_Budget_WarningsDoNotEvictTraceback()
    {
        var lines = new List<string>();
        for (var i = 0; i < 200; i++)
        {
            lines.Add($"WARNING: benign noise {i} " + new string('w', 60));
        }
        lines.Add("Traceback (most recent call last):");
        lines.Add("RuntimeError: HIP out of memory");

        var result = LogErrorScanner.Scan(lines, new LogScanOptions { MaxBytes = 2_000 });

        Assert.True(result.Truncated);
        var first = Assert.Single(result.Blocks, b => b.Kind == LogErrorKind.PythonInterpreter);
        Assert.Contains("HIP out of memory", first.Text);
        Assert.True(result.WarningLineCount == 200);
        Assert.True(result.DroppedBlockCount > 0);
    }

    [Fact]
    public void Scan_Budget_InterpretersFirst_ThenErrors_ThenWarnings()
    {
        var lines = new List<string>
        {
            "WARNING: w1",
            "[ERROR] e1",
            "ValueError: bare tail",
        };
        var result = LogErrorScanner.Scan(lines, new LogScanOptions { MaxBytes = 1_000 });

        Assert.Equal(
            new[] { LogErrorKind.PythonInterpreter, LogErrorKind.Error, LogErrorKind.Warning },
            result.Blocks.Select(b => b.Kind).ToArray());
    }

    // ---------- 指纹归并 ----------

    [Fact]
    public void Group_SameShapeMergesWithCount()
    {
        var blocks = Scan(
            "[ERROR] HIP error: invalid device function at node 1",
            "[INFO] heartbeat",
            "[ERROR] HIP error: invalid device function at node 42");

        var groups = ErrorGrouper.Group(blocks);
        var group = Assert.Single(groups);
        Assert.Equal(LogErrorKind.Error, group.Kind);
        Assert.Equal(2, group.Count);
        Assert.Equal(0, group.FirstLine);
        Assert.Equal(2, group.LastLine);
        // 数字差异被归一：标题不含具体节点号
        Assert.DoesNotContain("42", group.Title);
        Assert.DoesNotContain("node 1", group.Title);
    }

    [Fact]
    public void Group_TracebackTitleUsesExceptionTail()
    {
        var blocks = Scan(
            "Traceback (most recent call last):",
            "RuntimeError: HIP out of memory",
            "Traceback (most recent call last):",
            "RuntimeError: HIP out of memory");

        var group = Assert.Single(ErrorGrouper.Group(blocks));
        Assert.Equal(2, group.Count);
        Assert.Contains("HIP out of memory", group.Title);
        Assert.DoesNotContain("Traceback", group.Title);
    }

    [Fact]
    public void Group_DifferentKindsDoNotMerge()
    {
        var blocks = Scan("[ERROR] x failure 1", "WARNING: x failure 1");
        Assert.Equal(2, ErrorGrouper.Group(blocks).Count);
    }

    [Theory]
    [InlineData("worker pid=12345 started", "worker pid=# started")]
    [InlineData("at C:\\Users\\me\\app\\main.py:10", "at <path>:#")]
    [InlineData("connect 192.168.1.10:8188 failed", "connect <ip> failed")]
    [InlineData("ptr 0xDEADBEEF freed", "ptr <hex> freed")]
    public void Normalize_ReplacesVariableParts(string input, string expected)
    {
        Assert.Equal(expected, ErrorGrouper.Normalize(input));
    }
}
