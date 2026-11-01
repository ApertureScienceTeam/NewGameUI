using GoldSrc.Avalonia;
using Microsoft.Win32;

namespace Plugin.Engine;

/// <summary>
/// 磁盘“保存的设置”读取：mod 目录下 config.cfg 及其 exec 链（controller.cfg / userconfig.cfg）。
///
/// 原版（GameUI.dll OptionsDialog，COptionsSub* + CCvar* 控件）打开设置页时从引擎 cvar 系统
/// 读当前值；引擎在设置变更/退出时把 FCVAR_ARCHIVE cvar 与键位写回 config.cfg（文件头注释：
/// "This file is overwritten whenever you change your user settings in the game."）。
/// 无引擎环境（Preview）下 cvar 系统不存在，这里解析磁盘配置作为读取来源，
/// 保证设置页显示的就是保存值而不是代码里的 fallback。
///
/// 解析规则按引擎 exec 语义：顺序执行，后行覆盖前行，exec 就地递归（限深防环），
/// bind "key" "action" 记入键位表（action → key），unbindall 清表。
/// </summary>
public static class SavedGameConfig
{
    static readonly object s_lock = new();
    static bool s_loaded;
    static string? s_gameDir;
    static Dictionary<string, string> s_cvars = new(StringComparer.OrdinalIgnoreCase);
    // action → 绑定到它的全部按键（CS 支持每个动作绑两个键，这里保留全量）
    static Dictionary<string, List<string>> s_binds = new(StringComparer.OrdinalIgnoreCase);
    // key → action 反向表（unbind 按键摘除时用）
    static Dictionary<string, string> s_keyToAction = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>mod 目录（含 config.cfg），探测顺序：引擎 → GOLDSRC_GAMEDIR → Steam 注册表/库</summary>
    public static string? GameDirectory
    {
        get
        {
            lock (s_lock)
            {
                if (s_gameDir == null)
                    s_gameDir = LocateGameDir();
                return s_gameDir;
            }
        }
    }

    /// <summary>读保存的 cvar 值（解析 exec 链后的最终值），无则 null</summary>
    public static string? GetCvar(string name)
    {
        lock (s_lock)
        {
            EnsureLoaded();
            return s_cvars.TryGetValue(name, out var v) ? v : null;
        }
    }

    /// <summary>读保存的键位：绑定到该 action 的按键列表（config 中出现顺序，可为空表）</summary>
    public static IReadOnlyList<string> GetBinds(string action)
    {
        lock (s_lock)
        {
            EnsureLoaded();
            return s_binds.TryGetValue(action, out var keys) ? keys : Array.Empty<string>();
        }
    }

    /// <summary>使缓存失效（设置写盘后调用）</summary>
    public static void Invalidate()
    {
        lock (s_lock)
            s_loaded = false;
    }

