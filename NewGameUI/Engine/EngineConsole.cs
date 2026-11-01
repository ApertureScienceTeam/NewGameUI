using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using GoldSrc.Avalonia;

namespace Plugin.Engine;

/// <summary>
/// 原版 GameUI 控制台（IGameConsole / GameConsole003）的接入钩子与显隐编排。
///
/// 显隐时机完全由引擎驱动（与原版一致）：
///   ShowConsole   -> IGameConsole::Activate   （槽 1）-> 显示我们的窗口
///   HideConsole   -> IGameConsole::Hide       （槽 3）-> 隐藏
///   HideGameUI    -> 引擎内部直接调 Hide       （槽 3）
///   ActivateGameUI 末尾若控制台可见标志仍为 1，会再调 Activate（槽 1）把控制台压在菜单上方
///
/// 原生 CConsoleDialog 永远不会被显示（Activate 被吞掉）；Printf/DPrintf 按
/// consoledialog.cpp 的格式化复刻后经 <see cref="LineAdded"/> 原样发布，
/// 输出缓冲/命令历史/提交/condump 属于业务，由 ConsoleService 承接。
/// </summary>
public static unsafe class EngineConsole
{
    static IntPtr s_console;
    static bool s_hooksInstalled;
    static bool s_warned;

    // VFTHook 需要接收原始实现指针；控制台已完全由 Avalonia 接管，不再回调
    static delegate* unmanaged[Thiscall]<IntPtr, void> s_pfnActivate;
    static delegate* unmanaged[Thiscall]<IntPtr, void> s_pfnHide;
    static delegate* unmanaged[Thiscall]<IntPtr, void> s_pfnClear;
    static delegate* unmanaged[Thiscall]<IntPtr, int> s_pfnIsVisible;
    static delegate* unmanaged[Cdecl]<IntPtr, sbyte*, sbyte*, void> s_pfnPrintf;
    static delegate* unmanaged[Cdecl]<IntPtr, sbyte*, sbyte*, void> s_pfnDPrintf;

    /// <summary>系统 ANSI 代码页（中文系统 = GBK/936）：GoldSrc 引擎自身的控制台字符串是 ANSI</summary>
    internal static readonly Encoding Ansi = CreateAnsiEncoding();

    static bool s_loggedUtf8;

    /// <summary>
    /// 控制台文本解码。引擎自身的输出（含中文本地化）是系统 ANSI，但 AMXX 这类插件
    /// 把 lang 文件里的字节原样打印出来——cstrike\addons\amxmodx\data\lang\*.txt
    /// 实测是 UTF-8（同一段字节按 GBK 解直接是非法多字节序列），按 ANSI 解就成了乱码。
    /// 判据取"整段含高位字节且是合法 UTF-8"：中文 GBK 串几乎必然含非法 UTF-8 序列，
    /// 落回 ANSI；纯 ASCII 走哪条都一样。
    /// </summary>
    static string DecodeText(sbyte* p)
    {
        if (p == null)
            return "";
        int len = 0;
        while (p[len] != 0)
            len++;
        if (len == 0)
            return "";

        var bytes = new ReadOnlySpan<byte>((byte*)p, len);
        if (IsUtf8(bytes))
        {
            if (!s_loggedUtf8)
            {
                s_loggedUtf8 = true;
                Log.Write("EngineConsole: 输出是 UTF-8（AMXX 等插件），已按 UTF-8 解码");
            }
            return Encoding.UTF8.GetString(bytes);
        }
        return Ansi.GetString(bytes);
    }

    /// <summary>含高位字节、且整段是合法 UTF-8（拒绝 overlong、代理区与超范围码位）</summary>
    static bool IsUtf8(ReadOnlySpan<byte> b)
    {
        var asciiOnly = true;
        for (int i = 0; i < b.Length; i++)
        {
            byte c = b[i];
            if (c < 0x80)
                continue;
            asciiOnly = false;

            int extra = c switch
            {
                >= 0xC2 and <= 0xDF => 1,
                >= 0xE0 and <= 0xEF => 2,
                >= 0xF0 and <= 0xF4 => 3,
                _ => -1,
            };
            if (extra < 0 || i + extra >= b.Length)
                return false;
            for (int k = 1; k <= extra; k++)
                if ((b[i + k] & 0xC0) != 0x80)
                    return false;

            if (c == 0xE0 && (b[i + 1] < 0xA0 || b[i + 1] > 0xBF))
                return false;
            if (c == 0xED && (b[i + 1] < 0x80 || b[i + 1] > 0x9F))
                return false;
            if (c == 0xF0 && (b[i + 1] < 0x90 || b[i + 1] > 0xBF))
                return false;
            if (c == 0xF4 && b[i + 1] > 0x8F)
                return false;

            i += extra;
        }
        return !asciiOnly;
    }

