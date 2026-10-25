namespace AiDiagnostics.Logs;

/// <summary>错误材料的三种类别（SRS v2.0 FR-5.7）：解释器报错优先级最高，warning 只装剩余预算。</summary>
public enum LogErrorKind
{
    /// <summary>Python 解释器自身报错：Traceback 块与裸异常尾行。</summary>
    PythonInterpreter,

    /// <summary>error 级日志行（ErrorMarkers 命中）。</summary>
    Error,

    /// <summary>warning 级日志行（显式 WARNING 前缀）。</summary>
    Warning,
}
