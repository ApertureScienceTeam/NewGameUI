using GoldSrc.Avalonia;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Plugin.Engine.Steam;

/// <summary>
/// 服务器条目：<c>gameserveritem_t</c> 的托管映射（win-x86 32 位 ABI）：
/// 0x00 servernetadr_t(connPort/queryPort/ip, 8 字节)、0x08 ping、0x0C had-response、
/// 0x0D do-not-refresh、0x0E gameDir[32]、0x2E map[32]、0x4E gameDesc[64]、
/// 0x90 appId、0x94 players、0x98 maxPlayers、0x9C botPlayers、0xA0 password、
/// 0xA1 secure、0xA4 lastPlayed、0xA8 serverVersion、0xAC serverName[64]、
/// 0xEC gameTags[128]、0x16C steamID
/// （两个 bool 各占 1 字节，数组从 0x0E 开始，不是 0x10——差 2 个字节就会
/// 让每个字符串丢掉开头两个字符，且 had-response 读到 ping 的字节里。）
/// </summary>
public sealed class GameServerItem
{
    public uint Ip { get; init; }
    public ushort ConnectionPort { get; init; }
    public ushort QueryPort { get; init; }
    public int Ping { get; init; }
    public bool HadSuccessfulResponse { get; init; }
    public string GameDir { get; init; } = "";
    public string Map { get; init; } = "";
    public string GameDescription { get; init; } = "";
    public uint AppId { get; init; }
    public int Players { get; init; }
    public int MaxPlayers { get; init; }
    public int BotPlayers { get; init; }
    public bool Password { get; init; }
    public bool Secure { get; init; }
    public uint TimeLastPlayed { get; init; }
    public int ServerVersion { get; init; }
    public string ServerName { get; init; } = "";
    public string GameTags { get; init; } = "";

    /// <summary>连接地址，形如 1.2.3.4:27015（connect 命令用）</summary>
    public string Address => $"{IpText}:{ConnectionPort}";

    /// <summary>IP 点分文本（servernetadr_t::m_unIP 已是网络序）</summary>
    public string IpText =>
        $"{(Ip >> 24) & 0xFF}.{(Ip >> 16) & 0xFF}.{(Ip >> 8) & 0xFF}.{Ip & 0xFF}";

    public string PlayersText => $"{Players}/{MaxPlayers}";
}

/// <summary>一次服务器列表查询（对应一个 HServerListRequest）</summary>
public sealed unsafe class ServerQuery : IDisposable
{
    nint _request;
    nint _responseObject;
    bool _disposed;

    internal ServerQuery(nint responseObject) => _responseObject = responseObject;

    internal nint Request
    {
        get => _request;
        set => _request = value;
    }

    internal int Id { get; set; }

    /// <summary>收到一个服务器（在 Steam 回调线程上触发）</summary>
    public event Action<int>? ServerResponded;

    /// <summary>某个服务器无响应</summary>
    public event Action<int>? ServerFailed;

    /// <summary>整轮查询结束</summary>
    public event Action? Completed;

    public int Count => SteamServers.GetServerCount(this);

    public GameServerItem? GetServer(int index) => SteamServers.GetServerDetails(this, index);

    internal void RaiseResponded(int index) => ServerResponded?.Invoke(index);

    internal void RaiseFailed(int index) => ServerFailed?.Invoke(index);

    internal void RaiseCompleted() => Completed?.Invoke();

    public void Refresh() => SteamServers.RefreshQuery(this);

    public void Cancel() => SteamServers.CancelQuery(this);

    public bool IsRefreshing => SteamServers.IsRefreshing(this);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        SteamServers.Release(this);
        if (_responseObject != 0)
        {
            SteamServers.FreeResponseObject(_responseObject);
            _responseObject = 0;
        }
        GC.SuppressFinalize(this);
    }

    ~ServerQuery() => Dispose();
}

/// <summary>
/// ISteamMatchmakingServers002 互操作。原版 ServerBrowser.dll 的服务器列表全部由
/// Steam 提供（导入表只有 steam_api.dll 与 WS2_32 的 gethostbyname，没有自建查询），
/// 取接口的方式与它一致：
/// <code>
/// hUser   = SteamAPI_GetHSteamUser();
/// servers = SteamInternal_FindOrCreateUserInterface(hUser, "SteamMatchMakingServers002");
/// </code>
/// vtable 偏移用 SDK 公开顺序（Valve 只做尾部追加）：
/// 0 RequestInternetServerList / 1 RequestLANServerList / 2 RequestFriendsServerList /
/// 3 RequestFavoritesServerList / 4 RequestHistoryServerList / 5 RequestSpectatorServerList /
/// 6 ReleaseRequest / 7 GetServerDetails / 8 CancelQuery / 9 RefreshQuery /
/// 10 IsRefreshing / 11 GetServerCount / 12 RefreshServer
/// </summary>
public static unsafe class SteamServers
{
    /// <summary>Counter-Strike 的 AppId（原版查询用的就是这个）</summary>
    public const uint CounterStrikeAppId = 10;

