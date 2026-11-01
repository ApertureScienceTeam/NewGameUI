using GoldSrc.Avalonia;
using System.Runtime.InteropServices;

namespace Plugin.Engine.Steam;

/// <summary>
/// ISteamFriends017 的最小封装：只取好友名字。
///
/// 偏移取自 SDK 公开顺序（ISteamFriends 最早的 8 个方法一直没变过）：
/// 0 GetPersonaName / 1 SetPersonaName / 2 GetPersonaState / 3 GetFriendCount /
/// 4 GetFriendByIndex / 5 GetFriendRelationship / 6 GetFriendPersonaState /
/// 7 GetFriendPersonaName
///
/// GetFriendByIndex 返回 CSteamID。CSteamID 带用户定义构造函数（非 POD），
/// MSVC 因此走“隐藏返回指针”的 sret 约定：
/// <code>void __thiscall GetFriendByIndex(CSteamID* ret, int iFriend, int iFriendFlags)</code>
/// 直接把返回值当 EDX:EAX 收会踩空（实测 steamclient.dll 写入 0x00000000 崩溃），
/// 必须按 sret 调用：this 进 ECX、ret 作为第一个栈参数。
/// 仍然先用 <see cref="MyName"/> 做一次自检，拿不到自己的名字就整体禁用，
/// 避免在错误的虚表上乱跳。
/// </summary>
public static unsafe class SteamFriends
{
    /// <summary>k_EFriendFlagImmediate</summary>
    public const int FriendFlagImmediate = 0x04;

    const int IGetPersonaName = 0;
    const int IGetFriendCount = 3;
    const int IGetFriendByIndex = 4;
    const int IGetFriendPersonaName = 7;

    static readonly object s_lock = new();
    static nint s_iface;
    static bool s_done;
    static string? s_myName;
    static bool s_verified;

    [DllImport("steam_api", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamAPI_GetHSteamUser")]
    static extern int SteamAPI_GetHSteamUser();

    [DllImport("steam_api", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamInternal_FindOrCreateUserInterface")]
    static extern nint SteamInternal_FindOrCreateUserInterface(int hSteamUser, byte* version);

    static nint Iface
    {
        get
        {
            if (s_done)
                return s_iface;
            lock (s_lock)
            {
                if (s_done)
                    return s_iface;
                s_done = true;
                try
                {
                    fixed (byte* version = "SteamFriends017\0"u8)
                    {
                        var user = SteamAPI_GetHSteamUser();
                        s_iface = SteamInternal_FindOrCreateUserInterface(user, version);
                    }
                    GoldSrcAvalonia.Log?.Invoke($"SteamFriends: iface=0x{s_iface:X}");
                }
                catch (Exception ex)
                {
                    GoldSrcAvalonia.Log?.Invoke($"SteamFriends: unavailable ({ex.GetType().Name})");
                    s_iface = 0;
                }
            }
            return s_iface;
        }
    }

    static nint Method(int index) => (*(nint**)Iface)[index];

    /// <summary>自己的昵称；为空表示接口不可用（同时作为虚表自检）</summary>
    public static string? MyName
    {
        get
        {
            if (s_myName != null)
                return s_myName;
            if (Iface == 0)
                return null;
            try
            {
                var fn = (delegate* unmanaged[Thiscall]<nint, byte*>)(nuint)Method(IGetPersonaName);
                var p = (byte*)fn(Iface);
                if (p == null)
                    return null;
                var len = 0;
                while (len < 128 && p[len] != 0)
                    len++;
                if (len is 0 or >= 128)
                    return null;
                s_myName = System.Text.Encoding.UTF8.GetString(p, len);
            }
            catch
            {
                s_myName = null;
            }
            return s_myName;
        }
    }

    public static bool Available
    {
        get
        {
            if (Iface == 0)
                return false;
            if (s_verified)
                return true;
            s_verified = !string.IsNullOrEmpty(MyName);
            if (!s_verified)
                GoldSrcAvalonia.Log?.Invoke("SteamFriends: persona self-check failed, disabled");
            return s_verified;
        }
    }

    /// <summary>在线好友昵称列表（最多 <paramref name="max"/> 个）</summary>
    public static List<string> GetFriendNames(int max = 32)
    {
        var list = new List<string>();
        if (!Available)
            return list;

        int count;
        try
        {
            count = ((delegate* unmanaged[Thiscall]<nint, int, int>)(nuint)Method(IGetFriendCount))(Iface, FriendFlagImmediate);
        }
        catch
        {
            return list;
        }
        if (count is <= 0 or > 2000)
            return list;

        // sret 调用：nint 首个参数进 ECX（= this），其余按右到左压栈 → 栈上是 ret/i/flags
        var byIndex = (delegate* unmanaged[Thiscall]<nint, ulong*, int, int, void>)(nuint)Method(IGetFriendByIndex);
        // CSteamID 按值传参是 8 字节内存，等价于 ulong 占两个栈槽
        var nameOf = (delegate* unmanaged[Thiscall]<nint, ulong, byte*>)(nuint)Method(IGetFriendPersonaName);

        for (var i = 0; i < count && list.Count < max; i++)
        {
            ulong id;
            try
            {
                id = 0;
                byIndex(Iface, &id, i, FriendFlagImmediate);
            }
            catch
            {
                break;
            }
            if (id == 0)
                continue;
            var p = (byte*)nameOf(Iface, id);
            if (p == null)
                continue;
            var len = 0;
            while (len < 128 && p[len] != 0)
                len++;
            if (len is 0 or >= 128)
                continue;
            list.Add(System.Text.Encoding.UTF8.GetString(p, len));
        }
        return list;
    }
}
