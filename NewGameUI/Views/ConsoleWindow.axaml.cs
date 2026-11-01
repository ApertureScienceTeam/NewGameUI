using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

using GoldSrc.Avalonia;
using Plugin.Engine;

namespace Plugin.Views;

/// <summary>
/// Avalonia 版控制台窗口：接管 GameUI CConsoleDialog 的显示位置与交互。
///
/// 显隐由接入层驱动（引擎 IGameConsole::Activate/Hide），这里只负责内容：
/// 输出（Printf/DPrintf 双色）、回显、命令历史（UP/DOWN）、TAB 补全、
/// 波浪号关闭（原版：行尾 ~ 且未按 Alt/Ctrl 时关闭）、ESC 打开菜单。
/// 时间戳守卫用于忽略"打开控制台的那次 ~ 按键"随后作为 WM_CHAR 到达。
/// </summary>
public partial class ConsoleWindow : Window
{
    const int FallbackW = 1280;
    const int FallbackH = 720;
    const int MaxRenderLines = 3000;
    const int MaxCompletionItems = 10;
    const int TildeGuardMs = 300;

    static List<string>? s_allNames;

    readonly List<string> _matches = new();
    readonly List<TextBlock> _completionRows = new();
    string _completionPrefix = "";
    int _matchIndex = -1;
    int _historyIndex = -1;
    string _draft = "";
    bool _suppressTextChanged;
    bool _scrollQueued;
    int _renderedLines;
    DateTime _shownAt = DateTime.MinValue;

    public ConsoleWindow()
    {
        InitializeComponent();

        ApplyGeometry();

        TitleText.Text = EngineLocalize.FindOrDefault("GameUI_Console", "控制台");
        SubmitButton.Content = EngineLocalize.FindOrDefault("GameUI_Submit", "提交");

        foreach (var line in ConsoleService.SnapshotLines())
            AppendLineCore(line);
        ScrollToEnd();

        // Tab/上下键要在 TextBox 自身和窗口导航之前处理
        Entry.AddHandler(KeyDownEvent, OnEntryKeyDown, RoutingStrategies.Tunnel);
        Entry.TextChanged += OnEntryTextChanged;

        ConsoleService.LineAdded += OnLineAdded;
        ConsoleService.Cleared += OnCleared;
        EngineConsole.VisibilityChanged += OnVisibilityChanged;
        // 复制走窗口层：hosted 窗口里点历史文字不保证把键盘焦点从输入行移走，
        // 挂在 SelectableTextBlock 自身按键处理上的 Ctrl+C 就收不到。
        // handledEventsToo：控件自己的处理会把按下/按键事件标成已处理，普通挂接收不到
        AddHandler(KeyDownEvent, OnCopyShortcut, RoutingStrategies.Tunnel, handledEventsToo: true);
        HistoryText.AddHandler(PointerPressedEvent, OnHistoryPointerPressed,
            RoutingStrategies.Bubble, handledEventsToo: true);
        Closed += (_, _) =>
        {
            ConsoleService.LineAdded -= OnLineAdded;
            ConsoleService.Cleared -= OnCleared;
            EngineConsole.VisibilityChanged -= OnVisibilityChanged;
        };

        if (EngineConsole.Visible)
            OnShown();
    }

    /// <summary>
    /// 原版 CGameConsole::Initialize 的几何：40/400/560 按 vgui 比例（屏幕高/480）
    /// 换算，并夹在屏幕内（留出左右各 40*scale 的边距）
    /// </summary>
    void ApplyGeometry()
    {
        var (sw, sh) = GoldSrcAvalonia.ScreenSize;
        if (sw <= 0 || sh <= 0)
            (sw, sh) = (FallbackW, FallbackH);

        double scale = sh / 480.0;
        int inset = (int)Math.Round(40 * scale);
        int w = Math.Min(sw - inset * 2, (int)Math.Round(400 * scale));
        int h = Math.Min(sh - inset * 2, (int)Math.Round(560 * scale));
        w = Math.Max(w, 360);
        h = Math.Max(h, 260);

        int x = Math.Clamp(inset, 0, Math.Max(0, sw - w));
        int y = Math.Clamp(inset, 0, Math.Max(0, sh - h));
        Position = new PixelPoint(x, y);
        Width = w;
        Height = h;
        MinWidth = 360;
        MinHeight = 260;
    }