    static Encoding CreateAnsiEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
        }
        try
        {
            return Encoding.GetEncoding(0);
        }
        catch
        {
            return Encoding.ASCII;
        }
    }

    /// <summary>控制台当前是否可见（我们的窗口状态，不是引擎内部标志）</summary>
    public static bool Visible { get; private set; }

    /// <summary>GameUI.dll 的 IGameConsole 是否已拿到</summary>
    public static bool Available => s_console != IntPtr.Zero;

    /// <summary>引擎控制台输出一行（Printf/DPrintf 已按原版格式化；引擎线程回调）</summary>
    public static event Action<ConsoleLine>? LineAdded;

    /// <summary>引擎清空了控制台缓冲（clear 命令 / CConsoleDialog::Clear）</summary>
    public static event Action? Cleared;

    /// <summary>控制台显隐变化（引擎线程回调）</summary>
    public static event Action<bool>? VisibilityChanged;

    // ==================== 钩子安装 ====================

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    static extern IntPtr GetModuleHandleA(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    static void Resolve()
    {
        if (s_console != IntPtr.Zero)
            return;
        var mod = GetModuleHandleA("GameUI.dll");
        if (mod == IntPtr.Zero)
        {
            if (!s_warned)
            {
                s_warned = true;
                Log.Write("EngineConsole: GameUI.dll 未加载，等待重试");
            }
            return;
        }
        var createInterface = GetProcAddress(mod, "CreateInterface");
        if (createInterface == IntPtr.Zero)
            return;
        var fn = (delegate* unmanaged[Cdecl]<byte*, int*, void*>)createInterface;
        int ret = 0;
        fixed (byte* name = "GameConsole003\0"u8)
            s_console = (IntPtr)fn(name, &ret);
        if (s_console != IntPtr.Zero)
            Log.Write($"EngineConsole: GameConsole003 -> 0x{(ulong)s_console:X}");
    }

    /// <summary>挂 IGameConsole 钩子（幂等；GameUI.dll 就绪后任意时机可调）</summary>
    public static void InstallHooks()
    {
        if (s_hooksInstalled)
            return;
        try
        {
            Resolve();
            if (s_console == IntPtr.Zero)
                return;
            var api = g_pMetaHookAPI;
            if (api == null)
                return;

            delegate* unmanaged[Thiscall]<IntPtr, void> fActivate = &OnActivate;
            fixed (void* p = &s_pfnActivate)
                api->VFTHook((void*)s_console, 0, 1, (void*)fActivate, (void**)p);

            delegate* unmanaged[Thiscall]<IntPtr, void> fHide = &OnHide;
            fixed (void* p = &s_pfnHide)
                api->VFTHook((void*)s_console, 0, 3, (void*)fHide, (void**)p);

            delegate* unmanaged[Thiscall]<IntPtr, void> fClear = &OnClear;
            fixed (void* p = &s_pfnClear)
                api->VFTHook((void*)s_console, 0, 4, (void*)fClear, (void**)p);

            delegate* unmanaged[Thiscall]<IntPtr, int> fVisible = &OnIsConsoleVisible;
            fixed (void* p = &s_pfnIsVisible)
                api->VFTHook((void*)s_console, 0, 5, (void*)fVisible, (void**)p);

            delegate* unmanaged[Cdecl]<IntPtr, sbyte*, sbyte*, void> fPrintf = &OnPrintf;
            fixed (void* p = &s_pfnPrintf)
                api->VFTHook((void*)s_console, 0, 6, (void*)fPrintf, (void**)p);

            delegate* unmanaged[Cdecl]<IntPtr, sbyte*, sbyte*, void> fDPrintf = &OnDPrintf;
            fixed (void* p = &s_pfnDPrintf)
                api->VFTHook((void*)s_console, 0, 7, (void*)fDPrintf, (void**)p);

            s_hooksInstalled = true;
            Log.Write("EngineConsole: 钩子已安装（Activate/Hide/Clear/IsConsoleVisible/Printf/DPrintf）");
        }
        catch (Exception ex)
        {
            Log.Write($"EngineConsole.InstallHooks failed: {ex}");
        }
    }

    // ==================== 引擎回调 ====================

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    static void OnActivate(IntPtr self) => ShowCore();

    // 引擎主动 Hide（ESC/命令）时不要再回调引擎的 HideConsole，否则重复进 HideCore
    static bool s_inEngineHide;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    static void OnHide(IntPtr self)
    {
        s_inEngineHide = true;
        try { HideCore(); }
        finally { s_inEngineHide = false; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    static void OnClear(IntPtr self) => Cleared?.Invoke();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    static int OnIsConsoleVisible(IntPtr self) => Visible ? 1 : 0;

    // 反汇编实证：Printf/DPrintf 是 cdecl，this 作为第一个栈参数，
    // 后跟 format 与单个可变参数；调用方清栈（add esp）。多参数格式忽略额外参数。
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnPrintf(IntPtr self, sbyte* format, sbyte* arg0)
    {
        try
        {
            if (format != null)
                PublishLines(FormatText(format, arg0), false);
        }
        catch (Exception ex)
        {
            LogOnce("Printf", ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnDPrintf(IntPtr self, sbyte* format, sbyte* arg0)
    {
        try
        {
            if (format != null)
                PublishLines(FormatText(format, arg0), true);
        }
        catch (Exception ex)
        {
            LogOnce("DPrintf", ex);
        }
    }

    static readonly HashSet<string> s_reported = new();

    static void LogOnce(string where, Exception ex)
    {
        lock (s_reported)
        {
            if (s_reported.Add(where))
                Log.Write($"EngineConsole.{where} 异常（已拦截）: {ex.Message}");
        }
    }

    // ==================== 显示 / 隐藏 ====================

    /// <summary>打开控制台（有引擎时走 ShowConsole，保证引擎内部可见标志同步）</summary>
    public static void Show()
    {
        if (EngineHooks.ShowConsoleFromEngine())
            return;
        ShowCore();
    }

    /// <summary>关闭控制台（有引擎时走 HideConsole，保证 toggleconsole 状态同步）</summary>
    public static void Hide()
    {
        if (EngineHooks.HideConsoleFromEngine())
            return;
        HideCore();
    }

    /// <summary>开关控制台（等价 ~ 键）</summary>
    public static void Toggle()
    {
        if (Visible)
            Hide();
        else
            Show();
    }

    static void ShowCore()
    {
        if (Visible)
        {
            // ActivateGameUI 末尾会用 Activate 再次抬起控制台，保持它在菜单之上
            UiWindows.SetConsoleVisible(true);
            return;
        }
        Visible = true;
        Log.Write("EngineConsole: 显示");
        VisibilityChanged?.Invoke(true);
        UiWindows.SetConsoleVisible(true);
    }

    static void HideCore()
    {
        if (!Visible)
            return;
        Visible = false;
        Log.Write("EngineConsole: 隐藏");
        VisibilityChanged?.Invoke(false);
        UiWindows.SetConsoleVisible(false);
        // 主动关闭（✕/命令）时引擎 CBaseUI 内部还记着"控制台可见"，
        // 不清掉的话下一次 ~ 会被引擎当成重复调用忽略，控制台再也打不开。
        // 走一遍引擎自己的 HideConsole 复位内部状态（引擎回调进来的不重复走）
        if (!s_inEngineHide && EngineHooks.HideConsoleFromEngine())
            Log.Write("EngineConsole: 已同步引擎控制台状态");
    }

    /// <summary>预览进程（无引擎）直接开关</summary>
    internal static void ShowDirect() => ShowCore();

    internal static void HideDirect() => HideCore();

    /// <summary>菜单关闭时兜底同步状态（不再回调 vgui）</summary>
    internal static void NotifyHiddenByMenu()
    {
        if (!Visible)
            return;
        Visible = false;
        VisibilityChanged?.Invoke(false);
    }

    /// <summary>加载覆盖窗结束时恢复仍处于"打开"状态的控制台</summary>
    internal static void RestoreAfterOverlay()
    {
        if (Visible)
            UiWindows.SetConsoleVisible(true);
    }

    // ==================== 输出发布 ====================

    static void PublishLines(string text, bool dev)
    {
        if (string.IsNullOrEmpty(text))
            return;
        if (text.IndexOf('\r') >= 0)
            text = text.Replace("\r", "");

        var parts = text.Split('\n');
        // 以换行结尾时不额外产生空行（引擎绝大多数 Printf("%s", "...\n")）
        int count = parts.Length;
        if (count > 0 && parts[count - 1].Length == 0)
            count--;

        for (int i = 0; i < count; i++)
            LineAdded?.Invoke(new ConsoleLine(parts[i], dev));
    }

    // ==================== 格式化（UTF-8 / ANSI + 单参数） ====================

    static string FormatText(sbyte* format, sbyte* arg0)
    {
        var fmt = DecodeText(format);
        return TrySubstitute(fmt, arg0, out var result) ? result : fmt;
    }

    static bool TrySubstitute(string fmt, sbyte* arg0, out string result)
    {
        result = fmt;
        int specStart = -1;
        int specEnd = -1;
        char conv = '\0';
        int width = 0;
        int precision = -1;
        bool zeroPad = false;
        bool leftAlign = false;
        int specCount = 0;

        for (int i = 0; i < fmt.Length; i++)
        {
            if (fmt[i] != '%')
                continue;
            if (i + 1 < fmt.Length && fmt[i + 1] == '%')
            {
                i++;
                continue;
            }
            // 多参数格式读不到后续栈参数，原样输出好过错位渲染
            if (++specCount > 1)
                return false;

            specStart = i;
            int j = i + 1;
            while (j < fmt.Length && fmt[j] is '-' or '+' or ' ' or '#' or '0')
            {
                if (fmt[j] == '-')
                    leftAlign = true;
                if (fmt[j] == '0')
                    zeroPad = true;
                j++;
            }
            int w = 0;
            while (j < fmt.Length && char.IsDigit(fmt[j]))
            {
                w = w * 10 + (fmt[j] - '0');
                j++;
            }
            width = w;
            if (j < fmt.Length && fmt[j] == '.')
            {
                j++;
                int p = 0;
                while (j < fmt.Length && char.IsDigit(fmt[j]))
                {
                    p = p * 10 + (fmt[j] - '0');
                    j++;
                }
                precision = p;
            }
            while (j < fmt.Length && fmt[j] is 'h' or 'l' or 'z' or 'L')
                j++;
            if (j >= fmt.Length)
                return false;
            conv = fmt[j];
            specEnd = j;
            i = j;
        }

        if (specCount == 0)
            return true;

        string value;
        switch (conv)
        {
            case 's':
                value = arg0 == null ? "(null)" : DecodeText(arg0);
                break;
            case 'd':
            case 'i':
                value = ((int)(nint)arg0).ToString(CultureInfo.InvariantCulture);
                break;
            case 'u':
                value = ((uint)(nint)arg0).ToString(CultureInfo.InvariantCulture);
                break;
            case 'x':
                value = ((uint)(nint)arg0).ToString("x", CultureInfo.InvariantCulture);
                break;
            case 'X':
                value = ((uint)(nint)arg0).ToString("X", CultureInfo.InvariantCulture);
                break;
            case 'f':
            case 'F':
            {
                var f = BitConverter.Int32BitsToSingle((int)(nint)arg0);
                value = f.ToString("F" + (precision >= 0 ? precision : 6), CultureInfo.InvariantCulture);
                break;
            }
            case 'g':
            case 'G':
            {
                var f = BitConverter.Int32BitsToSingle((int)(nint)arg0);
                value = f.ToString("G" + (precision > 0 ? precision : 6), CultureInfo.InvariantCulture);
                break;
            }
            case 'c':
                value = ((char)((int)(nint)arg0 & 0xFF)).ToString();
                break;
            case 'p':
                value = "0x" + ((uint)(nint)arg0).ToString("X8", CultureInfo.InvariantCulture);
                break;
            default:
                return false;
        }

        if (width > value.Length)
        {
            if (leftAlign)
            {
                value = value.PadRight(width);
            }
            else if (zeroPad && conv is 'd' or 'i' or 'u' or 'x' or 'X' or 'f' or 'F' or 'g' or 'G')
            {
                if (value.StartsWith("-", StringComparison.Ordinal))
                    value = "-" + value[1..].PadLeft(width - 1, '0');
                else
                    value = value.PadLeft(width, '0');
            }
            else
            {
                value = value.PadLeft(width);
            }
        }

        result = fmt[..specStart] + value + fmt[(specEnd + 1)..];
        return true;
    }

    /// <summary>插件卸载时解绑状态（面板对象由引擎持有，进程即将退出，不再还原）</summary>
    public static void Reset()
    {
        s_hooksInstalled = false;
        Visible = false;
        s_reported.Clear();
    }
}

/// <summary>
/// 控制台输出的一行。颜色由 <paramref name="IsDevMessage"/> 决定：
/// 普通 Printf = #D8DED3，DPrintf = #C4B550（与原版 consoledialog.cpp 一致）
/// </summary>
public readonly record struct ConsoleLine(string Text, bool IsDevMessage);
