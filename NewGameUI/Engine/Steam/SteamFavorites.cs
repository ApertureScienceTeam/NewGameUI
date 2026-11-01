using GoldSrc.Avalonia;
using System.Runtime.InteropServices;

namespace Plugin.Engine.Steam;

/// <summary>Steam 收藏/历史里的一条记录（ISteamMatchmaking 的 favorite 条目）</summary>
public sealed class FavoriteGame
{
    public uint AppId { get; init; }
    public uint Ip { get; init; }
    public ushort ConnectionPort { get; init; }
    public ushort QueryPort { get; init; }
    public uint Flags { get; init; }
    public uint LastPlayed { get; init; }

    public string IpText => $"{(Ip >> 24) & 0xFF}.{(Ip >> 16) & 0xFF}.{(Ip >> 8) & 0xFF}.{Ip & 0xFF}";
    public string Address => $"{IpText}:{ConnectionPort}";
    public bool IsFavorite => (Flags & SteamFavorites.FlagFavorite) != 0;
    public bool IsHistory => (Flags & SteamFavorites.FlagHistory) != 0;
}

/// <summary>
/// ISteamMatchmaking009 的收藏/历史接口。原版 ServerBrowser.dll 的 Favorites/History 页
/// 由 Steam 保存（重启后仍在），添加服务器也是写进这里，所以照同一条路径做：
/// <code>iface = SteamInternal_FindOrCreateUserInterface(user, "SteamMatchMaking009")</code>
/// Favorite 四个方法是 ISteamMatchmaking 最早定义的虚函数，位于 vtable 最前：
/// 0 GetFavoriteGameCount / 1 GetFavoriteGame / 2 AddFavoriteGame / 3 RemoveFavoriteGame
/// （lobby 相关方法是后来追加在后面的）
/// </summary>
public static unsafe class SteamFavorites
{
    /// <summary>k_unFavoriteFlagFavorite</summary>
    public const uint FlagFavorite = 0x01;

    /// <summary>k_unFavoriteFlagHistory</summary>
    public const uint FlagHistory = 0x02;

    const int IGetFavoriteGameCount = 0;
    const int IGetFavoriteGame = 1;
    const int IAddFavoriteGame = 2;
    const int IRemoveFavoriteGame = 3;

    static readonly object s_lock = new();
    static nint s_iface;
    static bool s_done;
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
                    fixed (byte* version = "SteamMatchMaking009\0"u8)
                    {
                        var user = SteamAPI_GetHSteamUser();
                        s_iface = SteamInternal_FindOrCreateUserInterface(user, version);
                    }
                    GoldSrcAvalonia.Log?.Invoke($"SteamFavorites: iface=0x{s_iface:X}");
                }
                catch (Exception ex)
                {
                    GoldSrcAvalonia.Log?.Invoke($"SteamFavorites: unavailable ({ex.GetType().Name})");
                    s_iface = 0;
                }
            }
            return s_iface;
        }
    }

    static nint Method(int index) => (*(nint**)Iface)[index];

    /// <summary>接口是否可用：用一个“无害”的调用自检，避免虚表布局不符时误用</summary>
    public static bool Available
    {
        get
        {
            if (Iface == 0)
                return false;
            if (s_verified)
                return true;
            var count = Count;
            s_verified = count is >= 0 and <= 5000;
            if (!s_verified)
                GoldSrcAvalonia.Log?.Invoke($"SteamFavorites: unexpected count {count}, disabled");
            return s_verified;
        }
    }

    public static int Count
    {
        get
        {
            if (Iface == 0)
                return -1;
            try
            {
                return ((delegate* unmanaged[Thiscall]<nint, int>)(nuint)Method(IGetFavoriteGameCount))(Iface);
            }
            catch
            {
                return -1;
            }
        }
    }

    public static List<FavoriteGame> GetAll()
    {
        var list = new List<FavoriteGame>();
        if (!Available)
            return list;
        var count = Count;
        var fn = (delegate* unmanaged[Thiscall]<nint, int, uint*, uint*, ushort*, ushort*, uint*, uint*, byte>)(nuint)Method(IGetFavoriteGame);
        for (var i = 0; i < count; i++)
        {
            uint appId, ip, flags, lastPlayed;
            ushort connPort, queryPort;
            var ok = fn(Iface, i, &appId, &ip, &connPort, &queryPort, &flags, &lastPlayed);
            if (ok == 0)
                continue;
            list.Add(new FavoriteGame
            {
                AppId = appId,
                Ip = ip,
                ConnectionPort = connPort,
                QueryPort = queryPort,
                Flags = flags,
                LastPlayed = lastPlayed
            });
        }
        return list;
    }

    /// <summary>加入收藏或历史（flags = FlagFavorite / FlagHistory）</summary>
    public static bool Add(uint ip, ushort connectionPort, ushort queryPort, uint flags)
    {
        if (!Available)
            return false;
        var fn = (delegate* unmanaged[Thiscall]<nint, uint, uint, ushort, ushort, uint, uint, int>)(nuint)Method(IAddFavoriteGame);
        var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var index = fn(Iface, SteamServers.CounterStrikeAppId, ip, connectionPort, queryPort, flags, now);
        return index >= 0;
    }

    public static bool Remove(uint ip, ushort connectionPort, ushort queryPort, uint flags)
    {
        if (!Available)
            return false;
        var fn = (delegate* unmanaged[Thiscall]<nint, uint, uint, ushort, ushort, uint, byte>)(nuint)Method(IRemoveFavoriteGame);
        return fn(Iface, SteamServers.CounterStrikeAppId, ip, connectionPort, queryPort, flags) != 0;
    }

    /// <summary>把 "1.2.3.4:27015" 解析成 (网络序 ip, 连接端口)</summary>
    public static bool TryParseAddress(string text, out uint ip, out ushort port)
    {
        ip = 0;
        port = 27015;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var s = text.Trim();
        var colon = s.LastIndexOf(':');
        if (colon > 0)
        {
            if (!ushort.TryParse(s[(colon + 1)..], out port))
                return false;
            s = s[..colon];
        }
        var parts = s.Split('.');
        if (parts.Length != 4)
            return false;
        uint v = 0;
        foreach (var part in parts)
        {
            if (!byte.TryParse(part, out var b))
                return false;
            v = (v << 8) | b;
        }
        ip = v;
        return true;
    }
}
