using Avalonia.Controls;

using GoldSrc.Avalonia;

namespace Plugin.Engine;

/// <summary>
/// 业务窗口编排：主菜单（含游戏内暂停页）/控制台/加载覆盖窗的创建与显隐。
///
/// 接入层只提供"Avalonia 窗口 → vgui2 面板"的映射和显隐/置顶/模态原语；
/// 哪个窗口什么时候出现、加载时谁让位、原生对话框在场时怎么退让，
/// 全部是这里的策略。菜单开关本身由引擎（ActivateGameUI/HideGameUI）驱动。
/// </summary>
public static class UiWindows
{
    static readonly List<Func<Window>> s_menuFactories = new();
    static readonly List<Window> s_menuWindows = new();

    static Func<Window>? s_consoleFactory;
    static Func<Window>? s_loadingFactory;

    static Window? s_consoleWindow;
    static Window? s_loadingWindow;

    static bool s_menuWindowsCreated;
    static bool s_consoleWanted;
    static bool s_nativeTakeover;
    static bool s_loadingOverlay;
    static bool s_initialized;
    static bool s_menuOverlayOpen;

    /// <summary>
    /// 菜单窗口内有需要优先消费 ESC 的浮层（设置/退出确认）。此时 ESC 只关浮层，
    /// 不进引擎——否则引擎会直接把整个菜单关掉。其余情况 ESC 全交给引擎原版
    /// </summary>
    internal static bool MenuOverlayOpen => s_menuOverlayOpen;

    internal static void SetMenuOverlay(bool open) => s_menuOverlayOpen = open;

    /// <summary>订阅模式变化（应用启动时调用一次）</summary>
    public static void Initialize()
    {
        if (s_initialized)
            return;
        s_initialized = true;
        GameMode.ModeChanged += OnGameUiModeChanged;
    }

    // ==================== 窗口工厂注册 ====================

    /// <summary>菜单要显示哪些窗口：位置/尺寸也由应用在这里定</summary>
    public static void SetMenuWindows(params Func<Window>[] factories)
    {
        s_menuFactories.Clear();
        s_menuFactories.AddRange(factories);
    }

    /// <summary>控制台窗口工厂；显示时机由引擎 IGameConsole::Activate/Hide 驱动</summary>
    public static void SetConsoleWindow(Func<Window> factory) => s_consoleFactory = factory;

    /// <summary>加载进度覆盖窗工厂；不注册时保持原版加载界面（不接管）</summary>
    public static void SetLoadingWindow(Func<Window> factory) => s_loadingFactory = factory;

    /// <summary>显式创建并显示菜单窗口（预览用；游戏内由菜单首次可见时触发）</summary>
    public static void ShowMenuWindows() => CreateMenuWindows();

    // ==================== 菜单显隐 ====================

    static void OnGameUiModeChanged(bool gameUiVisible)
    {
        if (gameUiVisible)
        {
            // 首次打开菜单时创建菜单窗口；vgui2 接口/面板已在 GameMode.SetGameUI
            // 广播事件前就绪
            if (!s_menuWindowsCreated && GameMode.HasVGui)
            {
                s_menuWindowsCreated = true;
                try
                {
                    CreateMenuWindows();
                }
                catch (Exception ex)
                {
                    Log.Write($"UiWindows: CreateMenuWindows failed: {ex}");
                }
            }
            EngineBaseUI.NotifyMenuShown();   // 清掉加载残留的原生接管状态
        }
        SetGameUiVisible(gameUiVisible);
        // 原版 ActivateGameUI 末尾会重新 Activate 控制台（可见标志为 1 时），
        // 把它压在菜单上方；窗口顺序也要跟随
        if (gameUiVisible)
            RaiseConsole();
    }

