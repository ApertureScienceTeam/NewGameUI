using GoldSrc.Avalonia;
using System.Runtime.InteropServices;
using System.Text;

namespace Plugin.Engine;

/// <summary>
/// 引擎本地化：vgui2 的 <c>ILocalize</c>。
///
/// 引擎/GameUI 传给进度条的文本有两种形态：
///   - "#GameUI_LoadingFilename" 这类 <b>键</b>（vgui2 的 Label 会自动把 '#' 开头的文本查表）；
///   - 已经本地化好的纯文本。
/// 我们自绘的 Avalonia 界面没有 vgui2 的自动查表，所以这里补上：
/// 从 vgui2.dll 的 CreateInterface 取 "VGUI_Localize003"（实测该字符串就在 vgui2.dll 里），
/// 用 <c>ILocalize::Find</c>（虚表槽 3，与 Metahook 的 ILocalize 定义一致）取 UTF-16 文本，
/// 再按 vgui2 习惯替换 %s1/%s2 占位符。查不到就原样返回
/// </summary>
internal static unsafe class EngineLocalize
{
    static nint s_localize;
    static bool s_resolved;

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    static extern IntPtr GetModuleHandleA(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    static nint Resolve()
    {
        if (s_resolved)
            return s_localize;
        s_resolved = true;

        var mod = GetModuleHandleA("vgui2.dll");
        if (mod == IntPtr.Zero)
        {
            GoldSrcAvalonia.Log?.Invoke("EngineLocalize: vgui2.dll 未加载，跳过本地化");
            return 0;
        }
        var createInterface = GetProcAddress(mod, "CreateInterface");
        if (createInterface == IntPtr.Zero)
        {
            GoldSrcAvalonia.Log?.Invoke("EngineLocalize: vgui2.dll 没有 CreateInterface");
            return 0;
        }

        var fn = (delegate* unmanaged[Cdecl]<byte*, int*, void*>)createInterface;
        var retCode = 0;
        foreach (var name in (ReadOnlySpan<string>)["VGUI_Localize003", "VGUI_Localize002"])
        {
            var bytes = Encoding.ASCII.GetBytes(name + "\0");
            fixed (byte* p = bytes)
                s_localize = (nint)fn(p, &retCode);
            if (s_localize != 0)
            {
                GoldSrcAvalonia.Log?.Invoke($"EngineLocalize: {name} -> 0x{s_localize:X}");
                return s_localize;
            }
        }
        GoldSrcAvalonia.Log?.Invoke("EngineLocalize: 未取到 ILocalize，文本按原样显示");
        return 0;
    }

    /// <summary>把引擎给的原始文本翻成本地化文本（token 或已翻译文本都接受）</summary>
    public static string Translate(string? raw, string arg1 = "")
    {
        if (string.IsNullOrEmpty(raw))
            return "";

        var isToken = raw[0] == '#';
        var token = isToken ? raw[1..] : raw;

        var text = Find(token);
        if (text == null)
        {
            // 不是 token（引擎给的已是本地化文本）就原样用；是 token 但查不到就把 '#' 去掉
            text = isToken ? token : raw;
        }
        return Substitute(text, arg1);
    }

    /// <summary>按 key 查表，查不到返回 <paramref name="fallback"/></summary>
    public static string FindOrDefault(string token, string fallback) =>
        Find(token) ?? fallback;

    /// <summary>按 key 查表并填充 %s1（查不到用 fallback）</summary>
    public static string FindFormat(string token, string arg1, string fallback)
    {
        var text = Find(token);
        return text == null ? fallback : Substitute(text, arg1);
    }

    static string? Find(string token)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        var localize = Resolve();
        if (localize == 0)
            return null;

        try
        {
            var vtable = *(void***)localize;
            // ILocalize: [0] QueryInterface? [1] AddFile [2] RemoveAll [3] Find
            var find = (delegate* unmanaged[Thiscall]<void*, sbyte*, ushort*>)vtable[3];
            var bytes = Encoding.UTF8.GetBytes(token + "\0");
            fixed (byte* name = bytes)
            {
                var wide = find((void*)localize, (sbyte*)name);
                if (wide == null)
                    return null;
                return new string((char*)wide);
            }
        }
        catch (Exception ex)
        {
            GoldSrcAvalonia.Log?.Invoke($"EngineLocalize: 查表 '{token}' 失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>vgui2 文本里的 %s1/%s2… 占位符：这里只填第一个（地图名），其余清空</summary>
    static string Substitute(string text, string arg1)
    {
        if (text.IndexOf('%') < 0)
            return text;

        var result = text.Replace("%s1", arg1);
        for (var i = 2; i <= 9; i++)
            result = result.Replace($"%s{i}", "");
        return result;
    }
}
