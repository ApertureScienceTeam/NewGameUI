using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using GoldSrc.Avalonia;
using GoldSrc.Avalonia.Engine;
using GoldSrc.HLSDK.Native;
using GoldSrc.Metahook.Native;
using GoldSrc.Metahook.VGui;

namespace Plugin.Engine;

/// <summary>
/// 引擎钩子总入口：IBaseUI、窗口过程、GameUI 生命周期、按键/鼠标转发。
///
/// 这些是"修改游戏"的部分——接入层（GoldSrc.Avalonia）只负责把 Avalonia
/// 界面显示出来并通过门面提供窗口/面板/输入原语；什么时候挂钩、挂哪些、
/// 按键怎么解释、菜单/控制台/暂停怎么编排全部由这里决定。
///
/// IBaseUI 真实 vtable 布局（1 起）：
///   1 Initialize, 2 Start, 3 Shutdown, 4 Key_Event, 5 CallEngineSurfaceAppHandler,
///   6 Paint, 7 HideGameUI, 8 ActivateGameUI, 9 HideConsole, 10 ShowConsole
/// 共 10 个虚函数，没有 IsGameUIVisible；只挂钩位对齐且实际使用的 7 个，
/// 其余透传即可（钩错签名会破坏 thiscall 栈平衡，越界槽位会写坏相邻 vtable）
/// </summary>
public static unsafe class EngineHooks
{
    static delegate* unmanaged[Thiscall]<IntPtr, IntPtr, int, void> g_pfnCBaseUI_Initialize;
    static delegate* unmanaged[Thiscall]<IntPtr, cl_enginefuncs_t*, int, void> g_pfnCBaseUI_Start;
    static delegate* unmanaged[Thiscall]<IntPtr, void> g_pfnCBaseUI_Shutdown;
    static delegate* unmanaged[Thiscall]<IntPtr, int, int, sbyte*, int> g_pfnCBaseUI_Key_Event;
    static delegate* unmanaged[Thiscall]<IntPtr, int, int, int, int, void> g_pfnCBaseUI_Paint;
    static delegate* unmanaged[Thiscall]<IntPtr, void> g_pfnCBaseUI_HideGameUI;
    static delegate* unmanaged[Thiscall]<IntPtr, void> g_pfnCBaseUI_ActivateGameUI;

    static IntPtr s_gameHwnd;
    static IntPtr s_originalWndProc;
    static int s_wndprocErrors;

    // CBaseUI 实例（各钩子的 this）与虚表指针（ShowConsole/HideConsole 未挂钩，直接按槽位调原始实现）
    static IntPtr s_baseUiObj;
    static void** s_baseUiVtable;

    static bool s_menuStateInitialized;

    /// <summary>业务逐帧对账（原版面板替换、对局结束判定等）订阅这里；引擎线程</summary>
    public static event Action? FramePump;

    // ==================== 安装 ====================