    /// <summary>菜单显隐联动：隐藏/恢复所有窗口面板</summary>
    public static void SetGameUiVisible(bool visible)
    {
        if (visible && (s_nativeTakeover || s_loadingOverlay))
            return;   // 原生 UI 接管中（加载/错误对话框盖场），不显示我们的窗口
        foreach (var w in AllWindows())
        {
            // 控制台：菜单打开时不主动复活已关闭的控制台（原版只有引擎可见标志
            // 为 1 时 ActivateGameUI 才会重新 Activate）；菜单关闭时一起收起
            if (ReferenceEquals(w, s_consoleWindow))
            {
                if (visible && !EngineConsole.Visible)
                    continue;
                GoldSrcAvalonia.SetWindowVisible(w, visible);
                continue;
            }
            // 加载覆盖窗不跟着菜单显隐：它只由 SetLoadingOverlay 开关。
            // 否则加载完按 ESC 开菜单时会被这里重新显示，直接盖住整个菜单
            if (visible && ReferenceEquals(w, s_loadingWindow))
                continue;
            GoldSrcAvalonia.SetWindowVisible(w, visible);
        }
        if (!visible)
        {
            // 引擎 HideGameUI 一般已经调过 IGameConsole::Hide；这里兜底同步状态，
            // 避免控制台状态与面板可见性脱节后 SyncConsole 又把它拉回来
            EngineConsole.NotifyHiddenByMenu();
            s_consoleWanted = false;
            GoldSrcAvalonia.SetPinnedWindow(null);
            GoldSrcAvalonia.SetInputModal(null);
            // 关菜单时丢弃拖拽捕获、释放键盘焦点并摘掉所有窗口的 IME 关联
            GoldSrcAvalonia.ResetInputState();
        }
    }

    /// <summary>
    /// 原生 UI 接管（GameUI 自己弹出的加载/错误对话框盖场）：
    /// 接管期间隐藏我们的窗口让位原生界面（加载进度条可见），结束后按菜单状态恢复。
    /// 由 GameMenuReplacer.Reconcile 每帧驱动，幂等
    /// </summary>
    public static void SetNativeTakeover(bool takeover)
    {
        if (s_nativeTakeover == takeover)
            return;
        s_nativeTakeover = takeover;
        Log.Write($"UiWindows: native takeover={takeover}");
        if (takeover)
            SetGameUiVisible(false);
        else if (GameMode.Current == GameMode.UiMode.GameUI)
            SetGameUiVisible(true);
    }

    // ==================== 加载进度覆盖窗 ====================

    /// <summary>
    /// 加载覆盖窗显隐：显示时隐藏其它所有窗口（菜单），只留覆盖窗在最上；
    /// 隐藏后按当前模式恢复菜单窗口。应用没注册覆盖窗时返回 false，
    /// 调用方据此退回原版加载界面
    /// </summary>
    public static bool SetLoadingOverlay(bool active)
    {
        if (active)
        {
            EnsureLoadingWindow();
            if (s_loadingWindow == null)
                return false;

            if (!s_loadingOverlay)
            {
                s_loadingOverlay = true;
                foreach (var w in AllWindows())
                    if (!ReferenceEquals(w, s_loadingWindow))
                        GoldSrcAvalonia.SetWindowVisible(w, false);
                GoldSrcAvalonia.ResetInputState();
            }
            GoldSrcAvalonia.SetWindowVisible(s_loadingWindow, true);
            GoldSrcAvalonia.ActivateWindow(s_loadingWindow);
            return true;
        }

        // 即使状态位已经是 false，也把面板压一次：加载结束后它绝不能再留在屏幕上
        if (!s_loadingOverlay)
        {
            if (s_loadingWindow != null)
                GoldSrcAvalonia.SetWindowVisible(s_loadingWindow, false);
            return true;
        }
        s_loadingOverlay = false;
        if (s_loadingWindow != null)
            GoldSrcAvalonia.SetWindowVisible(s_loadingWindow, false);
        if (GameMode.Current == GameMode.UiMode.GameUI)
            SetGameUiVisible(true);
        // 加载前开着的控制台（例如在控制台里 map）加载结束后继续显示
        EngineConsole.RestoreAfterOverlay();
        return true;
    }

