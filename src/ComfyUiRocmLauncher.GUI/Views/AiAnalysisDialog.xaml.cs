using System.Collections.ObjectModel;
using ComfyUiRocmLauncher.Core.Ai;
using ComfyUiRocmLauncher.GUI.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ComfyUiRocmLauncher.GUI.Views;

/// <summary>
/// AI 智能分析窗口内容（SRS v2.0 FR-5.6~5.11，原型 v2）：顶部模型栏 + 左分组日志/右回答两栏 + 竖排按钮列 + 底部状态栏。
/// 左栏为指纹归并后的分组视图（FR-5.11，替代旧可编辑预览框）；发送 / 取消 / 复制 / 重新抓取全部内聚在本控件；
/// 正文增量进回答区，工具/思考状态只进底部状态栏。由 <see cref="AiAnalysisWindow"/> 承载，关闭走标题栏 X。
/// </summary>
public sealed partial class AiAnalysisDialog : UserControl
{
    private enum StatusKind { Idle, Busy, Ok, Err }

    private readonly ComfyUiAiService _client;
    private readonly ObservableCollection<AiModelOption> _models = new();
    private readonly DispatcherQueue _queue;
    private CancellationTokenSource? _cts;
    private bool _answering;
    private bool _streaming;
    private string _lastAnswer = string.Empty;
    private readonly System.Text.StringBuilder _answerChunks = new();
    private ErrorLogExtraction _extraction = new([], 0, false);

    /// <summary>分组卡与「发送」复选框的对应关系（勾选集即发送给模型的材料集）。</summary>
    private readonly List<(ErrorGroupView Group, CheckBox Send)> _groupChecks = [];

    public AiAnalysisDialog()
    {
        InitializeComponent();
        _client = App.Services.Ai;
        _queue = DispatcherQueue.GetForCurrentThread();
        ModelCombo.ItemsSource = _models;
        AnswerMarkdown.Text = "选择模型并点击「发送分析」。";
        Loaded += (_, _) => _ = LoadModelsAsync();
        // 宿主窗口被 X 关闭时取消进行中的分析，别让请求白跑
        Unloaded += (_, _) => _cts?.Cancel();
    }

    /// <summary>抓取到的错误材料以分组卡片形式填入左栏（FR-5.11）；每卡带「发送」勾选，默认解释器/error 勾选、warning 不勾。</summary>
    public void SeedErrorPreview(ErrorLogExtraction extraction)
    {
        _extraction = extraction;
        _groupChecks.Clear();
        GroupPanel.Children.Clear();
        GroupHintText.Visibility = Visibility.Collapsed;

        if (extraction.Groups.Count == 0)
        {
            GroupHintText.Text = "未在当前日志缓冲区中识别到 error 级日志、Python 报错或 warning。";
            GroupHintText.Visibility = Visibility.Visible;
            UpdateSummary();
            return;
        }
        if (extraction.Truncated)
        {
            GroupHintText.Text = $"部分分组因长度预算被丢弃（{extraction.DroppedBlockCount} 组）；超长 Traceback 已截断保留，同类重复已归并计数。";
            GroupHintText.Visibility = Visibility.Visible;
        }

        foreach (var group in extraction.Groups)
        {
            var (card, send) = BuildGroupCard(group);
            _groupChecks.Add((group, send));
            GroupPanel.Children.Add(card);
        }
        UpdateSummary();
    }

    /// <summary>KPI 行：三类计数 + 当前勾选态。</summary>
    private void UpdateSummary()
    {
        var selected = _groupChecks.Count(p => p.Send.IsChecked == true);
        var total = _groupChecks.Count;
        ErrorSummaryText.Text = total == 0
            ? _extraction.ToSummaryText()
            : $"{_extraction.ToSummaryText()}；已勾选 {selected}/{total} 组";
    }