    static void EnsureLoaded()
    {
        if (s_loaded)
            return;
        s_loaded = true;
        s_cvars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        s_binds = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        s_keyToAction = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dir = GameDirectory;
        GoldSrcAvalonia.Log?.Invoke($"SavedGameConfig: dir={dir ?? "(null)"}");
        if (dir == null)
            return;
        try
        {
            ParseFile(Path.Combine(dir, "config.cfg"), new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
            GoldSrcAvalonia.Log?.Invoke($"SavedGameConfig: cvars={s_cvars.Count} binds={s_binds.Count}");
        }
        catch (Exception ex)
        {
            GoldSrcAvalonia.Log?.Invoke($"SavedGameConfig load failed: {ex.Message}");
        }
    }

    static void ParseFile(string path, HashSet<string> visited, int depth)
    {
        // exec 就地递归但限深防环（config.cfg ↔ userconfig.cfg 互 exec 是真实存在的）
        if (depth > 4 || !File.Exists(path) || !visited.Add(Path.GetFullPath(path)))
            return;
        foreach (var raw in File.ReadAllLines(path))
        {
            var tokens = Tokenize(raw);
            if (tokens.Count == 0)
                continue;
            var cmd = tokens[0];
            switch (cmd.ToLowerInvariant())
            {
                case "bind":
                    // bind "key" "action"：一键只指向一个动作，重复绑定先摘旧的
                    if (tokens.Count >= 3)
                        BindKey(tokens[1], tokens[2]);
                    break;
                case "unbind":
                    // unbind "key"：按按键摘除（GoldSrc 没有按动作解绑的命令）
                    if (tokens.Count >= 2)
                        UnbindKey(tokens[1]);
                    break;
                case "unbindall":
                    s_binds.Clear();
                    break;
                case "exec":
                    if (tokens.Count >= 2 && Path.IsPathRooted(tokens[1]) == false)
                        ParseFile(Path.Combine(Path.GetDirectoryName(path)!, tokens[1]), visited, depth + 1);
                    break;
                case "setinfo":
                case "echo":
                    break;
                default:
                    // cvar 行：name "value" 或 name value；+命令/-命令/杂项命令跳过
                    if (tokens.Count >= 2 && cmd.Length > 0 && (char.IsLetter(cmd[0]) || cmd[0] == '_')
                        && cmd[0] != '+' && cmd[0] != '-')
                        s_cvars[cmd] = tokens[1];
                    break;
            }
        }
    }

    /// <summary>挂键：一键只指向一个动作，重复绑定先从旧动作摘除</summary>
    static void BindKey(string key, string action)
    {
        UnbindKey(key);
        if (!s_binds.TryGetValue(action, out var list))
            s_binds[action] = list = new List<string>();
        if (!list.Contains(key, StringComparer.OrdinalIgnoreCase))
            list.Add(key);
        s_keyToAction[key] = action;
    }

    /// <summary>摘键：按按键移除它当前指向的动作</summary>
    static void UnbindKey(string key)
    {
        if (s_keyToAction.TryGetValue(key, out var action) && s_binds.TryGetValue(action, out var list))
            list.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        s_keyToAction.Remove(key);
    }

    /// <summary>config 行分词：双引号内为一个参数（去引号），其余按空白切</summary>
    static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        int i = 0, n = line.Length;
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(line[i]))
                i++;
            if (i >= n || line[i] == '/' && i + 1 < n && line[i + 1] == '/')
                break;
            if (line[i] == '"')
            {
                int j = i + 1;
                while (j < n && line[j] != '"')
                    j++;
                tokens.Add(line.Substring(i + 1, j - i - 1));
                i = j + 1;
            }
            else
            {
                int j = i;
                while (j < n && !char.IsWhiteSpace(line[j]))
                    j++;
                tokens.Add(line.Substring(i, j - i));
                i = j;
            }
        }
        return tokens;
    }

    /// <summary>优先引擎提供的 mod 目录；Preview 无引擎时经环境变量 / Steam 注册表与库目录探测</summary>
    static string? LocateGameDir()
    {
        // 引擎可能返回相对路径（如 "cstrike"，依赖进程工作目录）——统一转绝对路径
        static string Abs(string p)
        {
            try
            {
                return Path.GetFullPath(p);
            }
            catch
            {
                return p;
            }
        }

        var engineDir = EngineCommands.GetEngineGameDirectory();
        if (engineDir != null)
        {
            engineDir = Abs(engineDir);
            if (File.Exists(Path.Combine(engineDir, "config.cfg")))
            {
                GoldSrcAvalonia.Log?.Invoke($"SavedGameConfig: engineDir={engineDir}");
                return engineDir;
            }
        }

        var env = Environment.GetEnvironmentVariable("GOLDSRC_GAMEDIR");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "config.cfg")))
            return env;

        // 不依赖注册表也能定位：游戏进程的工作目录 / 插件目录上层就是游戏根目录
        foreach (var baseDir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            foreach (var sub in new[] { "", "cstrike" })
            {
                var dir = Path.Combine(baseDir, sub);
                if (File.Exists(Path.Combine(dir, "config.cfg")))
                {
                    GoldSrcAvalonia.Log?.Invoke($"SavedGameConfig: located via {Abs(baseDir)}+{sub}");
                    return Abs(dir);
                }
            }
        }

        foreach (var root in SteamRoots())
        {
            var candidate = Path.Combine(root, "steamapps", "common", "Half-Life", "cstrike");
            if (File.Exists(Path.Combine(candidate, "config.cfg")))
            {
                GoldSrcAvalonia.Log?.Invoke($"SavedGameConfig: located via Steam root {root}");
                return candidate;
            }
        }
        return engineDir;
    }

    /// <summary>Steam 安装目录 + 全部库目录（libraryfolders.vdf）</summary>
    static IEnumerable<string> SteamRoots()
    {
        var roots = new List<string>();
        void Add(string? path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && !roots.Contains(path, StringComparer.OrdinalIgnoreCase))
                roots.Add(path);
        }

        // HKCU\Software\Valve\Steam → SteamPath（Steam 本体目录）
        string? steamPath = null;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            steamPath = k?.GetValue("SteamPath") as string;
            Add(steamPath);
        }
        catch
        {
        }
        // HKLM\SOFTWARE\Valve\Steam → InstallPath（32 位进程自动落到 WOW6432Node；64 位显式查 32 位视图）
        foreach (var view in new[] { RegistryView.Default, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var k = baseKey.OpenSubKey(@"Software\Valve\Steam");
                Add(k?.GetValue("InstallPath") as string);
            }
            catch
            {
            }
        }

        // 追加库目录：{steamPath}/steamapps/libraryfolders.vdf 里的 "path" 条目
        if (steamPath != null)
        {
            try
            {
                var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    foreach (var raw in File.ReadAllLines(vdf))
                    {
                        var t = raw.Trim();
                        if (t.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
                        {
                            var q = t.IndexOf('"', 6);
                            if (q >= 0)
                            {
                                var end = t.IndexOf('"', q + 1);
                                if (end > q)
                                    Add(t.Substring(q + 1, end - q - 1).Replace("\\\\", "\\"));
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }
        return roots;
    }
}
