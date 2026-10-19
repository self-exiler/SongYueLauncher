using System.Collections.ObjectModel;
using System.Text;
using ComfyUiRocmLauncher.Core.Ai;
using ComfyUiRocmLauncher.GUI.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ComfyUiRocmLauncher.GUI.Views;

/// <summary>
/// AI 智能分析窗口内容（SRS FR-5.6~5.10，原型 v2）：顶部模型栏 + 左日志/右回答两栏 + 竖排按钮列 + 底部状态栏。
/// 发送 / 取消 / 复制 / 重新抓取全部内聚在本控件；进度提示只进底部状态栏，回答区只放正文。
/// 由 <see cref="AiAnalysisWindow"/> 承载，关闭走标题栏 X（Unloaded 时取消进行中的分析）。
/// </summary>
public sealed partial class AiAnalysisDialog : UserControl
{
    private enum StatusKind { Idle, Busy, Ok, Err }

    private readonly ComfyUiAiService _client = new();
    private readonly ObservableCollection<AiModelOption> _models = new();
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
        AnswerMarkdown.Text = "选择模型并点击「发送给 opencode 分析」。";
        Loaded += (_, _) => _ = LoadModelsAsync();
        // 宿主窗口被 X 关闭时取消进行中的分析，别让 opencode 子进程白跑
        Unloaded += (_, _) => _cts?.Cancel();
    }

    /// <summary>抓取到的错误日志填入预览框（可编辑后再发送）。</summary>
    public void SeedErrorPreview(ErrorLogExtraction extraction)
    {
        ErrorPreviewBox.Text = extraction.ToPreviewText();
        var suffix = extraction.Truncated ? "（已按长度截断）" : string.Empty;
        ErrorSummaryText.Text =
            $"从 {extraction.TotalLines} 行日志缓冲区中识别到 {extraction.ErrorLineCount} 行错误{suffix}，可自行增删后再发送";
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
            SetStatus("请先选择一个 opencode 模型。", StatusKind.Err);
            return;
        }

        var errorText = ErrorPreviewBox.Text.Trim();
        if (errorText.Length == 0)
        {
            SetStatus("错误日志预览为空，没有可发送的内容。", StatusKind.Err);
            return;
        }

        SetAnswering(true);
        SetAnswerText(string.Empty);
        _lastAnswer = string.Empty;
        _answerChunks.Clear();
        SetStatus($"已提交给 {model.QualifiedId}，正在等待 opencode 回应…", StatusKind.Busy);

        _cts = new CancellationTokenSource();
        try
        {
            // 故意不 ConfigureAwait(false)：WinUI 3 在 UI 线程装了 DispatcherQueueSynchronizationContext，
            // 恢复点回到 UI 线程，后面就能直接改控件。后台线程的回调（OnOutputLine）仍自己 Enqueue。
            await RunAnalysisAsync(errorText, model.QualifiedId, _cts.Token);
        }
        finally
        {
            var cts = _cts;
            _cts = null;
            SetAnswering(false);
            cts?.Dispose();
        }
    }

    private async Task RunAnalysisAsync(string errorText, string modelQualifiedId, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await _client.AskAsync(
                errorText,
                modelQualifiedId,
                App.Services.Settings.ComfyUiDir,
                App.Services.Settings.PythonExe,
                OnOutputLine,
                cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                SetAnswerText("已取消本次分析。");
                SetStatus("已取消。", StatusKind.Err);
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
                SetStatus("opencode 返回失败，请检查登录状态（opencode auth login）或更换模型后重试。", StatusKind.Err);
            }
        }
        catch (OperationCanceledException)
        {
            SetAnswerText("已取消本次分析。");
            SetStatus("已取消。", StatusKind.Err);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException)
        {
            SetAnswerText(ex.Message);
            SetStatus("调用 opencode 出错。", StatusKind.Err);
        }
        catch (Exception ex)
        {
            SetAnswerText(ex.Message);
            SetStatus("调用 opencode 发生未预期错误。", StatusKind.Err);
        }
    }

    private void SetAnswerText(string text) => AnswerMarkdown.Text = text;

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_lastAnswer.Length == 0) return;
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(_lastAnswer);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        SetStatus("已复制回答到剪贴板。", StatusKind.Ok);
    }

    private void OnOutputLine(string line)
    {
        // 进度提示由 Core 门面标注，界面不再靠「opencode 」字符串前缀猜测
        if (!ComfyUiAiService.TryFormatEvent(line, out var display, out var isProgress)) return;
        if (display is null) return;

        if (isProgress)
        {
            // 进度只进底部状态栏——写进回答区会覆盖已流式出现的正文，看起来像永远卡「处理中」
            _queue.TryEnqueue(() =>
            {
                if (_answering) SetStatus(display, StatusKind.Busy);
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

    private void AppendAnswerChunk(string chunk)
    {
        if (_answerChunks.Length > 0) _answerChunks.Append("\n\n");
        _answerChunks.Append(chunk);
        var text = _answerChunks.ToString();
        SetAnswerText(text);
        _lastAnswer = text;
    }

    private void SetAnswering(bool answering)
    {
        _answering = answering;
        ModelCombo.IsEnabled = !answering;
        RefreshModelsButton.IsEnabled = !answering;
        RecaptureButton.IsEnabled = !answering;
        ErrorPreviewBox.IsReadOnly = answering;
        SendButtonText.Text = answering ? "取消分析" : "发送给 opencode 分析";
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
            _ => "TextFillColorDisabledBrush",
        });
    }

    private void RefreshModels_Click(object sender, RoutedEventArgs e) => _ = LoadModelsAsync();

    private async Task LoadModelsAsync()
    {
        if (_answering) return;
        ModelCombo.IsEnabled = false;
        SetStatus("正在读取 opencode 内嵌模型…", StatusKind.Busy);
        try
        {
            var models = await _client.ListModelsAsync();
            _models.Clear();
            foreach (var m in models) _models.Add(m);
            if (_models.Count == 0)
            {
                SetStatus("opencode 未返回可用模型，请先执行 opencode auth login 配置供应商。", StatusKind.Err);
                ModelCombo.PlaceholderText = "暂无可用模型";
            }
            else
            {
                ModelCombo.SelectedIndex = 0;
                SetStatus($"共读取到 {_models.Count} 个模型。", StatusKind.Ok);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException)
        {
            _models.Clear();
            ModelCombo.PlaceholderText = "模型读取失败";
            SetStatus($"模型读取失败：{ex.Message}", StatusKind.Err);
        }
        finally
        {
            ModelCombo.IsEnabled = !_answering;
        }
    }

    private void Recapture_Click(object sender, RoutedEventArgs e) => RecaptureRequested?.Invoke(this, EventArgs.Empty);
}
