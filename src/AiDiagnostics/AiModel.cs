namespace AiDiagnostics;

/// <summary>可供选择的模型条目（下拉列表用）。</summary>
public sealed record AiModel(string ProviderId, string ModelId, string DisplayName)
{
    /// <summary>opencode <c>--model</c> 需要的 <c>provider/model</c> 形式。</summary>
    public string QualifiedId => $"{ProviderId}/{ModelId}";

    public override string ToString() => DisplayName;
}

/// <summary>一次分析的结果。</summary>
public sealed record AiAnswer(bool Success, string Text);
