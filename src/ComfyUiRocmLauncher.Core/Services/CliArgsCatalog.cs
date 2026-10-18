using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 启动参数清单（附录 A）数据驱动入口：从程序集内嵌的 Resources/cli-args.json 加载。
/// </summary>
public sealed class CliArgsCatalog
{
    // 静态字段初始化由运行时保证线程安全（等价于此前的 Lazy，少一层包装）。
    // 代价：内嵌资源缺失时表现为 TypeInitializationException——内层消息仍是 EmbeddedJson 的原始异常。
    private static readonly CliArgsCatalog Shared = new(EmbeddedJson.Load<CliArgsFile>(
        typeof(CliArgsCatalog).Assembly, "Resources.cli-args.json", "cli-args.json"));

    private CliArgsCatalog(CliArgsFile file)
    {
        Groups = file.Groups;
        All = Groups.SelectMany(g => g.Args).ToList();
    }

    public IReadOnlyList<CliArgGroup> Groups { get; }

    public IReadOnlyList<CliArgDefinition> All { get; }

    public int Count => All.Count;

    /// <summary>进程内单例。</summary>
    public static CliArgsCatalog Instance => Shared;

    /// <summary>同一互斥组内除 name 之外的全部参数。</summary>
    public IEnumerable<CliArgDefinition> MutexPeers(string name)
    {
        var self = All.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
        if (self?.Mutex is null) return Array.Empty<CliArgDefinition>();
        return All.Where(a => a.Mutex == self.Mutex && !string.Equals(a.Name, name, StringComparison.Ordinal));
    }

    /// <summary>
    /// 启用某参数时自动关闭同互斥组的其余参数（FR-2.1）。返回被自动关闭的参数名。
    /// </summary>
    public IReadOnlyList<string> EnforceMutex(StructuredArgs args, string name)
    {
        var closed = new List<string>();
        foreach (var peer in MutexPeers(name))
        {
            if (args.IsEnabled(peer.Name))
            {
                args.Enable(peer.Name, false);
                closed.Add(peer.Name);
            }
        }
        return closed;
    }
}
