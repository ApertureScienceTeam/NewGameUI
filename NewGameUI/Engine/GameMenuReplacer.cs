using GoldSrc.Avalonia;

namespace Plugin.Engine;

/// <summary>
/// 原版 GameUI 页面替代器（业务策略，跑在接入层事件上）。
///
/// GameUI 打开时把 vgui2 GameUI 根面板（PANEL_GAMEUIDLL）下的原生子面板
/// （主菜单任务栏、原版对话框等）全部隐藏，只留 Avalonia 窗口面板；
/// GameUI 关闭时全部恢复原可见性。Blazonia 页面借此完全替代原版页面。
/// 加载期间让位逻辑同步在这里：覆盖窗在场时压掉原生加载对话框，
/// 冒出错误/消息框时反向让位原版界面。
///
/// 我们自己的窗口面板（<see cref="GoldSrcAvalonia.IsHostedPanel"/>）不在隐藏范围。
/// 已隐藏的引擎面板若被引擎重新激活（如再次 ActivateGameUI），幂等处理。
/// 引擎动向全部来自接入层事件：GameUiVisibilityChanged / LoadingProgressChanged /
/// FramePump；vgui2 操作走门面的面板原语。
/// </summary>
public static class GameMenuReplacer
{
    struct Saved
    {
        public uint Panel;
    }

    static readonly List<Saved> s_hidden = new();
    static bool s_loadingActive;

    /// <summary>订阅接入层事件（应用启动时调用一次；预览进程没有引擎事件，保持空转）</summary>
    public static void Initialize()
    {
        GameMode.ModeChanged += OnGameUiVisibilityChanged;
        EngineBaseUI.LoadingProgressChanged += OnLoadingProgressChanged;
        EngineHooks.FramePump += Reconcile;
    }

    // 事件都在引擎线程回调里触发（BaseUI::Paint / 引擎钩子），异常不能冒回引擎
    static void OnGameUiVisibilityChanged(bool visible)
    {
        try
        {
            SetReplaced(visible);
        }
        catch (Exception ex)
        {
            Log.Write($"GameMenuReplacer: {ex}");
        }
    }

    static void OnLoadingProgressChanged(LoadingProgressInfo info)
    {
        try
        {
            if (info.Active == s_loadingActive)
                return;
            s_loadingActive = info.Active;
            if (info.Active)
                EnterLoading();
            else
                LeaveLoading();
        }
        catch (Exception ex)
        {
            Log.Write($"GameMenuReplacer.Loading: {ex}");
        }
    }

    public static void SetReplaced(bool replaced)
    {
        if (!GameMode.HasVGui)
            return;
        var root = GoldSrcAvalonia.GameUiRootPanel;

        if (replaced)
        {
            if (s_hidden.Count > 0)
                return;
            int count = GoldSrcAvalonia.PanelGetChildCount(root);
            var names = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
            {
                uint child = GoldSrcAvalonia.PanelGetChild(root, i);
                if (child == 0 || GoldSrcAvalonia.IsHostedPanel(child))
                    continue;
                if (!GoldSrcAvalonia.PanelIsVisible(child))
                    continue;
                names.Append('[').Append(GoldSrcAvalonia.PanelGetName(child)).Append("] ");
                GoldSrcAvalonia.PanelSetVisible(child, false);
                s_hidden.Add(new Saved { Panel = child });
            }
            Log.Write($"GameMenuReplacer: hidden {s_hidden.Count} engine GameUI panels → {names}");
        }
        else
        {
            if (s_hidden.Count == 0)
                return;
            foreach (var s in s_hidden)
                GoldSrcAvalonia.PanelSetVisible(s.Panel, true);
            Log.Write($"GameMenuReplacer: restored {s_hidden.Count} engine GameUI panels");
            s_hidden.Clear();
        }
    }

    /// <summary>
    /// 每帧对账（FramePump，菜单可见时由 BaseUI::Paint 泵帧驱动）：
    /// 1) 引擎可能复活我们隐藏过的原生面板（如再次 ActivateGameUI），重新压下去；
    /// 2) 出现我们没藏过的新原生面板（GameUI 的加载/错误对话框）即"原生接管"，
    ///    隐藏 Avalonia 窗口让位原生界面（加载进度条可见）；面板消失后恢复。
    /// 加载进行中也视为接管，遮住对话框创建前的空窗期
    /// </summary>
    public static void Reconcile()
    {
        if (!GameMode.HasVGui)
            return;
        var root = GoldSrcAvalonia.GameUiRootPanel;

        if (EngineBaseUI.LoadingProgress.Active)
        {
            // 加载中：Avalonia 覆盖窗盖场（它自己是 popup，每帧 Activate 提到最上）。
            // 不要求"必须藏掉原生加载对话框"才接管——原版对话框名字不一定含 load，
            // 它本来就被压在覆盖窗下面；只有冒出错误/消息框时才让位原版
            if (UiWindows.SetLoadingOverlay(true))
            {
                LogLoadingChildrenOnce();
                SuppressLoadingDialogs();
                if (!HasErrorDialog())
                {
                    UiWindows.SetNativeTakeover(false);
                    return;
                }
            }
            UiWindows.SetLoadingOverlay(false);
        }

        bool native = EngineBaseUI.LoadingProgress.Active;
        int count = GoldSrcAvalonia.PanelGetChildCount(root);
        for (int i = 0; i < count; i++)
        {
            uint child = GoldSrcAvalonia.PanelGetChild(root, i);
            if (child == 0 || GoldSrcAvalonia.IsHostedPanel(child))
                continue;
            bool hidden = false;
            foreach (var s in s_hidden)
                if (s.Panel == child) { hidden = true; break; }
            if (hidden)
            {
                if (GoldSrcAvalonia.PanelIsVisible(child))
                    GoldSrcAvalonia.PanelSetVisible(child, false);
                continue;
            }
            if (GoldSrcAvalonia.PanelIsVisible(child))
                native = true;
        }
        UiWindows.SetNativeTakeover(native);
    }

