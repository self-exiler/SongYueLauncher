namespace AiDiagnostics;

/// <summary>本机 opencode 广播的可用模型条目（下拉列表用）。</summary>
public sealed record AiModel(string ModelId, string DisplayName)
{
    /// <summary>传给 <c>opencode --model</c> 的 <c>provider/model</c> 复合 id。</summary>
    public string QualifiedId => ModelId;

    public override string ToString() => DisplayName;
}

/// <summary>一次分析的结果。</summary>
public sealed record AiAnswer(bool Success, string Text);
