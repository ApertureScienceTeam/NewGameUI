using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using Blazonia.Controls;

using GoldSrc.Avalonia;
using Plugin.Engine;

namespace Plugin.Views;

/// <summary>
/// 全屏主菜单窗口：窗口只有一份，页面按"是否在游戏中"切换——
/// 未进入对局显示主页面（左侧品牌 + 导航，右侧内容区切换 Blazonia razor 表单），
/// 进入对局且被引擎 ESC 打开时显示暂停页（居中按钮列）。
///
/// 菜单开关完全交给引擎（ActivateGameUI / HideGameUI）：HUD 显隐、输入态、
/// client 菜单收放与原版完全一致，本窗口只决定显示哪一页。
/// 每次切页/打开内容页都新建 razor 组件实例，cvar/config.cfg 因此每次重新读取。
/// 窗口尺寸在构造时取 vgui2 屏幕大小（预览进程无引擎，退回 1280x720）
/// </summary>
public partial class MainMenuWindow : Window
{
    const int FallbackW = 1280;
    const int FallbackH = 720;

    public MainMenuWindow()
    {
        InitializeComponent();

        var (w, h) = GoldSrcAvalonia.ScreenSize;
        if (w <= 0 || h <= 0)
            (w, h) = (FallbackW, FallbackH);
        Width = w;
        Height = h;
        Position = new PixelPoint(0, 0);

        KeyDown += OnWindowKeyDown;
        // 页面形态只看对局状态：模式切换（进出菜单）与对局结束都可能改变它
        GameMode.ModeChanged += OnGameModeChanged;
        GameMode.MatchEnded += OnMatchEnded;
        SyncMode();
        Select(NavNewGame, () => new BlazoniaControl<RazorPages.NewGameForm>());
    }

    void OnGameModeChanged(bool gameUiVisible) => SyncMode();

    void OnMatchEnded()
    {
        SyncMode();
        Select(NavNewGame, () => new BlazoniaControl<RazorPages.NewGameForm>());
    }

    /// <summary>进入过对局 -> 暂停页；否则主页面。菜单显隐由引擎驱动，这里只管页面</summary>
    void SyncMode()
    {
        bool inGame = GameMode.EnteredGame;
        MainShell.IsVisible = !inGame;
        PausePage.IsVisible = inGame;
        if (!inGame)
        {
            // 回到主页面时浮层不能留着
            CloseSettings();
            CloseQuitDialog();
        }
    }

    /// <summary>浮层在场时 ESC 归 Avalonia（只关浮层），否则交给引擎关闭菜单</summary>
    void UpdateOverlayState() =>
        UiWindows.SetMenuOverlay(SettingsBackdrop.IsVisible || QuitDialogBackdrop.IsVisible);

    /// <summary>导航选中态通过 Classes.selected 驱动 NavButton 模板（见 axaml）</summary>
    void Select(Button nav, Func<Control> createContent)
    {
        ContentHost.Content = createContent();
        NavNewGame.Classes.Set("selected", nav == NavNewGame);
        NavFindServers.Classes.Set("selected", nav == NavFindServers);
        NavOptions.Classes.Set("selected", nav == NavOptions);
        NavQuit.Classes.Set("selected", nav == NavQuit);
    }

    // ==================== 主页面导航 ====================

    void OnNavNewGame(object? sender, RoutedEventArgs e) =>
        Select(NavNewGame, () => new BlazoniaControl<RazorPages.NewGameForm>());

    // 服务器浏览器是内容区页面（与新建游戏/选项一致）；页面内部的服务器信息、
    // 添加服务器才用模态浮层
    void OnNavFindServers(object? sender, RoutedEventArgs e) =>
        Select(NavFindServers, () => new BlazoniaControl<RazorPages.ServerBrowserForm>());

    void OnNavOptions(object? sender, RoutedEventArgs e) =>
        Select(NavOptions, () => new BlazoniaControl<RazorPages.OptionsForm>());

