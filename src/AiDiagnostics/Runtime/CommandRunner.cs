namespace AiDiagnostics.Runtime;

/// <summary>一次性命令执行的结果（与 <see cref="ProcessRunner.RunCapturedAsync"/> 的元组一一对应）。</summary>
internal readonly record struct CommandResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// 命令执行抽象：生产实现包装 <see cref="ProcessRunner"/>，测试注入内存桩，
/// 让客户端契约（NDJSON 分流、重试、超时文案）不必真的拉起 opencode。
/// </summary>
internal interface ICommandRunner
{
    Task<CommandResult> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        int timeoutMs,
        Action<string>? onOutputLine,
        CancellationToken cancellationToken,
        string? standardInput = null,
        IDictionary<string, string>? environment = null);
}

internal sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        int timeoutMs,
        Action<string>? onOutputLine,
        CancellationToken cancellationToken,
        string? standardInput = null,
        IDictionary<string, string>? environment = null)
    {
        var (code, stdout, stderr) = await ProcessRunner.RunCapturedAsync(
            fileName, arguments, workingDirectory, timeoutMs, onOutputLine, cancellationToken,
            standardInput, environment).ConfigureAwait(false);
        return new CommandResult(code, stdout, stderr);
    }
}
