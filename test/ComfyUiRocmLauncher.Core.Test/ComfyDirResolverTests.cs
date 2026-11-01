using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;

namespace ComfyUiRocmLauncher.Core.Test;

/// <summary>
/// FR-1.4 / FR-1.5 数据目录生效路径解析：base-directory 重定位公共根、
/// 各 *-directory 单独覆盖、只认 Enabled、相对值按 ComfyUI 目录展开。
/// </summary>
public class ComfyDirResolverTests
{
    private static AppConfig Config() => new() { ComfyUiDir = @"D:\comfy" };

    private static void Enable(AppConfig cfg, string name, string value)
    {
        cfg.Args.Enable(name, true);
        cfg.Args.SetValue(name, value);
    }

    [Fact]
    public void 未启用任何参数_按ComfyUI目录解析()
    {
        var cfg = Config();
        Assert.Equal(@"D:\comfy\models", ComfyDirResolver.Resolve(cfg, "models"));
        Assert.Equal(@"D:\comfy\user\workflow", ComfyDirResolver.Resolve(cfg, @"user\workflow"));
        Assert.Equal(@"D:\comfy\custom_nodes", ComfyDirResolver.Resolve(cfg, "custom_nodes"));
    }

    [Fact]
    public void BaseDirectory_重定位全部数据目录()
    {
        var cfg = Config();
        Enable(cfg, "base-directory", @"E:\ComfyuiData");
        Assert.Equal(@"E:\ComfyuiData\models", ComfyDirResolver.Resolve(cfg, "models"));
        Assert.Equal(@"E:\ComfyuiData\output", ComfyDirResolver.Resolve(cfg, "output"));
        Assert.Equal(@"E:\ComfyuiData\custom_nodes", ComfyDirResolver.Resolve(cfg, "custom_nodes"));
        Assert.Equal(@"E:\ComfyuiData\user\workflow", ComfyDirResolver.Resolve(cfg, @"user\workflow"));
    }

    [Fact]
    public void 单项参数_覆盖BaseDirectory()
    {
        var cfg = Config();
        Enable(cfg, "base-directory", @"E:\ComfyuiData");
        Enable(cfg, "output-directory", @"F:\out");
        Assert.Equal(@"F:\out", ComfyDirResolver.Resolve(cfg, "output"));
        Assert.Equal(@"E:\ComfyuiData\models", ComfyDirResolver.Resolve(cfg, "models"));
    }

    [Fact]
    public void 关闭开关的残留值_不参与解析()
    {
        var cfg = Config();
        Enable(cfg, "base-directory", @"E:\ComfyuiData");
        cfg.Args.Enable("base-directory", false);
        Assert.Equal(@"D:\comfy\models", ComfyDirResolver.Resolve(cfg, "models"));
    }

    [Fact]
    public void 相对值_按ComfyUI目录展开()
    {
        var cfg = Config();
        Enable(cfg, "base-directory", "data");
        Assert.Equal(@"D:\comfy\data\models", ComfyDirResolver.Resolve(cfg, "models"));
    }

    [Fact]
    public void 带引号与尾分隔符的取值_归一化()
    {
        var cfg = Config();
        Enable(cfg, "base-directory", "\"E:\\ComfyuiData\\\"");
        Assert.Equal(@"E:\ComfyuiData\models", ComfyDirResolver.Resolve(cfg, "models"));
    }
}
