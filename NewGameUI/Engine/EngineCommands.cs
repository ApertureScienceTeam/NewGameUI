using GoldSrc.Avalonia;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Plugin.Engine;

/// <summary>
/// 引擎命令/控制变量门面（业务侧）：给 Blazonia 页面提供安全的引擎访问入口。
///
/// 全部走 cl_enginefuncs_t（pfnClientCmd / pfnGetCvar* / pfnGetGameDirectory），
/// 预览进程（无引擎，gEngfuncs 未初始化）下为安全空操作/默认值，
/// 页面代码无需区分运行环境。
///
/// 编码注意：GoldSrc 控制台/cvar 字符串是 ANSI（中文系统即 GBK），
/// 玩家名字、主机名等常含中文，统一用系统 ANSI 代码页编解码，
/// 不用 Marshal.PtrToStringAnsi（.NET Core 下按 UTF-8 解会乱码）
/// </summary>
public static unsafe class EngineCommands
{
    static EngineCommands()
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
            Ansi = Encoding.GetEncoding(0);
        }
        catch
        {
            Ansi = Encoding.ASCII;
        }
    }

    /// <summary>系统 ANSI 代码页（中文系统 = GBK/936）</summary>
    public static Encoding Ansi { get; }

    static bool Available => Global.gEngfuncs.pfnClientCmd != null;

    /// <summary>引擎函数表是否已就绪（预览进程为 false）</summary>
    public static bool EngineAvailable => Available;

    static List<string>? s_commandNames;
    static List<string>? s_cvarNames;

    /// <summary>引擎已注册的命令名（控制台 TAB 补全用；ANSI 解码）</summary>
    public static IReadOnlyList<string> GetCommandNames()
    {
        if (s_commandNames != null)
            return s_commandNames;

        var names = new List<string>();
        if (Available && Global.gEngfuncs.GetFirstCmdFunctionHandle != null)
        {
            try
            {
                uint handle = Global.gEngfuncs.GetFirstCmdFunctionHandle();
                // 防御：句柄链异常时不要死循环
                for (int guard = 0; handle != 0 && guard < 20000; guard++)
                {
                    var name = Global.gEngfuncs.GetCmdFunctionName(handle);
                    if (name != null)
                    {
                        var s = ReadAnsi(name);
                        if (s.Length > 0)
                            names.Add(s);
                    }
                    handle = Global.gEngfuncs.GetNextCmdFunctionHandle(handle);
                }
            }
            catch (Exception ex)
            {
                GoldSrcAvalonia.Log?.Invoke($"EngineCommands.GetCommandNames failed: {ex.Message}");
            }
        }
        s_commandNames = names;
        return names;
    }

    /// <summary>引擎已注册的 cvar 名（控制台 TAB 补全用；ANSI 解码）</summary>
    public static IReadOnlyList<string> GetCvarNames()
    {
        if (s_cvarNames != null)
            return s_cvarNames;

        var names = new List<string>();
        if (Available && Global.gEngfuncs.GetFirstCvarPtr != null)
        {
            try
            {
                var cvar = Global.gEngfuncs.GetFirstCvarPtr();
                for (int guard = 0; cvar != null && guard < 20000; guard++)
                {
                    if (cvar->name != null)
                    {
                        var s = ReadAnsi(cvar->name);
                        if (s.Length > 0)
                            names.Add(s);
                    }
                    cvar = cvar->next;
                }
            }
            catch (Exception ex)
            {
                GoldSrcAvalonia.Log?.Invoke($"EngineCommands.GetCvarNames failed: {ex.Message}");
            }
        }
        s_cvarNames = names;
        return names;
    }

    static unsafe string ReadAnsi(sbyte* p)
    {
        int len = 0;
        while (p[len] != 0)
            len++;
        return len == 0 ? "" : Ansi.GetString((byte*)p, len);
    }

    /// <summary>执行控制台命令（等价于在控制台输入；多条命令可用分号连接）</summary>
    public static void ClientCmd(string command)
    {
        if (!Available || string.IsNullOrEmpty(command))
            return;
        var bytes = Ansi.GetBytes(command);
        fixed (byte* p = bytes)
        {
            Global.gEngfuncs.pfnClientCmd((sbyte*)p);
        }
    }

    /// <summary>读字符串 cvar：引擎当前值 → 磁盘保存值（config.cfg，SavedGameConfig）→ fallback。
    /// 原版设置页（GameUI CCvar* 控件）同样以 cvar 系统为当前值来源；
    /// 引擎缺失（Preview）或 cvar 未注册时落到磁盘保存值，保证设置页显示保存的设置</summary>
    public static string GetCvarString(string name, string fallback = "")
    {
        if (Available)
        {
            var bytes = Ansi.GetBytes(name);
            fixed (byte* p = bytes)
            {
                var r = Global.gEngfuncs.pfnGetCvarString((sbyte*)p);
                if (r != null)
                {
                    // ANSI 字符串手动解码（中文系统 = GBK）
                    int len = 0;
                    while (r[len] != 0)
                        len++;
                    var v = Ansi.GetString((byte*)r, len);
                    if (v.Length > 0)
                        return v;
                }
            }
        }
        return SavedGameConfig.GetCvar(name) ?? fallback;
    }

    /// <summary>读数值 cvar（同 GetCvarString 的三级链，值按引擎格式解析）</summary>
    public static float GetCvarFloat(string name, float fallback = 0f)
    {
        var s = GetCvarString(name, "");
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    /// <summary>写字符串 cvar：走控制台命令 name "value"（Cvar_Set 在 HLSDK.Net 里签名不完整）</summary>
    public static void SetCvar(string name, string value) =>
        ClientCmd($"{name} \"{value}\"");

    /// <summary>写数值 cvar：走控制台命令 name value</summary>
    public static void SetCvarFloat(string name, float value) =>
        ClientCmd($"{name} {value.ToString("0.####", CultureInfo.InvariantCulture)}");

    /// <summary>当前 mod 目录（如 ...\Half-Life\cstrike）：引擎值 → 磁盘探测（SavedGameConfig），
    /// 都拿不到时返回 null</summary>
    public static string? GetGameDirectory() =>
        GetEngineGameDirectory() ?? SavedGameConfig.GameDirectory;

    /// <summary>仅引擎提供的 mod 目录，无引擎返回 null（SavedGameConfig 探测时避免循环依赖用）</summary>
    public static string? GetEngineGameDirectory()
    {
        if (!Available)
            return null;
        var r = Global.gEngfuncs.pfnGetGameDirectory();
        if (r == null)
            return null;
        int len = 0;
        while (r[len] != 0)
            len++;
        return Ansi.GetString((byte*)r, len);
    }

    /// <summary>当前关卡名（如 maps/de_dust2.bsp）；未进关卡或无引擎返回 null</summary>
    public static string? GetLevelName()
    {
        if (!Available)
            return null;
        var r = Global.gEngfuncs.pfnGetLevelName();
        if (r == null)
            return null;
        int len = 0;
        while (r[len] != 0)
            len++;
        return Ansi.GetString((byte*)r, len);
    }

    /// <summary>把当前设置（cvar + 键位）立即写回 config.cfg（host_writeconfig）。
    /// 原版引擎只在退出时写盘；设置页应用后调用可让保存值即时落盘、
    /// 磁盘读取（SavedGameConfig）与当前值保持一致。无引擎时为空操作</summary>
    public static void SaveSettings()
    {
        if (!Available)
            return;
        ClientCmd("host_writeconfig");
        SavedGameConfig.Invalidate();
    }
}
