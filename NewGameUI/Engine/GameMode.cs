using GoldSrc.Avalonia;

namespace Plugin.Engine;

/// <summary>
/// 游戏模式状态机：把引擎 vgui2 的菜单状态（IBaseUI::ActivateGameUI / HideGameUI）
/// 与 Avalonia 的键盘焦点做双向仲裁，使两侧的窗口/焦点体系保持一致。
///
/// 输入路由规则：
///   GameUI 打开              -> 输入允许到达 Avalonia；vgui2 键盘照常（除非 Avalonia 抓取了键盘）
///   游戏内 + Avalonia 文本焦点 -> 输入允许到达 Avalonia；vgui2 键盘被吞并禁用
///   游戏内 + 无 Avalonia 焦点 -> 输入全部留给引擎游戏
/// </summary>
public static class GameMode
{
    public enum UiMode
    {
        Unknown,    // 引擎尚未发出 Activate/HideGameUI，输入判定回退到旧行为（光标探测）
        InGame,
        GameUI,
    }

    public static UiMode Current { get; private set; } = UiMode.Unknown;

    /// <summary>是否进入过对局（HideGameUI→InGame 过至少一次）。
    /// “返回游戏”按钮据此显隐：刚启动的主菜单上没有游戏可返回</summary>
    public static bool EnteredGame { get; private set; }

    /// <summary>对局结束（断开连接/被踢出/关卡离开）时广播；菜单据此回到初始形态</summary>
    public static event Action? MatchEnded;

    /// <summary>通知对局已结束：清除"进入过对局"状态（返回游戏/断开连接随之隐藏）</summary>
    public static void NotifyMatchEnded()
    {
        if (!EnteredGame)
            return;
        EnteredGame = false;
        MatchEnded?.Invoke();
    }

    /// <summary>BaseUI 钩子在模式切换时触发；窗口层据此显示/隐藏 Avalonia 界面</summary>
    public static event Action<bool>? ModeChanged;

    /// <summary>vgui2 接口与 GameUI 根面板是否就绪（面板原语可用）</summary>
    public static bool HasVGui => GoldSrcAvalonia.PanelApiReady;

    /// <summary>Avalonia 侧是否抓取了键盘（文本/键位控件持焦，由接入层按窗口焦点维护）</summary>
    public static bool SwallowVGuiKeyboard => GoldSrcAvalonia.HasKeyboardCapture;

    /// <summary>
    /// 是否允许把 BaseUI::Key_Event 转发给引擎原始实现。
    /// 仅在 GameUI 可见（vgui2 已初始化）时转发：其他时机（启动早期/加载中/控制台）
    /// 原始实现可能访问尚未就绪的 vgui2 状态导致 hw.dll 空指针崩溃
    /// </summary>
    public static bool AllowForwardVGuiKeyboard => Current == UiMode.GameUI && HasVGui;

    /// <summary>由 BaseUI::ActivateGameUI / HideGameUI 钩子调用</summary>
    public static void SetGameUI(bool visible)
    {
        var next = visible ? UiMode.GameUI : UiMode.InGame;
        if (next == Current)
            return;
        Current = next;
        if (!visible)
            EnteredGame = true;
        Log.Write($"GameMode: {(visible ? "ActivateGameUI" : "HideGameUI")} -> {Current}");
        if (!visible)
            GoldSrcAvalonia.ReleaseKeyboardFocus();
        else
            GoldSrcAvalonia.EnsurePanels();   // 面板就绪后再广播，订阅方依赖面板原语
        ModeChanged?.Invoke(visible);
    }

    /// <summary>插件卸载时调用</summary>
    public static void Reset()
    {
        Current = UiMode.Unknown;
        EnteredGame = false;
        ModeChanged = null;
        MatchEnded = null;
    }
}
