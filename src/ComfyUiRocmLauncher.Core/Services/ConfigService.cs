using System.Text.Json;
using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// config.json 读写（SRS 5.1）：%APPDATA%\ComfyUI ROCm Launcher\config.json，原子写入，解析失败时备份重建。
/// </summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public ConfigService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ComfyUI ROCm Launcher"))
    {
    }

    public ConfigService(string rootDirectory)
    {
        RootDirectory = rootDirectory;
        ConfigPath = Path.Combine(rootDirectory, "config.json");
        LogDirectory = Path.Combine(rootDirectory, "logs");
    }

    private string RootDirectory { get; }

    private string ConfigPath { get; }

    /// <summary>配置根目录（AI 网关数据目录等派生目录的基准）。</summary>
    public string RootDirectoryPath => RootDirectory;

    public string LogDirectory { get; }

    public AppConfig Load()
    {
        Directory.CreateDirectory(RootDirectory);
        if (!File.Exists(ConfigPath)) return new AppConfig();

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            return cfg ?? new AppConfig();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            BackupBroken();
            return new AppConfig();
        }
    }

    /// <summary>原子写入：临时文件 + 替换（SRS NFR-3）。</summary>
    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(RootDirectory);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        var temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, ConfigPath, overwrite: true);
    }

    public string EnsureLogDirectory()
    {
        Directory.CreateDirectory(LogDirectory);
        return LogDirectory;
    }

    private void BackupBroken()
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Move(ConfigPath, Path.Combine(RootDirectory, $"config.broken.{stamp}.json"), overwrite: true);
        }
        catch (IOException)
        {
            // 备份失败不阻断：仍以默认配置启动
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }
}