    /// <summary>宿主重新从日志缓冲抓取错误。</summary>
    public event EventHandler? RecaptureRequested;

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_answering)
        {
            _cts?.Cancel();
            return;
        }

        var model = ModelCombo.SelectedItem as AiModelOption;
        if (model is null)
        {
            SetStatus("请先选择一个 AI 模型。", StatusKind.Err);
            return;
        }

        if (_extraction.Groups.Count == 0)
        {
            SetStatus("没有可发送的错误材料，请先在日志中制造一次报错或点击「重新抓取」。", StatusKind.Err);
            return;
        }

        // 只把勾选的分组组装进提示词（FR-5.11）
        var selected = _groupChecks
            .Where(p => p.Send.IsChecked == true)
            .Select(p => p.Group)
            .ToList();
        if (selected.Count == 0)
        {
            SetStatus("未勾选任何错误组：请至少勾选一组，或点击「重新抓取」恢复默认勾选。", StatusKind.Err);
            return;
        }

        SetAnswering(true);
        SetAnswerText(string.Empty);
        _lastAnswer = string.Empty;
        _answerChunks.Clear();
        _streaming = false;
        SetStatus($"已提交 {selected.Count} 组给 {model.Id}，正在等待模型回应…", StatusKind.Busy);

        _cts = new CancellationTokenSource();
        try
        {
            // 故意不 ConfigureAwait(false)：WinUI 3 在 UI 线程装了 DispatcherQueueSynchronizationContext，
            // 恢复点回到 UI 线程，后面就能直接改控件。后台线程的回调（OnDelta/OnStatus）仍自己 Enqueue。
            await RunAnalysisAsync(_extraction with { Groups = selected }, model.Id, _cts.Token);
        }
        finally
        {
            var cts = _cts;
            _cts = null;
            SetAnswering(false);
            cts?.Dispose();
        }
    }

    private async Task RunAnalysisAsync(ErrorLogExtraction extraction, string modelId, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await _client.AskAsync(
                extraction,
                modelId,
                App.Services.Settings.ComfyUiDir,
                App.Services.Settings.PythonExe,
                OnDelta,
                OnStatus,
                cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                ShowCancelledWithPartial();
            }
            else if (answer.Success)
            {
                SetAnswerText(answer.Text);
                _lastAnswer = answer.Text;
                SetStatus("分析完成，可点击「复制回答」保存。", StatusKind.Ok);
            }
            else
            {
                SetAnswerText(answer.Text);
                SetStatus("分析失败，请检查网关服务状态或更换模型后重试。", StatusKind.Err);
            }
        }
        catch (OperationCanceledException)
        {
            ShowCancelledWithPartial();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException)
        {
            SetAnswerText(ex.Message);
            SetStatus("调用 AI 网关出错。", StatusKind.Err);
        }
        catch (Exception ex)
        {
            SetAnswerText(ex.Message);
            SetStatus("调用 AI 网关发生未预期错误。", StatusKind.Err);
        }
    }

    private void SetAnswerText(string text) => AnswerMarkdown.Text = text;

    /// <summary>取消时保留已流式显示的半截回答，只追加取消标记（tmp#16），不再整段覆盖。</summary>
    private void ShowCancelledWithPartial()
    {
        var partial = _answerChunks.ToString();
        SetAnswerText(partial.Length > 0 ? partial + "\n\n（已取消本次分析）" : "已取消本次分析。");
        SetStatus("已取消。", StatusKind.Err);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_lastAnswer.Length == 0) return;
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(_lastAnswer);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        SetStatus("已复制回答到剪贴板。", StatusKind.Ok);
    }

    private void OnDelta(string chunk)
    {
        _queue.TryEnqueue(() =>
        {
            if (!_answering) return;
            _streaming = true;
            _answerChunks.Append(chunk);
            var text = _answerChunks.ToString();
            SetAnswerText(text);
            _lastAnswer = text;
            AnswerScroll.ChangeView(null, double.MaxValue, null);
        });
    }

    private void OnStatus(string message)
    {
        // 进度提示只进底部状态栏——写进回答区会覆盖已流式出现的正文，看起来像永远卡「处理中」
        _queue.TryEnqueue(() =>
        {
            if (_answering && !_streaming) SetStatus(message, StatusKind.Busy);
        });
    }

    private void SetAnswering(bool answering)
    {
        _answering = answering;
        ModelCombo.IsEnabled = !answering;
        RefreshModelsButton.IsEnabled = !answering;
        RecaptureButton.IsEnabled = !answering;
        SendButtonText.Text = answering ? "取消分析" : "发送分析";
        CopyButton.IsEnabled = !answering && _lastAnswer.Length > 0;
    }

    private void SetStatus(string text, StatusKind kind)
    {
        StatusText.Text = text;
        StatusDot.Fill = ThemeHelper.Brush(kind switch
        {
            StatusKind.Busy => "AccentFillColorDefaultBrush",
            StatusKind.Ok => "SystemFillColorSuccessBrush",
            StatusKind.Err => "SystemFillColorCriticalBrush",
            // 空闲用次级前景：disabled 刷子语义上是「控件禁用」
            _ => "TextFillColorSecondaryBrush",
        });
    }

    private void RefreshModels_Click(object sender, RoutedEventArgs e) => _ = LoadModelsAsync();

    private async Task LoadModelsAsync()
    {
        if (_answering) return;
        ModelCombo.IsEnabled = false;
        SetStatus("正在读取网关模型…", StatusKind.Busy);
        try
        {
            var models = await _client.ListModelsAsync();
            _models.Clear();
            foreach (var m in models) _models.Add(m);
            if (_models.Count == 0)
            {
                SetStatus("网关未返回可用模型，请确认 Our Free Model 服务已启动并完成模型探测。", StatusKind.Err);
                ModelCombo.PlaceholderText = "暂无可用模型";
            }
            else
            {
                ModelCombo.SelectedIndex = 0;
                SetStatus($"共读取到 {_models.Count} 个模型。", StatusKind.Ok);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException
            or HttpRequestException or TaskCanceledException)
        {
            _models.Clear();
            ModelCombo.PlaceholderText = "模型读取失败";
            // 网关进程死掉抛 HttpRequestException、超时抛 TaskCanceledException——此前都不在过滤器里，
            // 被 `_ =` 火后不管吞掉，状态栏永久停在「正在读取网关模型…」（审查 #12d）
            var reason = ex is TaskCanceledException ? "读取模型超时（网关无响应）" : ex.Message;
            SetStatus($"模型读取失败：{reason}", StatusKind.Err);
        }
        finally
        {
            ModelCombo.IsEnabled = !_answering;
        }
    }

    private void Recapture_Click(object sender, RoutedEventArgs e) => RecaptureRequested?.Invoke(this, EventArgs.Empty);

    // ---------- 分组卡片（FR-5.11） ----------

    /// <summary>构建一张分组卡：头部为「发送勾选 + 类别徽章 + 归一化标题 + 计数/行号」，内容为代表块原文。
    /// 默认勾选策略与提示词分节一致——解释器与 error 是根因材料（勾选），warning 是背景信号（不勾）。</summary>
    private (Expander Card, CheckBox Send) BuildGroupCard(ErrorGroupView group)
    {
        var (label, brushKey) = group.Kind switch
        {
            ErrorGroupKind.PythonInterpreter => ("解释器", "SystemFillColorCriticalBrush"),
            ErrorGroupKind.Error => ("ERROR", "SystemFillColorCriticalBrush"),
            _ => ("WARN", "SystemFillColorCautionBrush"),
        };

        var send = new CheckBox
        {
            IsChecked = group.Kind != ErrorGroupKind.Warning,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // 复选框只有 ToolTip 时读屏读不到用途与对象，补自动化名称
        AutomationProperties.SetName(send, $"发送分组「{group.Title}」给 AI");
        ToolTipService.SetToolTip(send, "勾选后该组材料将包含在发送给 AI 的提问中");
        send.Checked += (_, _) => UpdateSummary();
        send.Unchecked += (_, _) => UpdateSummary();

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(send);
        header.Children.Add(new Border
        {
            // 徽章底色取状态色淡底、文字恒为正文色：此前固定白字配 caution 黄底在浅色主题下不可读
            Background = ThemeHelper.Tint(brushKey, 0.25),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1, 6, 2),
            Child = new TextBlock
            {
                Text = label,
                // Segoe 可读下限 12：徽章字号不小于正文字阶
                FontSize = 12,
                Foreground = ThemeHelper.Brush("TextFillColorPrimaryBrush"),
            },
        });
        header.Children.Add(new TextBlock
        {
            Text = group.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 360,
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock
        {
            Text = group.Count > 1 ? $"×{group.Count} · 第 {group.FirstLine + 1}~{group.LastLine + 1} 行" : $"第 {group.FirstLine + 1} 行",
            Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
            VerticalAlignment = VerticalAlignment.Center,
        });

        var body = new ScrollViewer
        {
            MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock
            {
                Text = group.RepresentativeText,
                FontFamily = (FontFamily)Application.Current.Resources["MonoFontFamily"],
                FontSize = 12,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap,
            },
        };

        var card = new Expander
        {
            Header = header,
            Content = body,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        return (card, send);
    }
}