    const int IRequestInternetServerList = 0;
    const int IRequestLanServerList = 1;
    const int IRequestFriendsServerList = 2;
    const int IRequestFavoritesServerList = 3;
    const int IRequestHistoryServerList = 4;
    const int IRequestSpectatorServerList = 5;
    const int IReleaseRequest = 6;
    const int IGetServerDetails = 7;
    const int ICancelQuery = 8;
    const int IRefreshQuery = 9;
    const int IIsRefreshing = 10;
    const int IGetServerCount = 11;
    const int IRefreshServer = 12;
    const int IPingServer = 13;
    const int IPlayersServer = 14;
    const int IRulesServer = 15;

    static readonly object s_lock = new();
    static readonly Dictionary<int, ServerQuery> s_queries = new();
    static int s_nextQueryId = 1;

    static nint s_iface;
    static bool s_lookupDone;
    static nint s_vtable;

    [DllImport("steam_api", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamAPI_GetHSteamUser")]
    static extern int SteamAPI_GetHSteamUser();

    [DllImport("steam_api", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamInternal_FindOrCreateUserInterface")]
    static extern nint SteamInternal_FindOrCreateUserInterface(int hSteamUser, byte* version);

    /// <summary>Steam 接口是否可用（Preview 进程或非 Steam 启动时为 false）</summary>
    public static bool Available
    {
        get
        {
            EnsureInterface();
            return s_iface != 0;
        }
    }

    static void EnsureInterface()
    {
        if (s_lookupDone)
            return;
        lock (s_lock)
        {
            if (s_lookupDone)
                return;
            s_lookupDone = true;
            try
            {
                fixed (byte* version = "SteamMatchMakingServers002\0"u8)
                {
                    var user = SteamAPI_GetHSteamUser();
                    s_iface = SteamInternal_FindOrCreateUserInterface(user, version);
                }
                GoldSrcAvalonia.Log?.Invoke($"SteamServers: iface=0x{s_iface:X}");
            }
            catch (Exception ex)
            {
                GoldSrcAvalonia.Log?.Invoke($"SteamServers: unavailable ({ex.GetType().Name}: {ex.Message})");
                s_iface = 0;
            }
        }
    }

    static nint Method(nint iface, int index)
    {
        var vtbl = *(nint**)iface;
        return vtbl[index];
    }

    // —— ISteamMatchmakingServerListResponse 回调对象 ——

    static nint Vtable
    {
        get
        {
            if (s_vtable != 0)
                return s_vtable;
            lock (s_lock)
            {
                if (s_vtable != 0)
                    return s_vtable;
                var vtbl = NativeMemory.Alloc(3 * (nuint)sizeof(nint));
                var p = (nint*)vtbl;
                p[0] = (nint)(delegate* unmanaged[Thiscall]<nint, nint, int, void>)&OnServerResponded;
                p[1] = (nint)(delegate* unmanaged[Thiscall]<nint, nint, int, void>)&OnServerFailedToRespond;
                p[2] = (nint)(delegate* unmanaged[Thiscall]<nint, nint, int, void>)&OnRefreshComplete;
                s_vtable = (nint)vtbl;
                return s_vtable;
            }
        }
    }

    static nint AllocResponseObject(int queryId)
    {
        // [0] = vtable 指针，[1] = queryId
        var obj = NativeMemory.Alloc(2 * (nuint)sizeof(nint));
        var p = (nint*)obj;
        p[0] = Vtable;
        p[1] = queryId;
        return (nint)obj;
    }

    internal static void FreeResponseObject(nint obj) => NativeMemory.Free((void*)obj);

    static ServerQuery? Find(nint self)
    {
        var id = (int)((nint*)self)[1];
        lock (s_lock)
            return s_queries.TryGetValue(id, out var q) ? q : null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    static void OnServerResponded(nint self, nint request, int index) => Find(self)?.RaiseResponded(index);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    static void OnServerFailedToRespond(nint self, nint request, int index) => Find(self)?.RaiseFailed(index);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    static void OnRefreshComplete(nint self, nint request, int response) => Find(self)?.RaiseCompleted();

    // —— 查询入口 ——

    /// <summary>
    /// 互联网服务器（Internet 页）。可选过滤器按原版 InternetGamesPage 的写法传
    /// MatchMakingKeyValuePair_t 数组（用 "region" 键 = Regions.vdf 里的 code）
    /// </summary>
    public static ServerQuery? RequestInternet(string? filterKey = null, string? filterValue = null) =>
        StartList(IRequestInternetServerList, filterKey, filterValue);

    /// <summary>局域网服务器（LAN 页）</summary>
    public static ServerQuery? RequestLan() => StartLan();

    /// <summary>Steam 收藏夹（Favorites 页）</summary>
    public static ServerQuery? RequestFavorites() => StartList(IRequestFavoritesServerList);

    /// <summary>Steam 历史记录（History 页）</summary>
    public static ServerQuery? RequestHistory() => StartList(IRequestHistoryServerList);

    /// <summary>观看服务器（Spectate 页）</summary>
    public static ServerQuery? RequestSpectator() => StartList(IRequestSpectatorServerList);

    /// <summary>朋友所在服务器（朋友页）</summary>
    public static ServerQuery? RequestFriends() => StartList(IRequestFriendsServerList);

    static ServerQuery? StartList(int methodIndex, string? filterKey = null, string? filterValue = null)
    {
        EnsureInterface();
        if (s_iface == 0)
            return null;
        var (query, response) = CreateQuery();
        var fn = (delegate* unmanaged[Thiscall]<nint, uint, nint, uint, nint, nint>)(nuint)Method(s_iface, methodIndex);

        // MatchMakingKeyValuePair_t 是 { char key[256]; char value[256]; }，
        // Steam 在调用里拷贝内容，返回后即可释放
        nint filters = 0;
        uint count = 0;
        if (!string.IsNullOrEmpty(filterKey))
        {
            filters = (nint)NativeMemory.AllocZeroed(512);
            WriteAscii((byte*)filters, filterKey!, 255);
            WriteAscii((byte*)(filters + 256), filterValue ?? "", 255);
            count = 1;
        }

        var request = fn(s_iface, CounterStrikeAppId, filters, count, response);
        if (filters != 0)
            NativeMemory.Free((void*)filters);
        return Finish(query, response, request);
    }

    static void WriteAscii(byte* dest, string text, int max)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        var n = Math.Min(bytes.Length, max);
        for (var i = 0; i < n; i++)
            dest[i] = bytes[i];
        dest[n] = 0;
    }

    static ServerQuery? StartLan()
    {
        EnsureInterface();
        if (s_iface == 0)
            return null;
        var (query, response) = CreateQuery();
        var fn = (delegate* unmanaged[Thiscall]<nint, uint, nint, nint>)(nuint)Method(s_iface, IRequestLanServerList);
        var request = fn(s_iface, CounterStrikeAppId, response);
        return Finish(query, response, request);
    }

    static (ServerQuery Query, nint Response) CreateQuery()
    {
        int id;
        lock (s_lock)
        {
            id = s_nextQueryId++;
            s_queries[id] = null!;
        }
        var response = AllocResponseObject(id);
        var query = new ServerQuery(response) { Id = id };
        lock (s_lock)
            s_queries[id] = query;
        return (query, response);
    }

    static ServerQuery? Finish(ServerQuery query, nint response, nint request)
    {
        if (request != 0)
        {
            query.Request = request;
            return query;
        }
        lock (s_lock)
            s_queries.Remove(query.Id);
        FreeResponseObject(response);
        return null;
    }

    // —— 列表读取 ——

    public static int GetServerCount(ServerQuery query) =>
        s_iface == 0 || query.Request == 0
            ? 0
            : ((delegate* unmanaged[Thiscall]<nint, nint, int>)(nuint)Method(s_iface, IGetServerCount))(s_iface, query.Request);

    public static GameServerItem? GetServerDetails(ServerQuery query, int index)
    {
        if (s_iface == 0 || query.Request == 0)
            return null;
        var fn = (delegate* unmanaged[Thiscall]<nint, nint, int, byte*>)(nuint)Method(s_iface, IGetServerDetails);
        var p = fn(s_iface, query.Request, index);
        return p == null ? null : Parse(p);
    }

    static string ReadUtf8(byte* p, int max)
    {
        var len = 0;
        while (len < max && p[len] != 0)
            len++;
        return len == 0 ? "" : System.Text.Encoding.UTF8.GetString(p, len);
    }

    static GameServerItem Parse(byte* p)
    {
        var adr = (ushort*)p;
        return new GameServerItem
        {
            ConnectionPort = adr[0],
            QueryPort = adr[1],
            Ip = *(uint*)(p + 4),
            Ping = *(int*)(p + 0x08),
            HadSuccessfulResponse = p[0x0C] != 0,   // 0x0C=had-response，0x0D=do-not-refresh
            GameDir = ReadUtf8(p + 0x0E, 32),
            Map = ReadUtf8(p + 0x2E, 32),
            GameDescription = ReadUtf8(p + 0x4E, 64),
            AppId = *(uint*)(p + 0x90),
            Players = *(int*)(p + 0x94),
            MaxPlayers = *(int*)(p + 0x98),
            BotPlayers = *(int*)(p + 0x9C),
            Password = p[0xA0] != 0,
            Secure = p[0xA1] != 0,
            TimeLastPlayed = *(uint*)(p + 0xA4),
            ServerVersion = *(int*)(p + 0xA8),
            ServerName = ReadUtf8(p + 0xAC, 64),
            GameTags = ReadUtf8(p + 0xEC, 128)
        };
    }

    // —— 其它 ——

    public static void RefreshQuery(ServerQuery query)
    {
        if (s_iface == 0 || query.Request == 0)
            return;
        ((delegate* unmanaged[Thiscall]<nint, nint, void>)(nuint)Method(s_iface, IRefreshQuery))(s_iface, query.Request);
    }

    public static void CancelQuery(ServerQuery query)
    {
        if (s_iface == 0 || query.Request == 0)
            return;
        ((delegate* unmanaged[Thiscall]<nint, nint, void>)(nuint)Method(s_iface, ICancelQuery))(s_iface, query.Request);
    }

    public static bool IsRefreshing(ServerQuery query)
    {
        if (s_iface == 0 || query.Request == 0)
            return false;
        return ((delegate* unmanaged[Thiscall]<nint, nint, int>)(nuint)Method(s_iface, IIsRefreshing))(s_iface, query.Request) != 0;
    }

    public static void RefreshServer(ServerQuery query, int index)
    {
        if (s_iface == 0 || query.Request == 0)
            return;
        ((delegate* unmanaged[Thiscall]<nint, nint, int, void>)(nuint)Method(s_iface, IRefreshServer))(s_iface, query.Request, index);
    }

    internal static void Release(ServerQuery query)
    {
        if (s_iface != 0 && query.Request != 0)
            ((delegate* unmanaged[Thiscall]<nint, nint, void>)(nuint)Method(s_iface, IReleaseRequest))(s_iface, query.Request);
        query.Request = 0;
        lock (s_lock)
            s_queries.Remove(query.Id);
    }

    // —— 玩家列表（ISteamMatchmakingServers::PlayersServer + ISteamMatchmakingPlayersResponse）——
    // 原版 DialogGameInfo 的 PlayerList 就是这么拉的：AddPlayerToList(name, score, timePlayed)

    static nint s_playersVtable;

    static nint PlayersVtable
    {
        get
        {
            if (s_playersVtable != 0)
                return s_playersVtable;
            lock (s_lock)
            {
                if (s_playersVtable != 0)
                    return s_playersVtable;
                var vtbl = NativeMemory.Alloc(3 * (nuint)sizeof(nint));
                var p = (nint*)vtbl;
                p[0] = (nint)(delegate* unmanaged[Thiscall]<nint, byte*, int, float, void>)&OnAddPlayerToList;
                p[1] = (nint)(delegate* unmanaged[Thiscall]<nint, void>)&OnPlayersFailedToRespond;
                p[2] = (nint)(delegate* unmanaged[Thiscall]<nint, void>)&OnPlayersRefreshComplete;
                s_playersVtable = (nint)vtbl;
                return s_playersVtable;
            }
        }
    }

    static PlayersQuery? PlayersOf(nint self)
    {
        var handle = (nint)((nint*)self)[1];
        return handle == 0 ? null : GCHandle.FromIntPtr(handle).Target as PlayersQuery;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    static void OnAddPlayerToList(nint self, byte* name, int score, float timePlayed) =>
        PlayersOf(self)?.Add(ReadUtf8(name, 128), score);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    static void OnPlayersFailedToRespond(nint self) => PlayersOf(self)?.Fail();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    static void OnPlayersRefreshComplete(nint self) => PlayersOf(self)?.Complete();

    // —— 单服 Ping（ISteamMatchmakingServers::PingServer + ISteamMatchmakingPingResponse）——
    // 原版 "刷新服务器" 走的就是它：ServerResponded(gameserveritem_t&) / ServerFailedToRespond

    static nint s_pingVtable;

    static nint PingVtable
    {
        get
        {
            if (s_pingVtable != 0)
                return s_pingVtable;
            lock (s_lock)
            {
                if (s_pingVtable != 0)
                    return s_pingVtable;
                var vtbl = NativeMemory.Alloc(2 * (nuint)sizeof(nint));
                var p = (nint*)vtbl;
                p[0] = (nint)(delegate* unmanaged[Thiscall]<nint, byte*, void>)&OnPingResponded;
                p[1] = (nint)(delegate* unmanaged[Thiscall]<nint, void>)&OnPingFailed;
                s_pingVtable = (nint)vtbl;
                return s_pingVtable;
            }
        }
    }

    static PingQuery? PingOf(nint self) => HandleTarget<PingQuery>(((nint*)self)[1]);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    static void OnPingResponded(nint self, byte* server) =>
        PingOf(self)?.Finish(server == null ? null : Parse(server));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    static void OnPingFailed(nint self) => PingOf(self)?.Fail();

    public static PingQuery? PingServer(uint ip, ushort port)
    {
        EnsureInterface();
        if (s_iface == 0)
            return null;
        var query = new PingQuery();
        query.Attach(PingVtable);
        var fn = (delegate* unmanaged[Thiscall]<nint, uint, ushort, nint, void>)(nuint)Method(s_iface, IPingServer);
        fn(s_iface, ip, port, query.ResponseObject);
        return query;
    }

    // —— 服务器规则（ISteamMatchmakingServers::RulesServer + ISteamMatchmakingRulesResponse）——

    static nint s_rulesVtable;

    static nint RulesVtable
    {
        get
        {
            if (s_rulesVtable != 0)
                return s_rulesVtable;
            lock (s_lock)
            {
                if (s_rulesVtable != 0)
                    return s_rulesVtable;
                var vtbl = NativeMemory.Alloc(3 * (nuint)sizeof(nint));
                var p = (nint*)vtbl;
                p[0] = (nint)(delegate* unmanaged[Thiscall]<nint, byte*, byte*, void>)&OnRulesResponded;
                p[1] = (nint)(delegate* unmanaged[Thiscall]<nint, void>)&OnRulesFailed;
                p[2] = (nint)(delegate* unmanaged[Thiscall]<nint, void>)&OnRulesComplete;
                s_rulesVtable = (nint)vtbl;
                return s_rulesVtable;
            }
        }
    }

    static RulesQuery? RulesOf(nint self) => HandleTarget<RulesQuery>(((nint*)self)[1]);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    static void OnRulesResponded(nint self, byte* rule, byte* value) =>
        RulesOf(self)?.Add(rule == null ? "" : ReadUtf8(rule, 128), value == null ? "" : ReadUtf8(value, 256));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    static void OnRulesFailed(nint self) => RulesOf(self)?.Fail();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    static void OnRulesComplete(nint self) => RulesOf(self)?.Complete();

    public static RulesQuery? RequestRules(uint ip, ushort port)
    {
        EnsureInterface();
        if (s_iface == 0)
            return null;
        var query = new RulesQuery();
        query.Attach(RulesVtable);
        var fn = (delegate* unmanaged[Thiscall]<nint, uint, ushort, nint, void>)(nuint)Method(s_iface, IRulesServer);
        fn(s_iface, ip, port, query.ResponseObject);
        return query;
    }

    internal static T? HandleTarget<T>(nint handle) where T : class =>
        handle == 0 ? null : GCHandle.FromIntPtr(handle).Target as T;

    /// <summary>拉某个服务器的玩家列表；返回 null 表示 Steam 接口不可用</summary>
    public static PlayersQuery? RequestPlayers(uint ip, ushort port)
    {
        EnsureInterface();
        if (s_iface == 0)
            return null;
        var query = new PlayersQuery();
        query.Attach(PlayersVtable);
        var fn = (delegate* unmanaged[Thiscall]<nint, uint, ushort, nint, void>)(nuint)Method(s_iface, IPlayersServer);
        fn(s_iface, ip, port, query.ResponseObject);
        return query;
    }
}

/// <summary>一次玩家列表查询（ISteamMatchmakingPlayersResponse）</summary>
public sealed unsafe class PlayersQuery : IDisposable
{
    readonly List<(string Name, int Score)> _players = [];
    nint _gchandle;
    bool _disposed;

    public IReadOnlyList<(string Name, int Score)> Players => _players;

    public bool Done { get; private set; }

    public bool Failed { get; private set; }

    /// <summary>有新数据/结束时触发（在 Steam 线程上）</summary>
    public event Action? Changed;

    internal nint ResponseObject { get; private set; }

    internal void Attach(nint vtable)
    {
        _gchandle = GCHandle.ToIntPtr(GCHandle.Alloc(this));
        var obj = NativeMemory.Alloc(2 * (nuint)sizeof(nint));
        var p = (nint*)obj;
        p[0] = vtable;
        p[1] = _gchandle;
        ResponseObject = (nint)obj;
    }

    internal void Add(string name, int score)
    {
        _players.Add((name, score));
        Changed?.Invoke();
    }

    internal void Fail()
    {
        Failed = true;
        Done = true;
        Changed?.Invoke();
    }

    internal void Complete()
    {
        Done = true;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (ResponseObject != 0)
        {
            NativeMemory.Free((void*)ResponseObject);
            ResponseObject = 0;
        }
        if (_gchandle != 0)
        {
            GCHandle.FromIntPtr(_gchandle).Free();
            _gchandle = 0;
        }
        GC.SuppressFinalize(this);
    }

    ~PlayersQuery() => Dispose();
}

/// <summary>单服 ping 查询（ISteamMatchmakingPingResponse）</summary>
public sealed unsafe class PingQuery : IDisposable
{
    nint _gchandle;
    bool _disposed;

    public GameServerItem? Result { get; private set; }
    public bool Done { get; private set; }
    public bool Failed { get; private set; }
    public event Action? Changed;

    internal nint ResponseObject { get; private set; }

    internal void Attach(nint vtable)
    {
        _gchandle = GCHandle.ToIntPtr(GCHandle.Alloc(this));
        var obj = NativeMemory.Alloc(2 * (nuint)sizeof(nint));
        var p = (nint*)obj;
        p[0] = vtable;
        p[1] = _gchandle;
        ResponseObject = (nint)obj;
    }

    internal void Finish(GameServerItem? item)
    {
        Result = item;
        Failed = item == null;
        Done = true;
        Changed?.Invoke();
    }

    internal void Fail() => Finish(null);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (ResponseObject != 0)
        {
            NativeMemory.Free((void*)ResponseObject);
            ResponseObject = 0;
        }
        if (_gchandle != 0)
        {
            GCHandle.FromIntPtr(_gchandle).Free();
            _gchandle = 0;
        }
        GC.SuppressFinalize(this);
    }

    ~PingQuery() => Dispose();
}

/// <summary>服务器规则查询（ISteamMatchmakingRulesResponse）</summary>
public sealed unsafe class RulesQuery : IDisposable
{
    readonly List<(string Key, string Value)> _rules = [];
    nint _gchandle;
    bool _disposed;

    public IReadOnlyList<(string Key, string Value)> Rules => _rules;
    public bool Done { get; private set; }
    public bool Failed { get; private set; }
    public event Action? Changed;

    internal nint ResponseObject { get; private set; }

    internal void Attach(nint vtable)
    {
        _gchandle = GCHandle.ToIntPtr(GCHandle.Alloc(this));
        var obj = NativeMemory.Alloc(2 * (nuint)sizeof(nint));
        var p = (nint*)obj;
        p[0] = vtable;
        p[1] = _gchandle;
        ResponseObject = (nint)obj;
    }

    internal void Add(string key, string value)
    {
        _rules.Add((key, value));
        Changed?.Invoke();
    }

    internal void Fail()
    {
        Failed = true;
        Done = true;
        Changed?.Invoke();
    }

    internal void Complete()
    {
        Done = true;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (ResponseObject != 0)
        {
            NativeMemory.Free((void*)ResponseObject);
            ResponseObject = 0;
        }
        if (_gchandle != 0)
        {
            GCHandle.FromIntPtr(_gchandle).Free();
            _gchandle = 0;
        }
        GC.SuppressFinalize(this);
    }

    ~RulesQuery() => Dispose();
}
