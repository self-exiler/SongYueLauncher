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

    // ---------- 优先级预算（先指纹归并、再按预算装填） ----------

    [Fact]
    public void Scan_Budget_WarningsDoNotEvictTraceback()
    {
        // 互不同型的 warning（归一后指纹各异），才能撑满预算触发丢弃
        var lines = new List<string>();
        for (var i = 0; i < 200; i++)
        {
            var tag = $"{(char)('a' + i / 26)}{(char)('a' + i % 26)}";
            lines.Add($"WARNING: benign {tag} " + new string('w', 60));
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
    public void Scan_Budget_IdenticalFloodMergesBeforeBudget()
    {
        // A5 回归：同一错误刷屏先归并、只占一份预算——别的类型不能被重复项挤掉
        var lines = new List<string>();
        for (var i = 0; i < 1_000; i++)
        {
            lines.Add($"WARNING: same noise over and over {i}");
        }
        lines.Add("[ERROR] the only real error");

        var result = LogErrorScanner.Scan(lines, new LogScanOptions { MaxBytes = 2_000 });

        Assert.Contains(result.Blocks, b => b.Kind == LogErrorKind.Error);
        var warningGroup = Assert.Single(result.Groups, g => g.Kind == LogErrorKind.Warning);
        Assert.Equal(1_000, warningGroup.Count);
    }

    [Fact]
    public void Scan_Budget_OversizedTracebackIsTruncatedNotDropped()
    {
        // #13 回归：超过整个预算的 Traceback 截断保留（头 20 + 尾 30 行），不再整块丢弃
        var lines = new List<string> { "Traceback (most recent call last):" };
        for (var i = 0; i < 300; i++)
        {
            lines.Add($"  File \"module_{i}.py\", line {i}, in func_{i} " + new string('f', 60));
        }
        lines.Add("RuntimeError: HIP out of memory");

        var result = LogErrorScanner.Scan(lines, new LogScanOptions { MaxBytes = 4_000 });

        var interpreter = Assert.Single(result.Groups, g => g.Kind == LogErrorKind.PythonInterpreter);
        Assert.Contains("截断", interpreter.Representative.Text);
        Assert.Contains("RuntimeError: HIP out of memory", interpreter.Representative.Text);
        Assert.Contains("Traceback", interpreter.Representative.Text); // 头部保留
        Assert.True(interpreter.Representative.Text.Length <= 4_000);
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

    // ---------- 指纹归并（Scan 已内置归并，直接断言结果分组） ----------

    [Fact]
    public void Group_SameShapeMergesWithCount()
    {
        var result = LogErrorScanner.Scan(new[]
        {
            "[ERROR] HIP error: invalid device function at node 1",
            "[INFO] heartbeat",
            "[ERROR] HIP error: invalid device function at node 42",
        });

        var group = Assert.Single(result.Groups);
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
        var result = LogErrorScanner.Scan(new[]
        {
            "Traceback (most recent call last):",
            "RuntimeError: HIP out of memory",
            "Traceback (most recent call last):",
            "RuntimeError: HIP out of memory",
        });

        var group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Count);
        Assert.Contains("HIP out of memory", group.Title);
        Assert.DoesNotContain("Traceback", group.Title);
    }

    [Fact]
    public void Group_DifferentKindsDoNotMerge()
    {
        var result = LogErrorScanner.Scan(new[] { "[ERROR] x failure 1", "WARNING: x failure 1" });
        Assert.Equal(2, result.Groups.Count);
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
