using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using GoldSrc.Avalonia;

namespace Plugin.Engine;

/// <summary>
/// 关菜单的编排（"返回游戏"、菜单态 ESC、控制台关闭后的回游戏都收敛到这里）。
///
/// 1) 直接调引擎原始 CBaseUI::HideGameUI 关菜单——不发送 ESC：引擎把 ESC 绑给
///    client 的 cancelselect（hw.dll 按键派发），发一次就会把 client 当前的
///    选队伍/选兵种菜单关掉，返回游戏后选角界面消失。
/// 2) 它不复位 HUD 绘制门控（hw.dll 每帧读 IGameUI::IsGameUIActive 决定是否调
///    HUD_Redraw），表现为回游戏后 HUD 不画。稍后在游戏线程重放一次
///    CBaseUI::HideGameUI（引擎 ESC 隐藏分支里的同一动作）把门控归零。
/// </summary>
public static class EngineBaseUI
{
    static IntPtr s_gameUI;    // GameUI007（IGameUI），读 HUD 门控状态用
    static bool s_warned;

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    static extern IntPtr GetModuleHandleA(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    static unsafe void Resolve()
    {
        if (s_gameUI != IntPtr.Zero)
            return;
        var mod = GetModuleHandleA("GameUI.dll");
        if (mod == IntPtr.Zero)
            return;
        var createInterface = GetProcAddress(mod, "CreateInterface");
        if (createInterface == IntPtr.Zero)
            return;
        var fn = (delegate* unmanaged[Cdecl]<byte*, int*, void*>)createInterface;
        int retCode = 0;
        fixed (byte* name = "GameUI007\0"u8)
            s_gameUI = (IntPtr)fn(name, &retCode);
        if (s_gameUI != IntPtr.Zero)
            Log.Write($"EngineBaseUI: GameUI007 -> 0x{(ulong)s_gameUI:X}");
        else if (!s_warned)
        {
            s_warned = true;
            Log.Write("EngineBaseUI: GameUI007 未找到，加载进度钩子不可用");
        }
    }

    /// <summary>
    /// IGameUI::IsGameUIActive（虚表槽 6）——hw.dll 决定是否画 HUD 读的就是它。
    /// 读不到时返回 false（保守：不补发 ESC，避免误开菜单）
    /// </summary>
    static unsafe bool GameUiActive
    {
        get
        {
            Resolve();
            if (s_gameUI == IntPtr.Zero)
                return false;
            var vtable = *(void***)s_gameUI;
            var isActive = (delegate* unmanaged[Thiscall]<void*, int>)vtable[6];
            return isActive != null && isActive((void*)s_gameUI) != 0;
        }
    }

    /// <summary>
    /// 关闭游戏菜单并回到游戏。返回游戏按钮、菜单态 ESC 都收敛到这里。
    /// </summary>
    public static void CloseGameMenu()
    {
        if (!GameMode.EnteredGame)
            return;   // 启动主菜单没有游戏可返回，保持菜单不动（引擎原行为）

        GoldSrcAvalonia.ReleaseKeyboardFocus();
        EngineHooks.HideGameUIFromEngine();
        Log.Write($"EngineBaseUI.CloseGameMenu: 已关闭菜单 gateActive={GameUiActive}");

        _ = Task.Run(async () =>
        {
            await Task.Delay(120);
            if (!GameUiActive)
            {
                Log.Write("EngineBaseUI.CloseGameMenu: GameUI 状态已复位，跳过 HUD 门控复位");
                return;
            }
            // 后台线程只投递消息，重放 HideGameUI 在游戏线程执行
            EngineHooks.RequestHudGateReset();
        });
    }

    // ==================== 加载进度接管 ====================
    // GameUI 的 IGameUI::LoadingStarted/LoadingFinished（虚表槽 12/13，KeyValues
    // 消息名实证）由引擎在关卡加载起止时调用；加载对话框（进度条）是 GameUI 的
    // 原生面板，我们的全屏菜单必须让位才能露出原版加载界面
    static unsafe delegate* unmanaged[Thiscall]<IntPtr, sbyte*, sbyte*, void> g_pfnLoadingStarted;
    static unsafe delegate* unmanaged[Thiscall]<IntPtr, sbyte*, sbyte*, void> g_pfnLoadingFinished;

    // 进度条四件套（CLoadingDialog 收到的同一份数据，虚表槽 14/15/16/17）
    static unsafe delegate* unmanaged[Thiscall]<IntPtr, sbyte*, int, void> g_pfnStartProgressBar;
    static unsafe delegate* unmanaged[Thiscall]<IntPtr, int, float, int> g_pfnContinueProgressBar;
    static unsafe delegate* unmanaged[Thiscall]<IntPtr, byte, sbyte*, sbyte*, void> g_pfnStopProgressBar;
    static unsafe delegate* unmanaged[Thiscall]<IntPtr, sbyte*, int> g_pfnSetProgressBarStatusText;

    // 下载资源进度（虚表槽 18/19）：连接服务器下载自定义资源时引擎不调
    // LoadingStarted，只走 14/17/18/19——18 收 0..1 的 float 进度（收尾传 100.0f），
    // 19 收当前文件文本（"Downloading file 'xxx'" 或 #token），原版落在
    // CLoadingDialog 的 SecondaryProgressLabel + 第二段进度条上
    static unsafe delegate* unmanaged[Thiscall]<IntPtr, float, void> g_pfnSetSecondaryProgress;
    static unsafe delegate* unmanaged[Thiscall]<IntPtr, sbyte*, void> g_pfnSetSubStatusText;

    static bool s_loading;
    static bool s_loadingHooksInstalled;
    static bool s_progressActive;   // 进度在场（下载阶段无 LoadingStarted，靠槽 14/17/18/19 置位）
    static bool s_secondaryActive;  // 副进度条在场（首个槽 18 回调后出现，同原版切双条布局）

    // 当前进度（给 Avalonia 加载覆盖窗用）
    static string s_progressTitle = "";
    static string s_progressStatus = "";
    static string s_progressSubStatus = "";
    static double s_progressFraction;     // 主进度条（槽 15，总体：point/steps）
    static double s_secondaryFraction;    // 副进度条（槽 18，下载中当前文件 0..1）
    static int s_progressSteps;           // StartProgressBar 的 steps（主进度分母）
    static bool s_progressError;

    /// <summary>引擎正在加载关卡（LoadingStarted..LoadingFinished 之间）</summary>
    public static bool Loading => s_loading;

    /// <summary>
    /// 当前加载/下载进度（都不在场时为 Active=false）；
    /// Status/SubStatus 为引擎原文（未翻译）
    /// </summary>
    public static LoadingProgressInfo LoadingProgress { get; private set; }

    /// <summary>加载进度变化（引擎线程回调；UI 侧自行切到 UI 线程）</summary>
    public static event Action<LoadingProgressInfo>? LoadingProgressChanged;

    /// <summary>挂 LoadingStarted/LoadingFinished 钩子（幂等，GameUI007 就绪后任意时机可调）</summary>
    public static unsafe void InstallLoadingHooks()
    {
        if (s_loadingHooksInstalled)
            return;
        Resolve();
        if (s_gameUI == IntPtr.Zero)
            return;
        var api = g_pMetaHookAPI;
        delegate* unmanaged[Thiscall]<IntPtr, sbyte*, sbyte*, void> f12 = &OnLoadingStarted;
        fixed (void* p = &g_pfnLoadingStarted)
            api->VFTHook((void*)s_gameUI, 0, 12, (void*)f12, (void**)p);
        delegate* unmanaged[Thiscall]<IntPtr, sbyte*, sbyte*, void> f13 = &OnLoadingFinished;
        fixed (void* p = &g_pfnLoadingFinished)
            api->VFTHook((void*)s_gameUI, 0, 13, (void*)f13, (void**)p);

        // 14 StartProgressBar / 15 ContinueProgressBar / 16 StopProgressBar /
        // 17 SetProgressBarStatusText：原版 CLoadingDialog 就是靠这四个画进度条的
        delegate* unmanaged[Thiscall]<IntPtr, sbyte*, int, void> f14 = &OnStartProgressBar;
        fixed (void* p = &g_pfnStartProgressBar)
            api->VFTHook((void*)s_gameUI, 0, 14, (void*)f14, (void**)p);
        delegate* unmanaged[Thiscall]<IntPtr, int, float, int> f15 = &OnContinueProgressBar;
        fixed (void* p = &g_pfnContinueProgressBar)
            api->VFTHook((void*)s_gameUI, 0, 15, (void*)f15, (void**)p);
        delegate* unmanaged[Thiscall]<IntPtr, byte, sbyte*, sbyte*, void> f16 = &OnStopProgressBar;
        fixed (void* p = &g_pfnStopProgressBar)
            api->VFTHook((void*)s_gameUI, 0, 16, (void*)f16, (void**)p);
        delegate* unmanaged[Thiscall]<IntPtr, sbyte*, int> f17 = &OnSetProgressBarStatusText;
        fixed (void* p = &g_pfnSetProgressBarStatusText)
            api->VFTHook((void*)s_gameUI, 0, 17, (void*)f17, (void**)p);

        // 18 SetSecondaryProgress / 19 SetProgressBarSubStatusText：
        // 引擎下载资源时（CL_StartResourceDownloading 前后）走这两个口上报
        // 下载进度与当前文件名，下载阶段没有 LoadingStarted/Finished
        delegate* unmanaged[Thiscall]<IntPtr, float, void> f18 = &OnSetSecondaryProgress;
        fixed (void* p = &g_pfnSetSecondaryProgress)
            api->VFTHook((void*)s_gameUI, 0, 18, (void*)f18, (void**)p);
        delegate* unmanaged[Thiscall]<IntPtr, sbyte*, void> f19 = &OnSetSubStatusText;
        fixed (void* p = &g_pfnSetSubStatusText)
            api->VFTHook((void*)s_gameUI, 0, 19, (void*)f19, (void**)p);

        s_loadingHooksInstalled = true;
        Log.Write("EngineBaseUI: 加载/进度条钩子已安装（槽 12-19）");
    }

    /// <summary>把当前进度推给应用侧（订阅方自行切 UI 线程）</summary>
    static void PublishProgress()
    {
        var info = new LoadingProgressInfo(
            s_loading || s_progressActive, s_progressTitle, s_progressStatus,
            s_progressSubStatus, s_progressFraction, s_secondaryFraction,
            s_secondaryActive, s_progressError);
        LoadingProgress = info;
        LoadingProgressChanged?.Invoke(info);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    public static unsafe void OnStartProgressBar(IntPtr obj, sbyte* progressType, int steps)
    {
        s_progressFraction = 0;
        s_progressError = false;
        s_progressActive = true;
        s_progressSubStatus = "";
        s_secondaryActive = false;
        s_secondaryFraction = 0;
        s_progressSteps = steps;
        var type = Fmt(progressType);
        if (string.IsNullOrEmpty(s_progressTitle))
            s_progressTitle = type;
        Log.Write($"EngineBaseUI: StartProgressBar type={type} steps={steps}");
        PublishProgress();
        g_pfnStartProgressBar(obj, progressType, steps);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    public static unsafe int OnContinueProgressBar(IntPtr obj, int progressPoint, float fraction)
    {
        // 原版 CLoadingDialog::OnContinueProgress：主进度 = point / steps（steps 来自
        // StartProgressBar）；fraction 参数原版根本不读。steps 缺失时退回旧的启发式
        var f = s_progressSteps > 0
            ? progressPoint / (double)s_progressSteps
            : fraction > 0
                ? (fraction <= 1 ? fraction : fraction / 100.0)
                : progressPoint / 100.0;
        s_progressFraction = Math.Clamp(f, 0, 1);
        s_progressActive = true;
        PublishProgress();
        return g_pfnContinueProgressBar(obj, progressPoint, fraction);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    public static unsafe void OnStopProgressBar(IntPtr obj, byte error, sbyte* failure, sbyte* extended)
    {
        s_progressError = error != 0;
        s_progressActive = false;
        s_secondaryActive = false;
        s_secondaryFraction = 0;
        s_progressSubStatus = "";
        if (s_progressError && string.IsNullOrEmpty(s_progressStatus))
            s_progressStatus = Fmt(failure);
        if (!s_progressError)
            s_progressFraction = 1;
        Log.Write($"EngineBaseUI: StopProgressBar error={s_progressError} reason={Fmt(failure)}");
        PublishProgress();
        g_pfnStopProgressBar(obj, error, failure, extended);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    public static unsafe int OnSetProgressBarStatusText(IntPtr obj, sbyte* statusText)
    {
        // 引擎可能传 "#GameUI_xxx" 这种键，原文发布、由应用侧查表翻译
        s_progressStatus = Fmt(statusText);
        s_progressActive = true;
        PublishProgress();
        return g_pfnSetProgressBarStatusText(obj, statusText);
    }

    /// <summary>
    /// 槽 18：副进度条（下载中当前文件的字节进度 0..1；收尾引擎传 100.0f）。
    /// 首次回调即"切到双条布局"——副进度条从这里开始显示；与原版一致，
    /// 副条还没显示时收到 >0.99 的值（如收尾 100.0f）不唤出副条
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    public static unsafe void OnSetSecondaryProgress(IntPtr obj, float fraction)
    {
        if (!s_secondaryActive && fraction > 0.99f)
        {
            g_pfnSetSecondaryProgress(obj, fraction);
            return;
        }
        var f = fraction <= 1 ? fraction : fraction / 100.0;
        s_secondaryFraction = Math.Clamp(f, 0, 1);
        s_secondaryActive = true;
        s_progressError = false;
        s_progressActive = true;
        PublishProgress();
        g_pfnSetSecondaryProgress(obj, fraction);
    }

    /// <summary>槽 19：下载中的当前文件文本（"Downloading file 'xxx'" 或 #token）</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    public static unsafe void OnSetSubStatusText(IntPtr obj, sbyte* text)
    {
        s_progressSubStatus = Fmt(text);
        s_progressActive = true;
        PublishProgress();
        g_pfnSetSubStatusText(obj, text);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    public static unsafe void OnLoadingStarted(IntPtr obj, sbyte* type, sbyte* name)
    {
        s_loading = true;
        s_progressTitle = Fmt(name);
        s_progressStatus = "";
        s_progressSubStatus = "";
        s_progressFraction = 0;
        s_progressError = false;
        // 与原版一致：LoadingStarted 把加载对话框切回单条布局，副进度条收起
        s_secondaryActive = false;
        s_secondaryFraction = 0;
        Log.Write($"EngineBaseUI: LoadingStarted type={Fmt(type)} name={Fmt(name)}");
        PublishProgress();
        g_pfnLoadingStarted(obj, type, name);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    public static unsafe void OnLoadingFinished(IntPtr obj, sbyte* type, sbyte* name)
    {
        s_loading = false;
        s_progressActive = false;
        s_secondaryActive = false;
        Log.Write($"EngineBaseUI: LoadingFinished type={Fmt(type)} name={Fmt(name)}");
        // 覆盖窗收起与"是否恢复菜单"由应用侧对 LoadingProgressChanged 的订阅处理；
        // 加载失败时错误对话框可能还盖着，Reconcile 识别为原生接管继续让位
        PublishProgress();
        g_pfnLoadingFinished(obj, type, name);
    }

    static unsafe string Fmt(sbyte* p) => p == null ? "" : new string(p);

    /// <summary>菜单重新显示：清掉加载/下载残留状态，允许窗口恢复显示</summary>
    public static void NotifyMenuShown()
    {
        s_loading = false;
        s_progressActive = false;
        s_secondaryActive = false;
        UiWindows.SetNativeTakeover(false);
    }
}

/// <summary>
/// 关卡加载/资源下载进度：来源是 IGameUI 的 StartProgressBar / ContinueProgressBar /
/// StopProgressBar / SetProgressBarStatusText（虚表槽 14/15/16/17），
/// 以及下载阶段的 SetSecondaryProgress / SetSubStatusText（槽 18/19），
/// 与原版 CLoadingDialog 收到的数据同源。
/// </summary>
/// <param name="Active">是否正在加载或下载</param>
/// <param name="Title">资源名（通常是地图名，如 de_dust2）</param>
/// <param name="Status">进度条状态文本（引擎原文，"#GameUI_xxx" 这类键由应用侧查表翻译）</param>
/// <param name="SubStatus">下载中的当前文件文本（引擎原文；非下载期为空）</param>
/// <param name="Fraction">主进度条：总体进度 0..1（槽 15，point/steps；下载期为已完成文件比例）</param>
/// <param name="SecondaryFraction">副进度条：下载中当前文件进度 0..1（槽 18）</param>
/// <param name="SecondaryActive">副进度条是否显示（首个槽 18 回调后出现，同原版切双条布局）</param>
/// <param name="Error">加载失败（StopProgressBar 的 bError）</param>
public readonly record struct LoadingProgressInfo(
    bool Active,
    string Title,
    string Status,
    string SubStatus,
    double Fraction,
    double SecondaryFraction,
    bool SecondaryActive,
    bool Error)
{
    public static LoadingProgressInfo None => new(false, "", "", "", 0, 0, false, false);
}