    // ==================== 输出 ====================

    void OnLineAdded(ConsoleLine line)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnLineAdded(line));
            return;
        }
        AppendLineCore(line);
        ScrollToEnd();
    }

    void AppendLineCore(ConsoleLine line)
    {
        var inlines = HistoryText.Inlines!;
        var brush = line.IsDevMessage ? ResourceBrush("ConsoleDevBrush") : ResourceBrush("ConsolePrintBrush");
        inlines.Add(new Run(line.Text) { Foreground = brush });
        inlines.Add(new LineBreak());
        _renderedLines++;

        // 长时间运行只保留最近若干行（原生 RichText 无上限，这里防止 UI 越来越卡）
        if (_renderedLines > MaxRenderLines)
        {
            int dropLines = Math.Min(200, _renderedLines);
            int dropInlines = Math.Min(inlines.Count, dropLines * 2);
            for (int i = 0; i < dropInlines; i++)
                inlines.RemoveAt(0);
            _renderedLines -= dropLines;
        }
    }

    IBrush ResourceBrush(string key) =>
        (IBrush?)Resources[key] ?? Brushes.White;

    void OnCleared()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnCleared);
            return;
        }
        HistoryText.Inlines!.Clear();
        _renderedLines = 0;
        HideCompletion();
    }

    void ScrollToEnd()
    {
        if (_scrollQueued)
            return;
        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            try
            {
                HistoryScroll.ScrollToEnd();
            }
            catch
            {
            }
        }, DispatcherPriority.Background);
    }

    // ==================== 复制选中文字 ====================

    /// <summary>
    /// Ctrl+C 复制历史选区。放在窗口层而不是依赖 SelectableTextBlock 自身：
    /// hosted 窗口里点历史文字不保证把键盘焦点从输入行移走，控件级按键处理就收不到
    /// </summary>
    void OnCopyShortcut(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || (e.KeyModifiers & KeyModifiers.Control) == 0)
            return;
        if (TryCopyHistory())
            e.Handled = true;
    }

    /// <summary>右键历史区同样复制（原版 vgui 控制台的习惯），与 Ctrl+C 共用一条复制路径</summary>
    void OnHistoryPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(HistoryText).Properties.IsRightButtonPressed && TryCopyHistory())
            e.Handled = true;
    }

    /// <summary>复制历史选区。输入行自身有选区时让给 TextBox 处理</summary>
    bool TryCopyHistory()
    {
        var text = HistoryText.SelectedText;
        if (text.Length == 0 || Entry.SelectionStart != Entry.SelectionEnd)
            return false;

        CopyToClipboard(text);
        return true;
    }

    async void CopyToClipboard(string text)
    {
        var clipboard = TopLevel.GetTopLevel(HistoryText)?.Clipboard;
        if (clipboard != null)
        {
            try
            {
                await clipboard.SetTextAsync(text);
            }
            catch
            {
            }
        }
        // 点选可能把焦点带到历史区，复制完还给输入行
        Entry.Focus();
    }

    // ==================== 显隐 ====================

    void OnVisibilityChanged(bool visible)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnVisibilityChanged(visible));
            return;
        }
        if (visible)
            OnShown();
        else
            HideCompletion();
    }

    void OnShown()
    {
        _shownAt = DateTime.UtcNow;
        _historyIndex = -1;
        HideCompletion();
        ScrollToEnd();
        Dispatcher.UIThread.Post(() =>
        {
            Entry.Focus();
            Entry.CaretIndex = Entry.Text?.Length ?? 0;
        }, DispatcherPriority.Input);
    }

    void OnCloseClick(object? sender, RoutedEventArgs e) => EngineConsole.Hide();

    void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    void OnResizeGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginResizeDrag(WindowEdge.SouthEast, e);
    }

    // ==================== 输入 ====================

    void OnSubmitClick(object? sender, RoutedEventArgs e)
    {
        Submit();
        Entry.Focus();
    }

    void Submit()
    {
        var text = Entry.Text ?? "";
        SetEntryText("");
        _historyIndex = -1;
        _draft = "";
        HideCompletion();
        ConsoleService.Submit(text);
        Entry.Focus();
        ScrollToEnd();
    }

    void OnEntryKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Submit();
                break;
            case Key.Tab:
                e.Handled = true;
                Complete((e.KeyModifiers & KeyModifiers.Shift) != 0);
                break;
            case Key.Up:
                e.Handled = true;
                Recall(-1);
                break;
            case Key.Down:
                e.Handled = true;
                Recall(1);
                break;
            case Key.Escape:
                e.Handled = true;
                OnEscape();
                break;
        }
    }

    /// <summary>
    /// ESC 只关控制台：底下的菜单（游戏内是暂停页）保持打开，
    /// 回游戏只能通过"返回游戏"（等价原版菜单里关控制台的行为）
    /// </summary>
    void OnEscape()
    {
        GoldSrcAvalonia.ReleaseKeyboardFocus();
        EngineConsole.Hide();
    }

    void OnEntryTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged)
            return;

        var text = Entry.Text ?? "";

        // 原版：行尾 ~ / ` 且未按 Alt/Ctrl 时关闭控制台
        if (text.Length > 0 && (text[^1] == '~' || text[^1] == '`') &&
            !IsKeyDown(0x12) && !IsKeyDown(0x11))
        {
            var trimmed = text[..^1];
            SetEntryText(trimmed);
            // 打开控制台的那次 ~ 会晚一步作为字符消息到达，别让它立刻关掉窗口
            if ((DateTime.UtcNow - _shownAt).TotalMilliseconds > TildeGuardMs)
                EngineConsole.Hide();
            return;
        }

        UpdateCompletion(text);
    }

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    void SetEntryText(string text)
    {
        _suppressTextChanged = true;
        Entry.Text = text;
        Entry.CaretIndex = text.Length;
        _suppressTextChanged = false;
        HideCompletion();
    }

    // ==================== 历史 ====================

    void Recall(int direction)
    {
        var history = ConsoleService.SnapshotHistory();
        if (history.Length == 0)
            return;

        if (direction < 0)
        {
            if (_historyIndex < 0)
            {
                _draft = Entry.Text ?? "";
                _historyIndex = history.Length - 1;
            }
            else if (_historyIndex > 0)
            {
                _historyIndex--;
            }
            SetEntryText(history[_historyIndex]);
            return;
        }

        if (_historyIndex < 0)
            return;
        _historyIndex++;
        if (_historyIndex >= history.Length)
        {
            _historyIndex = -1;
            SetEntryText(_draft);
        }
        else
        {
            SetEntryText(history[_historyIndex]);
        }
    }

    // ==================== 补全 ====================

    /// <summary>引擎命令 + cvar 名（预览进程为空，只影响补全）</summary>
    static IReadOnlyList<string> AllNames
    {
        get
        {
            if (s_allNames != null)
                return s_allNames;
            var merged = new List<string>();
            merged.AddRange(EngineCommands.GetCommandNames());
            merged.AddRange(EngineCommands.GetCvarNames());
            merged.Sort(StringComparer.OrdinalIgnoreCase);
            var dedup = new List<string>(merged.Count);
            foreach (var name in merged)
            {
                if (dedup.Count == 0 ||
                    !string.Equals(dedup[^1], name, StringComparison.OrdinalIgnoreCase))
                    dedup.Add(name);
            }
            s_allNames = dedup;
            return dedup;
        }
    }

    static string FirstToken(string text)
    {
        int space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }

    void UpdateCompletion(string text)
    {
        var prefix = FirstToken(text);
        if (prefix.Length == 0)
        {
            HideCompletion();
            return;
        }

        var matches = new List<string>();
        foreach (var name in AllNames)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                matches.Add(name);
        }
        if (matches.Count == 0)
        {
            HideCompletion();
            return;
        }

        _matches.Clear();
        _matches.AddRange(matches);
        _completionPrefix = prefix;
        _matchIndex = -1;
        ShowCompletionMatches();
    }

    void ShowCompletionMatches()
    {
        CompletionList.Children.Clear();
        _completionRows.Clear();

        int shown = Math.Min(_matches.Count, MaxCompletionItems);
        for (int i = 0; i < shown; i++)
        {
            var name = _matches[i];
            var row = new TextBlock
            {
                Text = name,
                FontFamily = new FontFamily("Consolas, Courier New"),
                FontSize = 12,
                Padding = new Thickness(6, 2),
                Foreground = new SolidColorBrush(Color.Parse("#CFE0EE")),
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            row.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                ApplyCompletion(name);
            };
            _completionRows.Add(row);
            CompletionList.Children.Add(row);
        }
        if (_matches.Count > shown)
        {
            CompletionList.Children.Add(new TextBlock
            {
                Text = "...",
                FontFamily = new FontFamily("Consolas, Courier New"),
                FontSize = 12,
                Padding = new Thickness(6, 2),
                Foreground = new SolidColorBrush(Color.Parse("#7F92A5")),
            });
        }

        HighlightCompletion();
        CompletionPanel.IsVisible = true;
    }

    void HighlightCompletion()
    {
        for (int i = 0; i < _completionRows.Count; i++)
            _completionRows[i].Background = i == _matchIndex
                ? new SolidColorBrush(Color.Parse("#2C3D4E"))
                : Brushes.Transparent;
    }

    void Complete(bool reverse)
    {
        var text = Entry.Text ?? "";
        var prefix = FirstToken(text);
        if (prefix.Length == 0)
            return;

        if (_matches.Count == 0 || !string.Equals(_completionPrefix, prefix, StringComparison.Ordinal))
        {
            UpdateCompletion(text);
            if (_matches.Count == 0)
                return;
        }

        if (_matches.Count == 1)
        {
            _matchIndex = 0;
            CycleToMatch(0, keepList: false);
            return;
        }

        int next = reverse
            ? (_matchIndex <= 0 ? _matches.Count - 1 : _matchIndex - 1)
            : (_matchIndex + 1) % _matches.Count;
        CycleToMatch(next, keepList: true);
    }

    /// <summary>Tab 循环：保留列表便于继续切换；点击/唯一匹配则收起列表</summary>
    void CycleToMatch(int index, bool keepList)
    {
        _matchIndex = index;
        var name = _matches[index];
        var text = Entry.Text ?? "";
        int space = text.IndexOf(' ');
        var rest = space < 0 ? "" : text[space..];
        var completed = rest.Length > 0 ? name + rest : name + " ";

        _suppressTextChanged = true;
        Entry.Text = completed;
        Entry.CaretIndex = completed.Length;
        _suppressTextChanged = false;

        if (keepList)
        {
            HighlightCompletion();
            CompletionPanel.IsVisible = true;
        }
        else
        {
            HideCompletion();
        }
    }

    /// <summary>点击补全项：原版 OnMenuItemSelected -> SetText + 末尾补空格</summary>
    void ApplyCompletion(string name)
    {
        var text = Entry.Text ?? "";
        int space = text.IndexOf(' ');
        var rest = space < 0 ? "" : text[space..];
        SetEntryText(rest.Length > 0 ? name + rest : name + " ");
        Entry.Focus();
    }

    void HideCompletion()
    {
        CompletionPanel.IsVisible = false;
        CompletionList.Children.Clear();
        _completionRows.Clear();
        _matches.Clear();
        _completionPrefix = "";
        _matchIndex = -1;
    }
}