    /// <summary>挂 IBaseUI/GameUI/控制台/加载等全部引擎钩子（引擎工厂就绪后调用一次）</summary>
    public static void Install()
    {
        delegate* unmanaged[Cdecl]<sbyte*, int*, void*> fnEngineCreateInterface = g_pMetaHookAPI->GetEngineFactory();

        using var str = "BaseUI001".GetNativeString();

        var baseUI = fnEngineCreateInterface(str, null);
        s_baseUiVtable = *(void***)baseUI;

        delegate* unmanaged[Thiscall]<IntPtr, IntPtr, int, void> fun1 = &Initialize;
        fixed (void* p = &g_pfnCBaseUI_Initialize)
            g_pMetaHookAPI->VFTHook((void*)baseUI, 0, 1, (void*)fun1, (void**)p);

        delegate* unmanaged[Thiscall]<IntPtr, cl_enginefuncs_t*, int, void> fun2 = &Start;
        fixed (void* p = &g_pfnCBaseUI_Start)
            g_pMetaHookAPI->VFTHook((void*)baseUI, 0, 2, (void*)fun2, (void**)p);

        delegate* unmanaged[Thiscall]<IntPtr, void> fun3 = &Shutdown;
        fixed (void* p = &g_pfnCBaseUI_Shutdown)
            g_pMetaHookAPI->VFTHook((void*)baseUI, 0, 3, (void*)fun3, (void**)p);

        delegate* unmanaged[Thiscall]<IntPtr, int, int, sbyte*, int> fun4 = &Key_Event;
        fixed (void* p = &g_pfnCBaseUI_Key_Event)
            g_pMetaHookAPI->VFTHook((void*)baseUI, 0, 4, (void*)fun4, (void**)p);

        delegate* unmanaged[Thiscall]<IntPtr, int, int, int, int, void> fun6 = &Paint;
        fixed (void* p = &g_pfnCBaseUI_Paint)
            g_pMetaHookAPI->VFTHook((void*)baseUI, 0, 6, (void*)fun6, (void**)p);

        delegate* unmanaged[Thiscall]<IntPtr, void> fun7 = &HideGameUI;
        fixed (void* p = &g_pfnCBaseUI_HideGameUI)
            g_pMetaHookAPI->VFTHook((void*)baseUI, 0, 7, (void*)fun7, (void**)p);

        delegate* unmanaged[Thiscall]<IntPtr, void> fun8 = &ActivateGameUI;
        fixed (void* p = &g_pfnCBaseUI_ActivateGameUI)
            g_pMetaHookAPI->VFTHook((void*)baseUI, 0, 8, (void*)fun8, (void**)p);

        EngineBaseUI.InstallLoadingHooks();
        EngineConsole.InstallHooks();
        Log.Write($"EngineHooks.Install: baseUI=0x{(nint)baseUI:X}");
    }

    /// <summary>插件卸载：销毁 Avalonia 实例、还原窗口过程、解绑引擎状态</summary>
    public static void Shutdown() => Cleanup();

    /// <summary>游戏内每帧泵入口：由 HUD_Redraw 每帧调用</summary>
    public static void RenderInGame()
    {
        GoldSrcAvalonia.RenderInGame();
        UiWindows.SyncConsole();
    }

    public static delegate* unmanaged[Cdecl]<int, void> MouseEventFn => &MouseEvent;

