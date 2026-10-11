using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;

namespace ComfyUiRocmLauncher.GUI.Views;

/// <summary>配置管理页（SRS FR-7）：GPU 模板集中选择、应用（全量替换）、另存 / 删除自定义模板。</summary>
public sealed partial class ConfigPage : Page
{
    public ConfigPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            App.Services.StateChanged += OnStateChanged;
            RefreshTemplates();
            SetEnabled(App.Services.State == LaunchState.Idle);
        };
        Unloaded += (_, _) => App.Services.StateChanged -= OnStateChanged;
    }

    private AppConfig Settings => App.Services.Settings;

    private static TemplateCatalog Catalog => TemplateCatalog.Instance;

    private GpuTemplate? Selected => TemplateCombo.SelectedItem as GpuTemplate;

    private void OnStateChanged(LaunchState state) =>
        DispatcherQueue.TryEnqueue(() => SetEnabled(state == LaunchState.Idle));

    private void SetEnabled(bool idle)
    {
        ApplyButton.IsEnabled = idle;
        SaveAsButton.IsEnabled = idle;
    }

    private void RefreshTemplates(string? keepId = null)
    {
        var list = Catalog.AllTemplates(Settings);
        var keep = keepId ?? Selected?.Id;
        TemplateCombo.ItemsSource = list;
        TemplateCombo.SelectedItem =
            list.FirstOrDefault(t => string.Equals(t.Id, keep, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(t => t.Id == "rdna3")
            ?? list.FirstOrDefault();
    }

    private void TemplateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var tpl = Selected;
        DeleteButton.IsEnabled = tpl?.IsCustom == true;
        SelectedInfoText.Text = tpl is null
            ? string.Empty
            : $"{(tpl.IsCustom ? "自定义" : "内置")}模板 · {Catalog.ResolveEnv(tpl, Settings).Count} 个环境变量 / {tpl.ArgCount} 个启动参数";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } tpl) ApplyTemplate(tpl);
    }

    private void ApplyTemplate(GpuTemplate tpl)
    {
        if (App.Services.State != LaunchState.Idle) return;

        Catalog.ApplyTo(tpl, Settings, CliArgsCatalog.Instance);
        App.Services.Save();
        RefreshTemplates(tpl.Id);
        ActionStatusText.Text = $"已应用「{tpl.Title}」：环境变量与启动参数已整体替换";
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (App.Services.State != LaunchState.Idle) return;

        var nameBox = new TextBox { PlaceholderText = "例如：我的 RDNA3 调优" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "另存为自定义模板",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "把当前的环境变量与启动参数保存为可复用的自定义模板。",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    nameBox,
                },
            },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var title = nameBox.Text.Trim();
        if (string.IsNullOrEmpty(title))
        {
            ActionStatusText.Text = "名称为空，未保存";
            return;
        }

        var id = TemplateCatalog.CaptureId(title, Settings);
        Settings.CustomTemplates.Add(Catalog.CaptureAsTemplate(Settings, title, id));
        App.Services.Save();
        RefreshTemplates(id);
        ActionStatusText.Text = $"已保存自定义模板「{title}」";
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { IsCustom: true } tpl) await DeleteAsync(tpl);
    }

    private async Task DeleteAsync(GpuTemplate tpl)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除自定义模板",
            Content = $"确定删除「{tpl.Title}」？该操作不影响已应用的环境变量与参数。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        Settings.CustomTemplates.RemoveAll(t => string.Equals(t.Id, tpl.Id, StringComparison.OrdinalIgnoreCase));
        App.Services.Save();
        RefreshTemplates();
        ActionStatusText.Text = "已删除自定义模板";
    }
}
