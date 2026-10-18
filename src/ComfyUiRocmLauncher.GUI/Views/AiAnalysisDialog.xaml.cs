using System.Collections.ObjectModel;
using System.Text;
using ComfyUiRocmLauncher.Core.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ComfyUiRocmLauncher.GUI.Views;

/// <summary>
/// AI 智能分析对话框内容（SRS FR-5.6~5.9）：错误日志预览 + opencode 内嵌模型下拉 + 发送与回答展示。
/// 由 LogPage 包在 ContentDialog 中承载，Primary/Close 按钮由宿主对话框驱动。
/// </summary>
public sealed partial class AiAnalysisDialog : UserControl
{
    private readonly OpenCodeClient _client = new();
    private readonly ObservableCollection<OpenCodeModel> _models = new();
    private readonly DispatcherQueue _queue;
    private CancellationTokenSource? _cts;
    private bool _answering;
    private string _lastAnswer = string.Empty;
    private readonly StringBuilder _answerChunks = new();

    public AiAnalysisDialog()
    {
        InitializeComponent();
        _queue = DispatcherQueue.GetForCurrentThread();
        ModelCombo.ItemsSource = _models;
        RenderToggle.IsChecked = true;
        Loaded += (_, _) => _ = LoadModelsAsync();
    }

    /// <summary>宿主对话框的 Primary 按钮文本。</summary>
    public string SendLabel => _answering ? "取消分析" : "发送给 opencode 分析";

    /// <summary>抓取到的错误日志填入预览框（可编辑后再发送）。</summary>
    public void SeedErrorPreview(string errorText, int totalLines, int errorLineCount, bool truncated)
    {
        ErrorPreviewBox.Text = errorText;
        var suffix = truncated ? "（已按长度截断）" : string.Empty;
        ErrorSummaryText.Text = $"从 {totalLines} 行日志缓冲区中识别到 {errorLineCount} 行错误{suffix}，可自行增删后再发送";
    }

    /// <summary>宿主 Primary 按钮回调：发送或取消。</summary>
    public async Task HandleSendAsync()
    {
        if (_answering)
        {
            _cts?.Cancel();
            return;
        }

        var model = ModelCombo.SelectedItem as OpenCodeModel;
        if (model is null)
        {
            StatusText.Text = "请先选择一个 opencode 模型。";
            return;
        }

        var errorText = ErrorPreviewBox.Text.Trim();
        if (errorText.Length == 0)
        {
            StatusText.Text = "错误日志预览为空，没有可发送的内容。";
            return;
        }

        var prompt = ErrorLogExtractor.BuildPrompt(
            errorText, App.Services.Settings.ComfyUiDir, App.Services.Settings.PythonExe);

        SetAnswering(true);
        SetAnswerText("正在等待 opencode 回应…");
        _lastAnswer = string.Empty;
        _answerChunks.Clear();
        StatusText.Text = $"已提交给 {model.QualifiedId}，分析可能需要数十秒。";

        _cts = new CancellationTokenSource();
        try
        {
            await RunAnalysisAsync(prompt, model.QualifiedId, _cts.Token).ConfigureAwait(false);
        }
        finally
        {
            var cts = _cts;
            _cts = null;
            _queue.TryEnqueue(() =>
            {
                SetAnswering(false);
                cts?.Dispose();
            });
        }
    }