    // ==================== 返回游戏 / 断开链接 ====================

    void OnResumeClick(object? sender, RoutedEventArgs e) => CloseMenu();

    /// <summary>返回游戏：EngineBaseUI 关菜单并复位 HUD 门控。先解除键盘抓取，避免残留焦点</summary>
    void CloseMenu()
    {
        GoldSrcAvalonia.ReleaseKeyboardFocus();
        EngineBaseUI.CloseGameMenu();
    }

    void OnDisconnectClick(object? sender, RoutedEventArgs e)
    {
        CloseSettings();
        EngineCommands.ClientCmd("disconnect");
        // 断开即无对局可返回：GameUI 保持打开，页面回到主菜单形态
        GameMode.NotifyMatchEnded();
    }

    // ==================== 暂停页游戏设置 ====================

    void OnPauseSettingsClick(object? sender, RoutedEventArgs e)
    {
        // 每次打开都新建实例：OptionsForm 在 OnInitialized 里读 cvar，
        // 复用旧实例会显示打开菜单之前的过期值（与主菜单切页行为一致）
        SettingsContent.Content = new BlazoniaControl<RazorPages.OptionsForm>();
        SettingsHost.Width = Math.Clamp(Width - 80, 640, 1080);
        SettingsHost.Height = Math.Clamp(Height - 60, 480, 680);
        SettingsBackdrop.IsVisible = true;
        UpdateOverlayState();
    }

    void CloseSettings()
    {
        if (!SettingsBackdrop.IsVisible)
            return;
        SettingsBackdrop.IsVisible = false;
        // 释放表单实例：下次打开重新读 cvar
        SettingsContent.Content = null;
        UpdateOverlayState();
    }

    void OnSettingsCloseClick(object? sender, RoutedEventArgs e) => CloseSettings();

    void OnSettingsBackdropTapped(object? sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.Source, SettingsBackdrop))
            CloseSettings();
    }

    // ==================== 退出确认 ====================

    void OnQuit(object? sender, RoutedEventArgs e) => ShowQuitDialog();

    void ShowQuitDialog()
    {
        QuitDialogBackdrop.IsVisible = true;
        QuitCancel.Focus();
        UpdateOverlayState();
    }

    void CloseQuitDialog()
    {
        if (!QuitDialogBackdrop.IsVisible)
            return;
        QuitDialogBackdrop.IsVisible = false;
        UpdateOverlayState();
    }

    // 确认框/设置浮层打开时 ESC 只关浮层（此时按键由 Key_Event 投给本窗口）。
    // 没有浮层时 ESC 根本不会到这里：Key_Event 原样交给引擎开关，与原版一致；
    // 只有文本控件抓着键盘（WndProc 侧截获）时才会走到下面，同样收敛到 CloseMenu
    void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (QuitDialogBackdrop.IsVisible)
        {
            if (e.Key == Key.Escape)
            {
                CloseQuitDialog();
                e.Handled = true;
            }
            return;
        }
        if (e.Key != Key.Escape)
            return;
        if (SettingsBackdrop.IsVisible)
        {
            CloseSettings();
            e.Handled = true;
            return;
        }
        // 刚启动的主菜单没有对局可返回，ESC 只吞掉（原版同样无动作）
        if (!GameMode.EnteredGame)
        {
            e.Handled = true;
            return;
        }
        CloseMenu();
        e.Handled = true;
    }

    void OnQuitBackdropTapped(object? sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.Source, QuitDialogBackdrop))
            CloseQuitDialog();
    }

    void OnQuitCancel(object? sender, RoutedEventArgs e) => CloseQuitDialog();

    void OnQuitConfirmed(object? sender, RoutedEventArgs e)
    {
        CloseQuitDialog();
#if !METAHOOK_PLUGINS
        Close(); // 预览进程没有引擎，关窗口验证完整流程
#else
        EngineCommands.ClientCmd("quit");
#endif
    }
}