    // ==================== 加载进度（接管原版 CLoadingDialog） ====================

    /// <summary>
    /// 关卡开始加载：切到 Avalonia 覆盖窗。应用没注册覆盖窗（或窗口建不出来）时
    /// 退回原版行为——让位原生加载界面
    /// </summary>
    public static void EnterLoading()
    {
        if (!UiWindows.SetLoadingOverlay(true))
        {
            UiWindows.SetNativeTakeover(true);
            return;
        }
        if (GameMode.HasVGui)
        {
            LogLoadingChildrenOnce();
            SuppressLoadingDialogs();
        }
    }

    /// <summary>关卡加载结束：收起覆盖窗，后续显示/接管交给 Reconcile 对账</summary>
    public static void LeaveLoading() => UiWindows.SetLoadingOverlay(false);

    static bool s_loggedLoadingChildren;
    static bool s_loggedLoadingPanel;

    /// <summary>加载期把 GameUI 子面板清单打一次日志，接管异常时靠它定位</summary>
    static void LogLoadingChildrenOnce()
    {
        if (s_loggedLoadingChildren)
            return;
        s_loggedLoadingChildren = true;
        var root = GoldSrcAvalonia.GameUiRootPanel;
        var sb = new System.Text.StringBuilder();
        int count = GoldSrcAvalonia.PanelGetChildCount(root);
        for (int i = 0; i < count; i++)
        {
            uint child = GoldSrcAvalonia.PanelGetChild(root, i);
            if (child == 0)
                continue;
            sb.Append('[').Append(GoldSrcAvalonia.PanelGetName(child))
              .Append(" vis=").Append(GoldSrcAvalonia.PanelIsVisible(child) ? 1 : 0)
              .Append(GoldSrcAvalonia.IsHostedPanel(child) ? " ours" : "")
              .Append("] ");
        }
        Log.Write($"GameMenuReplacer: 加载期 GameUI 子面板 → {sb}");
    }

    static bool IsLoadingPanelName(string name) =>
        name.Contains("load", StringComparison.OrdinalIgnoreCase)
        || name.Contains("progress", StringComparison.OrdinalIgnoreCase)
        || name.Contains("splash", StringComparison.OrdinalIgnoreCase);

    static bool IsErrorPanelName(string name) =>
        name.Contains("error", StringComparison.OrdinalIgnoreCase)
        || name.Contains("message", StringComparison.OrdinalIgnoreCase)
        || name.Contains("notice", StringComparison.OrdinalIgnoreCase)
        || name.Contains("confirm", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把名字像加载对话框的原生面板压下去（拿不准的留着——它在覆盖窗下面）。
    /// 只做尽力而为，不再作为是否接管的判据
    /// </summary>
    static void SuppressLoadingDialogs()
    {
        var root = GoldSrcAvalonia.GameUiRootPanel;
        int count = GoldSrcAvalonia.PanelGetChildCount(root);
        for (int i = 0; i < count; i++)
        {
            uint child = GoldSrcAvalonia.PanelGetChild(root, i);
            if (child == 0 || GoldSrcAvalonia.IsHostedPanel(child) || !GoldSrcAvalonia.PanelIsVisible(child))
                continue;

            var name = GoldSrcAvalonia.PanelGetName(child);
            if (!IsLoadingPanelName(name) || IsErrorPanelName(name))
                continue;

            GoldSrcAvalonia.PanelSetVisible(child, false);
            if (!s_loggedLoadingPanel)
            {
                s_loggedLoadingPanel = true;
                Log.Write($"GameMenuReplacer: 已隐藏原生加载对话框 '{name}'");
            }
        }
    }

    /// <summary>加载期出现错误/消息框：让位原版界面，避免把错误提示盖掉</summary>
    static bool HasErrorDialog()
    {
        var root = GoldSrcAvalonia.GameUiRootPanel;
        int count = GoldSrcAvalonia.PanelGetChildCount(root);
        for (int i = 0; i < count; i++)
        {
            uint child = GoldSrcAvalonia.PanelGetChild(root, i);
            if (child == 0 || GoldSrcAvalonia.IsHostedPanel(child) || !GoldSrcAvalonia.PanelIsVisible(child))
                continue;
            if (IsErrorPanelName(GoldSrcAvalonia.PanelGetName(child)))
                return true;
        }
        return false;
    }
}
