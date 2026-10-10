using System.Collections.Concurrent;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 日志服务（SRS FR-5）：内存环形缓冲 5000 行 + 会话落盘 + 磁盘保护（30 份 / 200 MB）。
/// </summary>
public sealed class LogService
{
    public const int DefaultCapacity = 5000;

    private readonly ConcurrentQueue<string> _buffer = new();
    private readonly object _fileLock = new();
    private StreamWriter? _writer;

    public LogService(string logDirectory, int capacity = DefaultCapacity)
    {
        LogDirectory = logDirectory;
        Capacity = capacity;
    }

    public string LogDirectory { get; }

    public int Capacity { get; }

    /// <summary>UI 订阅该事件追加渲染（触发线程为输出读取线程，UI 侧需自行切回 UI 线程）。</summary>
    public event Action<string>? LineAppended;

    public IReadOnlyList<string> Snapshot() => _buffer.ToArray();

    public int Count => _buffer.Count;

    /// <summary>开启一次会话日志：logs\comfyui_yyyy-MM-dd_HH-mm-ss.log。</summary>
    public string StartSession()
    {
        Directory.CreateDirectory(LogDirectory);
        var file = Path.Combine(LogDirectory, $"comfyui_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
        lock (_fileLock)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = new StreamWriter(new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = false,
            };
        }
        return file;
    }

    public void Append(string line)
    {
        _buffer.Enqueue(line);
        while (_buffer.Count > Capacity && _buffer.TryDequeue(out _)) { }

        lock (_fileLock)
        {
            _writer?.WriteLine(line);
        }
        LineAppended?.Invoke(line);
    }

    public void EndSession()
    {
        lock (_fileLock)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    public void Clear()
    {
        while (_buffer.TryDequeue(out _)) { }
    }

    /// <summary>磁盘保护（FR-5.4）：仅保留最近 retainCount 份；总大小超限时从最旧开始删除。</summary>
    public int ApplyRetention(int retainCount, int retainTotalMB)
    {
        if (!Directory.Exists(LogDirectory)) return 0;

        var files = new DirectoryInfo(LogDirectory)
            .EnumerateFiles("comfyui_*.log")
            .OrderBy(f => f.CreationTimeUtc)
            .ToList();

        var deleted = 0;
        while (files.Count > retainCount)
        {
            TryDelete(files[0]);
            files.RemoveAt(0);
            deleted++;
        }

        var limit = (long)retainTotalMB * 1024 * 1024;
        var total = files.Sum(SafeLength);
        var index = 0;
        while (total > limit && index < files.Count)
        {
            var len = SafeLength(files[index]);
            TryDelete(files[index]);
            total -= len;
            index++;
            deleted++;
        }
        return deleted;
    }

    private static long SafeLength(FileInfo f)
    {
        try { return f.Exists ? f.Length : 0; }
        catch (IOException) { return 0; }
    }

    private static void TryDelete(FileInfo f)
    {
        try { f.Delete(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
