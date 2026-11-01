namespace Plugin.Engine;

/// <summary>
/// 控制台业务：输出缓冲、命令历史、提交、condump。
///
/// 引擎输出由 EngineConsole 钩子格式化后经 <see cref="EngineConsole.LineAdded"/>
/// 原样发布（引擎线程），这里落缓冲；
/// 提交语义按原版 CConsolePanel::OnCommand 复刻：先回显 "] 命令\n" 再执行，
/// clear/condump 是控制台面板自己的命令，不下发引擎。
/// 控制台窗口只认这里的事件，不直接订阅接入层。
/// </summary>
public static class ConsoleService
{
    // 原生 RichText 不设上限；给个足够大的上限，避免长时间运行拖垮 UI。
    const int MaxLines = 5000;
    const int MaxHistory = 100;
    const int MaxDumpFiles = 1000;

    static readonly object s_sync = new();
    static readonly List<ConsoleLine> s_lines = new();
    // 原版历史项带命令/参数拆分做去重；窗口回显只用文本
    readonly record struct HistoryEntry(string Text, string Command, string Extra);
    static readonly List<HistoryEntry> s_history = new();

    static bool s_initialized;

    /// <summary>新增一行输出（引擎回调或本地回显；UI 侧自行切到 UI 线程）</summary>
    public static event Action<ConsoleLine>? LineAdded;

    /// <summary>缓冲被清空（引擎 clear 或控制台 clear 命令）</summary>
    public static event Action? Cleared;

    /// <summary>订阅接入层原始输出（应用启动时调用一次）</summary>
    public static void Initialize()
    {
        if (s_initialized)
            return;
        s_initialized = true;
        EngineConsole.LineAdded += OnLineAdded;
        EngineConsole.Cleared += OnCleared;
    }

    static void OnLineAdded(ConsoleLine line)
    {
        lock (s_sync)
        {
            s_lines.Add(line);
            if (s_lines.Count > MaxLines)
                s_lines.RemoveAt(0);
        }
        LineAdded?.Invoke(line);
    }

    static void OnCleared()
    {
        lock (s_sync)
            s_lines.Clear();
        Cleared?.Invoke();
    }

    /// <summary>当前缓冲快照（窗口创建时补齐历史输出）</summary>
    public static ConsoleLine[] SnapshotLines()
    {
        lock (s_sync)
            return s_lines.ToArray();
    }

    /// <summary>命令历史（最旧 -> 最新），供 UP/DOWN 回显</summary>
    public static string[] SnapshotHistory()
    {
        lock (s_sync)
        {
            var result = new string[s_history.Count];
            for (int i = 0; i < s_history.Count; i++)
                result[i] = s_history[i].Text;
            return result;
        }
    }

    /// <summary>
    /// 提交一条命令（等价控制台输入框回车）。
    /// 等价 CConsolePanel::OnCommand("Submit")：先回显 "] 命令\n"，再执行；
    /// clear/condump 是控制台面板自己的命令（原版不下发引擎）
    /// </summary>
    public static void Submit(string raw)
    {
        var command = (raw ?? "").TrimEnd('\r', '\n');
        AppendLocal("] " + command + "\n", false);

        var trimmed = command.Trim();
        if (trimmed.Length == 0)
            return;

        AddToHistory(command);

        var lower = trimmed.ToLowerInvariant();
        if (lower == "clear")
        {
            OnCleared();
            return;
        }
        if (lower == "condump" || lower.StartsWith("condump "))
        {
            DumpToFile();
            return;
        }
        EngineCommands.ClientCmd(command);
    }

    static void AppendLocal(string text, bool dev)
    {
        if (string.IsNullOrEmpty(text))
            return;
        var parts = text.Replace("\r", "").Split('\n');
        int count = parts.Length;
        if (count > 0 && parts[count - 1].Length == 0)
            count--;
        for (int i = 0; i < count; i++)
            OnLineAdded(new ConsoleLine(parts[i], dev));
    }

    static void AddToHistory(string commandText)
    {
        string cmdPart = commandText;
        string extra = "";
        int sp = commandText.IndexOf(' ');
        if (sp >= 0)
        {
            cmdPart = commandText[..sp];
            extra = commandText[(sp + 1)..].TrimEnd(' ');
        }
        // 原版 AddToHistory 会去掉命令末尾的一个空格
        if (cmdPart.EndsWith(" ", StringComparison.Ordinal))
            cmdPart = cmdPart[..^1];

        lock (s_sync)
        {
            for (int i = s_history.Count - 1; i >= 0; i--)
            {
                var h = s_history[i];
                if (!string.Equals(h.Command, cmdPart, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.Equals(h.Extra, extra, StringComparison.OrdinalIgnoreCase))
                    continue;
                s_history.RemoveAt(i);
            }
            s_history.Add(new HistoryEntry(commandText, cmdPart, extra));
            while (s_history.Count > MaxHistory)
                s_history.RemoveAt(0);
        }
    }

    // ==================== condump ====================

    static void DumpToFile()
    {
        var dir = EngineCommands.GetGameDirectory();
        string fileName = "";
        bool found = false;
        for (int i = 0; i < MaxDumpFiles; i++)
        {
            fileName = $"condump{i:000}.txt";
            var candidate = string.IsNullOrEmpty(dir) ? fileName : Path.Combine(dir, fileName);
            if (!File.Exists(candidate))
            {
                found = true;
                break;
            }
        }
        if (!found)
        {
            AppendLocal("Can't condump! Too many existing condump output files in the gamedir!\n", false);
            return;
        }

        var path = string.IsNullOrEmpty(dir) ? fileName : Path.Combine(dir, fileName);
        try
        {
            var sb = new System.Text.StringBuilder();
            lock (s_sync)
            {
                foreach (var line in s_lines)
                    sb.Append(line.Text).Append('\n');
            }
            var text = sb.ToString().Replace("\n", "\r\n");
            File.WriteAllBytes(path, EngineCommands.Ansi.GetBytes(text));
            AppendLocal("console dumped to " + fileName + "\n", false);
        }
        catch (Exception ex)
        {
            Log.Write($"ConsoleService: condump '{path}' 失败: {ex.Message}");
            AppendLocal("Unable to condump to " + fileName + "\n", false);
        }
    }
}