    /// <summary>首次加载时才创建覆盖窗（应用通过 SetLoadingWindow 注册工厂）</summary>
    static void EnsureLoadingWindow()
    {
        if (s_loadingWindow != null)
            return;
        var factory = s_loadingFactory;
        if (factory == null)
            return;
        try
        {
            var window = factory();
            s_loadingWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(s_loadingWindow, window))
                    s_loadingWindow = null;
            };
            window.Show();
            Log.Write("UiWindows: 加载覆盖窗已创建");
        }
        catch (Exception ex)
        {
            Log.Write($"UiWindows: 创建加载覆盖窗失败: {ex}");
        }
    }

    // ==================== 控制台窗口 ====================

    /// <summary>
    /// 控制台显隐。调用方：IGameConsole::Activate/Hide 钩子（引擎时机），
    /// 或应用侧按钮。窗口延迟创建、关闭只隐藏不销毁
    /// </summary>
    internal static void SetConsoleVisible(bool visible)
    {
        s_consoleWanted = visible;
        if (visible)
        {
            EnsureConsoleWindow();
            if (s_consoleWindow == null)
                return;   // 应用尚未注册控制台窗口，SyncConsole 每帧重试
            // 控制台不是模态：游戏内按 ~ 时它和暂停页同时在场，两边都要能点
            GoldSrcAvalonia.SetPinnedWindow(s_consoleWindow);
            GoldSrcAvalonia.SetWindowVisible(s_consoleWindow, true);
            GoldSrcAvalonia.ActivateWindow(s_consoleWindow);
            return;
        }

        if (s_consoleWindow != null)
        {
            GoldSrcAvalonia.SetWindowVisible(s_consoleWindow, false);
            GoldSrcAvalonia.ReleaseKeyboardFocus(s_consoleWindow);
        }
        GoldSrcAvalonia.SetPinnedWindow(null);
        GoldSrcAvalonia.SetInputModal(null);
    }

    /// <summary>把控制台抬到菜单之上（ActivateGameUI 末尾原版会再次 Activate）</summary>
    static void RaiseConsole()
    {
        if (!s_consoleWanted || s_consoleWindow == null)
            return;
        GoldSrcAvalonia.ActivateWindow(s_consoleWindow);
    }

    /// <summary>每帧兜底：首次显示请求早于应用/面板就绪时补建、补显示</summary>
    internal static void SyncConsole()
    {
        if (!s_consoleWanted)
            return;
        if (s_consoleWindow == null)
        {
            EnsureConsoleWindow();
            if (s_consoleWindow == null)
                return;
        }
        // vgui2 晚就绪时面板是延迟创建的：只要目标仍是"显示"，就把可见性再要一次
        if (!GoldSrcAvalonia.IsWindowPanelVisible(s_consoleWindow))
            GoldSrcAvalonia.SetWindowVisible(s_consoleWindow, true);
    }

    static void EnsureConsoleWindow()
    {
        if (s_consoleWindow != null)
            return;
        var factory = s_consoleFactory;
        if (factory == null)
            return;
        try
        {
            var window = factory();
            s_consoleWindow = window;
            window.Closed += (_, _) =>
            {
                if (!ReferenceEquals(s_consoleWindow, window))
                    return;
                s_consoleWindow = null;
            };
            window.Show();
            Log.Write("UiWindows: 控制台窗口已创建");
        }
        catch (Exception ex)
        {
            Log.Write($"UiWindows: 创建控制台窗口失败: {ex}");
        }
    }

    // ==================== 菜单窗口 ====================

    /// <summary>
    /// 首次打开菜单时创建菜单窗口。这里用的就是普通 Avalonia Window——
    /// 平台会自动给它分配 vgui2 面板，不需要手写窗口类。
    /// </summary>
    static void CreateMenuWindows()
    {
        if (s_menuWindowsCreated && s_menuWindows.Count > 0)
            return;
        s_menuWindowsCreated = true;

        // 窗口由应用注册，位置/尺寸也由应用定：业务层不认识 BlazoniaWindow 这些具体类型
        foreach (var factory in s_menuFactories)
            ShowMenuWindow(factory);
        Log.Write($"UiWindows: menu windows created ({s_menuWindows.Count})");
    }

    /// <summary>单个窗口失败不影响其余窗口，异常写进日志</summary>
    static void ShowMenuWindow(Func<Window> factory)
    {
        Window? window = null;
        try
        {
            window = factory();
            s_menuWindows.Add(window);
            window.Closed += (_, _) => s_menuWindows.Remove(window);
            window.Show();
        }
        catch (Exception ex)
        {
            if (window != null)
                s_menuWindows.Remove(window);
            Log.Write($"UiWindows: 创建窗口 '{window?.Title ?? "?"}' 失败: {ex}");
        }
    }

    // ==================== 内部 ====================

    /// <summary>当前所有已创建的 Avalonia 窗口（业务窗口 + 弹层）</summary>
    static IReadOnlyList<Window> AllWindows() => GoldSrcAvalonia.Windows;

    /// <summary>插件卸载：解绑事件、丢弃窗口引用</summary>
    public static void Reset()
    {
        if (s_initialized)
            GameMode.ModeChanged -= OnGameUiModeChanged;
        s_initialized = false;
        s_menuFactories.Clear();
        s_menuWindows.Clear();
        s_consoleWindow = null;
        s_loadingWindow = null;
        s_menuWindowsCreated = false;
        s_consoleWanted = false;
        s_nativeTakeover = false;
        s_loadingOverlay = false;
        s_menuOverlayOpen = false;
    }
}
