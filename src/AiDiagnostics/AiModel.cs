namespace AiDiagnostics;

/// <summary>网关广播的模型条目（下拉列表用）。</summary>
public sealed record AiModel(string ModelId, string DisplayName)
{
    /// <summary>发送给网关的原始模型 id（免费车道为裸 id，聚合渠道为 provider/model 复合 id）。</summary>
    public string QualifiedId => ModelId;

    public override string ToString() => DisplayName;
}

/// <summary>一次分析的结果。</summary>
public sealed record AiAnswer(bool Success, string Text);