    // ==================== 生命周期钩子 ====================

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    public static void Initialize(IntPtr obj, IntPtr factories, int count)
    {
        Log.Write("EngineHooks::Initialize enter");
        g_pfnCBaseUI_Initialize(obj, factories, count);
        // factoryList 约定：[0]=engine, [1]=vgui2, [2]=filesystem
        GoldSrcAvalonia.AcquireEngineInterfaces((void**)factories, count);
        RegisterWindowEventCallback();
        // 屏幕尺寸/平台宿主/UI 线程异常兜底由接入层统一处理
        GoldSrcAvalonia.Start(s_gameHwnd);
        Log.Write($"EngineHooks::Initialize: app started, main tid={System.Threading.Thread.CurrentThread.ManagedThreadId}");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    public static void Start(IntPtr obj, cl_enginefuncs_t* engineFuncs, int interfaceVersion)
    {
        g_pfnCBaseUI_Start(obj, engineFuncs, interfaceVersion);
        GoldSrcAvalonia.UnlockCursor();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    public static void Shutdown(IntPtr obj)
    {
        g_pfnCBaseUI_Shutdown(obj);
        Cleanup();
    }

    // 幂等清理：销毁 Avalonia 实例并还原引擎窗口过程
    public static void Cleanup()
    {
        try
        {
            GoldSrcAvalonia.Shutdown();
        }
        catch
        {
        }
        UiWindows.Reset();
        EngineConsole.Reset();
        GameMode.Reset();

        if (s_gameHwnd != IntPtr.Zero && s_originalWndProc != IntPtr.Zero)
        {
            Win32.SetWindowLongA(s_gameHwnd, Win32.GWL_WNDPROC, s_originalWndProc);
            s_originalWndProc = IntPtr.Zero;
        }
    }

    /// <summary>原生回调（WndProc / vtable 钩子）里拦截到的异常统一从这里落日志</summary>
    internal static void ReportError(string source, Exception ex)
    {
        if (s_wndprocErrors++ < 10)
            Log.Write($"插件异常已拦截: {source} {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        else if (s_wndprocErrors == 11)
            Log.Write("插件异常: 后续不再逐条打印");
    }

    // ==================== 窗口过程 ====================

    public static void RegisterWindowEventCallback()
    {
        // 用本进程主窗口句柄定位游戏窗口：FindWindow 按类名全局搜索会命中 Steam 客户端
        // 自己的 SDL_app 窗口（新版 Steam 即 SDL 构建），跨进程 SetWindowLong 报 ERROR_ACCESS_DENIED
        var handle = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        if (handle == IntPtr.Zero)
            handle = Win32.FindWindow("Valve001", null);
        s_gameHwnd = handle;
        Log.Write($"RegisterWindowEventCallback: hwnd=0x{handle:X} pid-self");

        var proc = Win32.GetWindowLongA(handle, Win32.GWL_WNDPROC);
        delegate* unmanaged[Cdecl]<IntPtr, uint, IntPtr, IntPtr, IntPtr> p = &MainWndProc;
        var r = Win32.SetWindowLongA(handle, Win32.GWL_WNDPROC, (IntPtr)(p));
        Log.Write($"SetWindowLongA: r=0x{r:X} WndProc=0x{proc:X}");
        s_originalWndProc = proc;
    }

    // 键盘类消息：Avalonia 抓取键盘期间不再交给引擎原 WndProc，
    // 否则引擎/vgui2 输入系统仍会收到字符，出现两侧输入框同时打字
    static bool IsKeyboardMessage(uint m) =>
        m is 0x0100 or 0x0101          // WM_KEYDOWN / WM_KEYUP
            or 0x0102 or 0x0104 or 0x0105   // WM_CHAR / WM_SYSKEYDOWN / WM_SYSKEYUP
            or 0x010D or 0x010E or 0x010F   // WM_IME_START/END/COMPOSITION
            or 0x0286;                      // WM_IME_CHAR（GoldSrc 组字提交走这里）

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static IntPtr MainWndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        // 这是 reverse P/Invoke 边界：托管异常一旦逃逸回游戏主循环，
        // .NET 会直接 __fastfail 掉整个进程（调试器看到的就是"未知 __fastfail() 状态代码"）。
        // 所以插件侧的窗口过程必须自己兜住所有异常，最差也只是把消息原样放行
        try
        {
            if (HandlePluginMessage(message, wParam, lParam))
                return IntPtr.Zero;
        }
        catch (Exception ex)
        {
            ReportError($"MainWndProc msg=0x{message:X}", ex);
        }
        return Win32.CallWindowProcA(s_originalWndProc, hWnd, message, wParam, lParam);
    }

    /// <summary>插件侧消息处理。返回 true 表示消息已被吃掉，不再传给引擎</summary>
    static bool HandlePluginMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        // 关菜单后复位 HUD 绘制门控：从后台线程投递回来，在游戏线程里重放
        // 引擎 ESC 的隐藏分支（CBaseUI::HideGameUI），不能直接发送 ESC——
        // 引擎收到 ESC 会执行 client 的 cancelselect，把选队伍/选兵种菜单关掉
        if (message == WM_ResetHudGate)
        {
            HideGameUIFromEngine();
            Log.Write("EngineHooks: 重放 HideGameUI 复位 HUD 门控");
            return true;
        }
        // 游戏内 ESC 在窗口层吃掉，engine/vgui 都看不到：引擎收到 ESC 会执行
        // client 的 cancelselect，把已打开的 client 菜单（选队伍/选兵种）直接关掉，
        // 返回游戏后选角界面就没了。开菜单走和 ~ 同一条链路（自己 ActivateGameUI）。
        if ((int)wParam == 0x1B
            && GameMode.Current == GameMode.UiMode.InGame && GameMode.EnteredGame)
        {
            if (message == 0x0100)          // WM_KEYDOWN
            {
                s_escapeSwallowed = true;
                if (!EngineConsole.Visible)
                {
                    Log.Write("EngineHooks: 游戏内 ESC -> ActivateGameUI（窗口层拦截）");
                    ActivateGameUIFromEngine();
                }
                return true;
            }
            if (message == 0x0101 && s_escapeSwallowed)   // WM_KEYUP
            {
                s_escapeSwallowed = false;
                return true;
            }
        }
        if (GameMode.SwallowVGuiKeyboard)
        {
            // IME（中文组字/提交）先处理：组字串走 preedit 预览，提交串注入文本
            if (GoldSrcAvalonia.RouteIme(message, lParam))
                return true;
            if (IsKeyboardMessage(message) && GoldSrcAvalonia.RouteKeyboard(message, wParam, lParam))
                return true;
        }
        // 鼠标：先按 vgui2 popup 链顺序仲裁（视觉遮挡与点击归属始终一致）
        if (GoldSrcAvalonia.IsMouseMessage(message) &&
            GoldSrcAvalonia.RouteMouse(message, wParam, lParam))
            return true;
        return false;
    }

    // 游戏内 ESC 的按下是否已被窗口层吞掉（配对吞掉 keyup，不给引擎留半截按键）
    static bool s_escapeSwallowed;

    // 插件自用消息（WM_APP + 1）：请求在游戏线程复位 HUD 绘制门控
    const uint WM_ResetHudGate = 0x8001;

    /// <summary>
    /// 关菜单后由后台线程调用：把复位请求投递到游戏线程处理。
    /// 引擎 ESC 的隐藏分支就是 CBaseUI::HideGameUI，重放它 IGameUI::IsGameUIActive
    /// 才会归零（hw.dll 每帧据此决定是否画 HUD）
    /// </summary>
    internal static void RequestHudGateReset()
    {
        if (s_gameHwnd != IntPtr.Zero)
            Win32.PostMessageA(s_gameHwnd, WM_ResetHudGate, IntPtr.Zero, IntPtr.Zero);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void MouseEvent(int @event)
    {
        // 鼠标事件统一走 WndProc（WM_*BUTTON + WM_MOUSEMOVE，坐标同源），
        // 引擎回调只透传，避免双路径注入坐标不一致导致拖选抖动
        gExportfuncs.IN_MouseEvent(@event);
    }

    // ==================== GameUI 显隐 ====================

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    public static void HideGameUI(IntPtr obj) => HideGameUICore(obj);

    static void HideGameUICore(IntPtr obj)
    {
        s_baseUiObj = obj;
        bool consoleVisible = EngineConsole.Visible;
        g_pfnCBaseUI_HideGameUI(obj);
        // 原版 HideGameUI 会直接调 IGameConsole::Hide，但不清理引擎内部的控制台
        // 可见标志（hw.dll 的 ShowConsole/HideConsole 才改它）。不清理的话，下次
        // ActivateGameUI 末尾会把刚关掉的控制台又弹回来
        if (consoleVisible)
            HideConsoleFromEngine();
        GameMode.SetGameUI(false);
    }

    /// <summary>
    /// 直接走引擎原始 CBaseUI::HideGameUI 关菜单（轻量链路：只切 vgui 状态）。
    /// 供 EngineBaseUI.CloseGameMenu 在按键链路之外触发关闭使用
    /// </summary>
    internal static void HideGameUIFromEngine()
    {
        if (s_baseUiObj != IntPtr.Zero)
            HideGameUICore(s_baseUiObj);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    public static void ActivateGameUI(IntPtr obj) => ActivateGameUICore(obj);

    static void ActivateGameUICore(IntPtr obj)
    {
        s_baseUiObj = obj;
        g_pfnCBaseUI_ActivateGameUI(obj);
        GameMode.SetGameUI(true);
    }

    const int BaseUI_VtHideConsole = 9;
    const int BaseUI_VtShowConsole = 10;

    /// <summary>引擎 ShowConsole：置可见标志 + IGameConsole::Activate（时机与原版 ~ 完全一致）</summary>
    internal static bool ShowConsoleFromEngine()
    {
        if (s_baseUiObj == IntPtr.Zero || s_baseUiVtable == null)
            return false;
        try
        {
            var fn = (delegate* unmanaged[Thiscall]<IntPtr, void>)s_baseUiVtable[BaseUI_VtShowConsole];
            fn(s_baseUiObj);
            return true;
        }
        catch (Exception ex)
        {
            ReportError("ShowConsoleFromEngine", ex);
            return false;
        }
    }

    /// <summary>引擎 HideConsole：清可见标志 + IGameConsole::Hide</summary>
    internal static bool HideConsoleFromEngine()
    {
        if (s_baseUiObj == IntPtr.Zero || s_baseUiVtable == null)
            return false;
        try
        {
            var fn = (delegate* unmanaged[Thiscall]<IntPtr, void>)s_baseUiVtable[BaseUI_VtHideConsole];
            fn(s_baseUiObj);
            return true;
        }
        catch (Exception ex)
        {
            ReportError("HideConsoleFromEngine", ex);
            return false;
        }
    }

    /// <summary>
    /// 走完整的引擎 ActivateGameUI（含输入态/光标切换）。原版 toggleconsole 在
    /// ShowConsole 之前就是这一步；只调 ShowConsole 不会让引擎退出相对鼠标模式，
    /// 表现在游戏内开控制台时没有鼠标指针
    /// </summary>
    internal static bool ActivateGameUIFromEngine()
    {
        if (s_baseUiObj == IntPtr.Zero || s_baseUiVtable == null)
            return false;
        if (GameMode.Current == GameMode.UiMode.GameUI)
            return true;
        try
        {
            ActivateGameUICore(s_baseUiObj);
            return true;
        }
        catch (Exception ex)
        {
            ReportError("ActivateGameUIFromEngine", ex);
            return false;
        }
    }

    // ==================== 按键 ====================

    // —— 按键双通路去重 ——
    // 引擎同时从窗口消息和 SDL 事件两条通路送按键，同一物理按下会两次进入
    // Key_Event。移动类绑定重复执行无感，但 ESC(cancelselect) 这类切换命令
    // 等于连按两次：菜单开又关、HUD 状态错乱。100ms 内同键的重复 down 视为
    // 双通路/自动重复，返回 1（已消费）让引擎跳过本次
    static int s_lastDownKey = -1;
    static int s_lastDownTick;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    public static int Key_Event(IntPtr obj, int down, int keynum, sbyte* pszCurrentBinding)
    {
        s_baseUiObj = obj;
        if (down != 0)
        {
            var now = Environment.TickCount;
            if (keynum == s_lastDownKey && now - s_lastDownTick < 100)
                return 1;
            s_lastDownKey = keynum;
            s_lastDownTick = now;
        }
        // 游戏内 ESC 自己开菜单，不交给引擎：引擎把 ESC 绑给 client 的
        // cancelselect（hw.dll 按键派发），会把已打开的 client 菜单（选队伍/
        // 选兵种）直接关掉，返回游戏就只剩空视野。~ 走的也是"自己 ActivateGameUI"这条路。
        if (down != 0 && keynum == 27
            && GameMode.Current == GameMode.UiMode.InGame && GameMode.EnteredGame)
        {
            if (!EngineConsole.Visible)
                ActivateGameUIFromEngine();
            return 1;
        }
        // 菜单态（GameUI 可见）：输入归 Avalonia。控制台开关按绑定表在我们这侧
        // 处理（玩家改键同样生效），其余按键投递给最上层 Avalonia 窗口；没有
        // Avalonia 窗口可接管（原生接管/加载对话框）时退回引擎。
        // 菜单态 ESC 不交给引擎：它的关闭分支只在"菜单由 ESC 打开"时生效
        if (GameMode.Current == GameMode.UiMode.GameUI)
        {
            if (GameMode.SwallowVGuiKeyboard)
                return 0;   // Avalonia 文本控件/键位捕获已在 WndProc 侧直接接管
            // 菜单浮层（设置/退出确认）打开时 ESC 只关浮层：交给 Avalonia 消费。
            // 也不能把菜单态 ESC 投给 Avalonia：返回游戏注入的 ESC 同样会到
            // 这里，投回去就变成"注入 ESC -> Avalonia 再注入 ESC"的死循环
            if (keynum == 27)
            {
                if (down != 0 && UiWindows.MenuOverlayOpen
                    && GoldSrcAvalonia.RouteMenuKey(keynum, true))
                {
                    s_escapeConsumed = true;
                    return 1;
                }
                if (down == 0 && s_escapeConsumed)
                {
                    s_escapeConsumed = false;
                    return 1;
                }
                // 菜单态关菜单必须由我们做：引擎 ESC 开关的关闭分支只在"菜单由
                // ESC 打开"时生效，地图加载后 GameUI 是引擎自动激活的，那条分支
                // 会当成无动作。不投给 Avalonia——返回游戏注入的 ESC 也走这里，
                // 投回去就是"注入 -> 再注入"的死循环。
                // 只认按下：控制台持焦时 down 被 Avalonia 吃掉，若 up 也关菜单，
                // 关控制台那一下会把底下的菜单一起关掉
                if (down != 0 && GameMode.EnteredGame)
                    EngineBaseUI.CloseGameMenu();
                return 1;
            }
            if (down != 0 && IsToggleConsoleBinding(pszCurrentBinding, keynum))
            {
                EngineConsole.Toggle();
                return 1;
            }
            if (GoldSrcAvalonia.RouteMenuKey(keynum, down != 0))
                return 1;
        }
        // 游戏内 ~：同样由我们处理。引擎自己的 toggleconsole 在 ESC 关闭/回游戏
        // 之后会被 hw.dll 的内部状态吃掉（按键到了但 ShowConsole 不执行），
        // 表现为"回游戏后第一次按 ~ 没反应"；直接调 ShowConsole 绕过该状态机
        if (down != 0 && GameMode.Current == GameMode.UiMode.InGame
            && GameMode.EnteredGame && IsToggleConsoleBinding(pszCurrentBinding, keynum))
        {
            // 原版 toggleconsole 的完整链路是 ActivateGameUI -> ShowConsole：
            // 只有 ShowConsole 不会切引擎输入态，SDL 相对鼠标模式还在，光标不画。
            // 打开控制台前补上 GameUI 激活；ShowCore 会凭"刚激活"标记收起菜单窗口
            if (!EngineConsole.Visible)
                ActivateGameUIFromEngine();
            EngineConsole.Toggle();
            return 1;
        }
        // Avalonia 抓取键盘（文本控件持有焦点）期间吞掉 vgui2 键盘，
        // 避免游戏菜单同时响应按键。
        // 仅在 GameUI 可见（vgui2 已就绪）时转发引擎原始实现——其他时机
        // 原始实现可能访问未初始化的 vgui2 状态，触发 hw.dll 空指针访问
        if (!GameMode.AllowForwardVGuiKeyboard)
        {
            // 启动早期（GameMode 还是 Unknown、vgui2 尚未就绪）转发会踩空指针，
            // 只能丢弃；但 ~ 被直接丢掉就是"刚打开窗口时按 ~ 打不开控制台"。
            // 先记账，等菜单状态就绪后补开一次（见 FlushPendingConsoleToggle）
            if (down != 0 && GameMode.Current == GameMode.UiMode.Unknown
                && !GameMode.SwallowVGuiKeyboard && IsToggleConsoleBinding(pszCurrentBinding, keynum))
                s_pendingConsoleToggles++;
            return 0;
        }
        if (GameMode.SwallowVGuiKeyboard)
            return 0;
        return g_pfnCBaseUI_Key_Event(obj, down, keynum, pszCurrentBinding);
    }

    static int s_pendingConsoleToggles;

    // 菜单浮层的 ESC 已在 Avalonia 侧消费：成对的 keyup 一并吞掉，不留给引擎
    static bool s_escapeConsumed;

    /// <summary>本次按下是否是控制台开关（默认 ~，允许玩家改键）</summary>
    static bool IsToggleConsoleBinding(sbyte* pszCurrentBinding, int keynum)
    {
        if (pszCurrentBinding != null)
        {
            ReadOnlySpan<byte> name = "toggleconsole"u8;
            int i = 0;
            for (; i < name.Length; i++)
            {
                byte c = (byte)pszCurrentBinding[i];
                if (c == 0)
                    return false;
                if (c is >= (byte)'A' and <= (byte)'Z')
                    c += 32;
                if (c != name[i])
                    return false;
            }
            return pszCurrentBinding[i] == 0;
        }
        return keynum == 192;   // 绑定串缺失时退回默认的 ~
    }

    /// <summary>
    /// 启动早期被丢弃的 ~ 在菜单就绪后补开一次。若期间玩家又按过 ~（引擎已把
    /// 控制台打开），以当前可见状态为准，不再补，避免多弹一次
    /// </summary>
    static void FlushPendingConsoleToggle()
    {
        if (s_pendingConsoleToggles == 0)
            return;
        if (!GameMode.AllowForwardVGuiKeyboard || GameMode.SwallowVGuiKeyboard)
            return;
        int count = s_pendingConsoleToggles;
        s_pendingConsoleToggles = 0;
        if ((count & 1) == 0 || EngineConsole.Visible)
            return;
        Log.Write("EngineHooks: 补开启动期被丢弃的 ~（菜单已就绪）");
        EngineConsole.Show();
    }

    // ==================== 帧绘制 ====================

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    public static void Paint(IntPtr obj, int x, int y, int right, int bottom)
    {
        // 记录 GL 基线 + 同步渲染尺寸 + 按需解锁光标由接入层负责
        GoldSrcAvalonia.BeginFrame();
        // 同样是 reverse P/Invoke 边界：Avalonia 侧的绘制/输入异常不能冒回引擎
        try
        {
            // 两层模型：原版 vgui（含 popup 层）先画完，Avalonia 窗口整体画在最上层。
            // 输入侧 RouteMouse 与此一致：先命中 Avalonia 窗口，未覆盖区域交给引擎/vgui2，
            // 保证视觉遮挡与点击命中永远一致（不要按焦点翻转层序，会导致两者脱节）
            g_pfnCBaseUI_Paint(obj, x, y, right, bottom);

            // 该引擎启动时直接进入主菜单且不走 IBaseUI::ActivateGameUI（日志可证），
            // 首次 Paint（= 菜单可见）时补齐 GameUI 状态，驱动多窗口初始化。
            if (!s_menuStateInitialized)
            {
                s_menuStateInitialized = true;
                Log.Write("EngineHooks::Paint: first menu paint -> sync GameUI state");
                GameMode.SetGameUI(true);
            }
            EngineBaseUI.InstallLoadingHooks();
            EngineConsole.InstallHooks();   // GameUI.dll 晚就绪时补挂控制台钩子
            // 菜单里没有 Avalonia 文本控件接收输入时摘掉引擎窗口的 IME 关联：
            // 否则中文输入法把按键（含 ~）吃成 VK_PROCESSKEY，控制台永远打不开。
            // 文本控件持焦会由 GL2InputMethod.EnsureAttached 重新挂回
            if (GameMode.Current == GameMode.UiMode.GameUI && !GoldSrcAvalonia.AnyTextInputActive())
                GoldSrcAvalonia.DetachIme();
            // 逐帧业务对账（原版面板替换、对局结束判定等）
            FramePump?.Invoke();
            // 启动早期按下、当时无法转发的 ~：菜单状态此刻已就绪，补开控制台
            FlushPendingConsoleToggle();
        }
        catch (Exception ex)
        {
            ReportError("EngineHooks::Paint", ex);
        }
        finally
        {
            GoldSrcAvalonia.EndFrame();
        }
    }
}