    private async Task RunAnalysisAsync(string prompt, string modelQualifiedId, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await _client.AskAsync(prompt, modelQualifiedId, OnOutputLine, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var cancelled = cancellationToken.IsCancellationRequested;

            _queue.TryEnqueue(() =>
            {
                if (cancelled)
                {
                    SetAnswerText("已取消本次分析。");
                    StatusText.Text = "已取消。";
                }
                else if (answer.Success)
                {
                    SetAnswerText(answer.Text);
                    _lastAnswer = answer.Text;
                    StatusText.Text = "分析完成，可点击「复制回答」保存。";
                }
                else
                {
                    SetAnswerText(answer.Text);
                    StatusText.Text = "opencode 返回失败，请检查登录状态（opencode auth login）或更换模型后重试。";
                }
            });
        }
        catch (OperationCanceledException)
        {
            _queue.TryEnqueue(() =>
            {
                SetAnswerText("已取消本次分析。");
                StatusText.Text = "已取消。";
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException)
        {
            _queue.TryEnqueue(() =>
            {
                SetAnswerText(ex.Message);
                StatusText.Text = "调用 opencode 出错。";
            });
        }
        catch (Exception ex)
        {
            _queue.TryEnqueue(() =>
            {
                SetAnswerText(ex.Message);
                StatusText.Text = "调用 opencode 发生未预期错误。";
            });
        }
    }

    /// <summary>同步原文 / Markdown 两个视图（MarkdownTextBlock 不支持选中，保留原文模式兜底）。</summary>
    private void SetAnswerText(string text)
    {
        AnswerText.Text = text;
        AnswerMarkdown.Text = text;
    }

    private void RenderMode_Changed(object sender, RoutedEventArgs e)
    {
        var markdown = RenderToggle.IsChecked == true;
        AnswerMarkdown.Visibility = markdown ? Visibility.Visible : Visibility.Collapsed;
        AnswerText.Visibility = markdown ? Visibility.Collapsed : Visibility.Visible;
        RenderToggle.Content = markdown ? "原文" : "Markdown";
    }

    /// <summary>宿主对话框 Secondary「复制回答」按钮回调；无回答时返回 false。</summary>
    public bool TryCopyAnswer()
    {
        if (string.IsNullOrEmpty(_lastAnswer)) return false;
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(_lastAnswer);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        return true;
    }

    private void OnOutputLine(string line)
    {
        var display = OpenCodeClient.TryFormatRunEvent(line);
        if (string.IsNullOrWhiteSpace(display)) return;

        if (IsProgressMessage(display))
        {
            _queue.TryEnqueue(() =>
            {
                if (!_answering) return;
                SetAnswerText($"opencode 处理中…\n{Tail(display, 160)}");
                AnswerScroll.ChangeView(null, double.MaxValue, null);
            });
            return;
        }

        _queue.TryEnqueue(() =>
        {
            if (!_answering) return;
            AppendAnswerChunk(display);
            AnswerScroll.ChangeView(null, double.MaxValue, null);
        });
    }

    private static bool IsProgressMessage(string display) =>
        display.StartsWith("opencode 已开始", StringComparison.Ordinal)
        || display.StartsWith("opencode 正在继续", StringComparison.Ordinal)
        || display.StartsWith("opencode 正在使用 ", StringComparison.Ordinal);

    private void AppendAnswerChunk(string chunk)
    {
        if (_answerChunks.Length > 0) _answerChunks.Append("\n\n");
        _answerChunks.Append(chunk);
        var text = _answerChunks.ToString();
        SetAnswerText(text);
        _lastAnswer = text;
    }

    private static string Tail(string text, int max) => text.Length <= max ? text : text[^max..];

    private void SetAnswering(bool answering)
    {
        _answering = answering;
        ModelCombo.IsEnabled = !answering;
        RefreshModelsButton.IsEnabled = !answering;
        RecaptureButton.IsEnabled = !answering;
        ErrorPreviewBox.IsReadOnly = answering;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshModels_Click(object sender, RoutedEventArgs e) => _ = LoadModelsAsync();

    private async Task LoadModelsAsync()
    {
        if (_answering) return;
        ModelCombo.IsEnabled = false;
        StatusText.Text = "正在读取 opencode 内嵌模型…";
        try
        {
            var models = await _client.ListModelsAsync();
            _models.Clear();
            foreach (var m in models) _models.Add(m);
            if (_models.Count == 0)
            {
                StatusText.Text = "opencode 未返回可用模型，请先执行 opencode auth login 配置供应商。";
                ModelCombo.PlaceholderText = "暂无可用模型";
            }
            else
            {
                ModelCombo.SelectedIndex = 0;
                StatusText.Text = $"共读取到 {_models.Count} 个模型。";
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException)
        {
            _models.Clear();
            ModelCombo.PlaceholderText = "模型读取失败";
            StatusText.Text = $"模型读取失败：{ex.Message}";
        }
        finally
        {
            ModelCombo.IsEnabled = !_answering;
        }
    }

    private void Recapture_Click(object sender, RoutedEventArgs e) => RecaptureRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>分析状态变化（开始 / 结束），宿主据此刷新 Primary 按钮文本。</summary>
    public event EventHandler? StateChanged;

    /// <summary>宿主重新从日志缓冲抓取错误。</summary>
    public event EventHandler? RecaptureRequested;
}
