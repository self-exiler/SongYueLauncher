using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 结构化参数 + 额外参数 → 命令行（FR-2.4 / FR-4.3）。
/// </summary>
public static class ArgsBuilder
{
    /// <summary>把结构化参数合成为命令行片段，末尾追加额外参数原文。</summary>
    public static IReadOnlyList<string> Build(StructuredArgs args, CliArgsCatalog catalog, string? extra = null)
    {
        var tokens = new List<string>();
        foreach (var def in catalog.All)
        {
            if (!args.IsEnabled(def.Name)) continue;
            tokens.AddRange(BuildOne(def, args));
        }

        if (!string.IsNullOrWhiteSpace(extra))
        {
            tokens.AddRange(SplitExtra(extra));
        }
        return tokens;
    }

    private static IEnumerable<string> BuildOne(CliArgDefinition def, StructuredArgs args)
    {
        var flag = "--" + def.Name;
        switch (def.Type)
        {
            case ArgType.Flag:
                return new[] { flag };

            case ArgType.Int:
            case ArgType.Float:
            case ArgType.String:
            case ArgType.Enum:
            {
                var value = args.Value(def.Name);
                return string.IsNullOrWhiteSpace(value) ? Array.Empty<string>() : new[] { flag, value };
            }

            case ArgType.OptionalString:
            case ArgType.OptionalInt:
            {
                // 带值型但允许省略：省略时上游使用 const（如 --listen → 0.0.0.0,::）
                var value = args.Value(def.Name);
                return string.IsNullOrWhiteSpace(value) ? new[] { flag } : new[] { flag, value };
            }

            case ArgType.MultiEnum:
            {
                var pick = args.MultiOf(def.Name).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
                return pick.Count == 0
                    ? new[] { flag }
                    : new[] { flag }.Concat(pick).ToArray();
            }

            case ArgType.FloatList:
            case ArgType.StringList:
            {
                // 值有两个来源：多值勾选 UI 写 Multi，单行文本 UI 写 Value——两处都取（文本按空白拆分，
                // 如 "4 8"）。此前只读 Multi，文本框输入被静默丢弃（审查 #4）。
                var pick = args.MultiOf(def.Name).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
                if (pick.Count == 0 && !string.IsNullOrWhiteSpace(args.Value(def.Name)))
                {
                    pick = args.Value(def.Name)
                        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                        .ToList();
                }
                // StringList 上游是 nargs='+'（至少一值）：取不到值必须整体不输出，
                // 裸传裸 flag 会让 argparse 报 "expected at least one argument" 直接退出；FloatList 是 nargs='*' 可裸传
                return pick.Count == 0
                    ? (def.Type == ArgType.FloatList ? new[] { flag } : Array.Empty<string>())
                    : new[] { flag }.Concat(pick).ToArray();
            }

            case ArgType.RepeatableStringList:
            case ArgType.RepeatableKeyValue:
            {
                var lines = args.LinesOf(def.Name)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .SelectMany(l => new[] { flag, l })
                    .ToArray();
                return lines;
            }

            case ArgType.RepeatableVerbose:
            {
                // 上游 argparse 的级别名区分大小写（LOG_LEVELS），手改 config.json 写小写会启动即报错——统一大写
                var level = args.Value(def.Name)?.Trim().ToUpperInvariant();
                var file = args.Value(def.Name + ":file");
                if (string.IsNullOrWhiteSpace(level) && string.IsNullOrWhiteSpace(file))
                    return new[] { flag };
                if (string.IsNullOrWhiteSpace(file))
                    return new[] { flag, level! };
                return new[] { flag, string.IsNullOrWhiteSpace(level) ? "DEBUG" : level!, file };
            }

            default:
                return new[] { flag };
        }
    }

    /// <summary>额外参数文本框按空白拆分，支持英文双引号包裹含空格的值。</summary>
    public static IReadOnlyList<string> SplitExtra(string extra)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var ch in extra)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>解析生效端口：启用 --port 且为合法端口（1~65535）时取其值，否则回落默认 8188（预览 / 就绪检测共用）。</summary>
    public static int ResolvePort(StructuredArgs args) =>
        args.IsEnabled("port") && int.TryParse(args.Value("port"), out var port) && port is > 0 and <= 65535 ? port : 8188;

    /// <summary>
    /// 浏览器地址推导（FR-4.3）：解析 --port / --listen / --tls-* ，--listen 为通配地址时用 127.0.0.1。
    /// </summary>
    public static string DeriveBrowserUrl(StructuredArgs args)
    {
        var port = ResolvePort(args);

        var host = "127.0.0.1";
        if (args.IsEnabled("listen"))
        {
            var listen = args.Value("listen").Trim();
            if (listen.Length > 0)
            {
                var first = listen.Split(',')[0].Trim();
                var isWildcard = first is "0.0.0.0" or "::" or "*";
                if (!isWildcard) host = first;
            }
        }

        var tls = args.IsEnabled("tls-keyfile") && args.IsEnabled("tls-certfile");
        var scheme = tls ? "https" : "http";
        return $"{scheme}://{host}:{port}";
    }

}
