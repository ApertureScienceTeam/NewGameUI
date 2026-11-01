using System.Text.RegularExpressions;
using GoldSrc.Avalonia;

namespace Plugin.Engine;

/// <summary>
/// cstrike\settings.scr 读写：原版新建游戏对话框的“上次设置”持久化存储。
///
/// 原版 GameUI.dll（CCreateMultiplayerGameGameplayPage）在开局时把控件值收集后
/// 整文件重写 settings.scr（WriteGameplaySettings @0x1fef0），文件头自述
/// "stores last settings chosen, too"；下次创建对话框时按条目回读（此时
/// mp_* cvar 尚未注册——mp.dll 未加载——控件直接用文件里的存储值）。
/// 引擎不持久化这些值（hostname/mp_* 均非 FCVAR_ARCHIVE，不进 config.cfg，
/// hw.dll 也不写 game.cfg），settings.scr 是唯一的跨重启存储，且与原版游戏互认。
///
/// 这里只替换每个条目内最后一个单行取值块 { "value" }，头注释/类型/范围/列表
/// 原样保留；文件或条目缺失时 GetValue 返回 null，由调用方决定回退值。
/// 编码按引擎惯例用系统 ANSI（中文系统 = GBK，服务器名可含中文）。
/// </summary>
public static class SettingsScr
{
    static readonly object Lock = new();
    static Dictionary<string, string>? s_values;

    /// <summary>读存储值；文件或条目缺失返回 null</summary>
    public static string? GetValue(string cvar)
    {
        lock (Lock)
        {
            var values = Load();
            return values.TryGetValue(cvar, out var v) ? v : null;
        }
    }

    /// <summary>把单个条目的取值块改写为新值并落盘（条目不存在则忽略）</summary>
    public static void SetValue(string cvar, string value)
    {
        lock (Lock)
        {
            var path = FilePath();
            if (path == null)
                return;
            try
            {
                var lines = File.ReadAllLines(path, EngineCommands.Ansi).ToList();
                if (!ReplaceValue(lines, cvar, value))
                    return;
                File.WriteAllLines(path, lines, EngineCommands.Ansi);
                s_values = null;
            }
            catch (Exception ex)
            {
                GoldSrcAvalonia.Log?.Invoke($"SettingsScr.SetValue({cvar}) failed: {ex.Message}");
            }
        }
    }

    static string? FilePath()
    {
        var dir = SavedGameConfig.GameDirectory;
        if (dir == null)
            return null;
        var path = Path.Combine(dir, "settings.scr");
        return File.Exists(path) ? path : null;
    }

    static Dictionary<string, string> Load()
    {
        if (s_values != null)
            return s_values;
        s_values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = FilePath();
        if (path == null)
            return s_values;
        try
        {
            var lines = File.ReadAllLines(path, EngineCommands.Ansi).ToList();
            for (var i = 0; i < lines.Count; i++)
            {
                var name = LoneQuoted(lines[i]);
                if (name == null)
                    continue;
                // 条目块：名字行后的第一个 '{' 到配对 '}'
                var end = BlockEnd(lines, i);
                if (end < 0)
                    continue;
                // 块内最后一个 { "value" } 即存储值
                var value = BlockValue(lines, i);
                if (value != null)
                    s_values[name] = value;
                i = end; // 跳过块内部：prompt 行（"#Valve_Hostname"）同样是孤立带引号行
            }
        }
        catch (Exception ex)
        {
            GoldSrcAvalonia.Log?.Invoke($"SettingsScr.Load failed: {ex.Message}");
        }
        return s_values;
    }

    /// <summary>整行恰为一个带引号 token（条目名行），返回去引号内容</summary>
    static string? LoneQuoted(string line)
    {
        var t = line.Trim();
        if (t.Length < 2 || t[0] != '"' || t[^1] != '"' || t.AsSpan(1, t.Length - 2).Contains('"'))
            return null;
        return t[1..^1];
    }

    /// <summary>条目名字行（index）所在块内最后一个 { "value" } 的值；无则 null</summary>
    static string? BlockValue(List<string> lines, int nameIndex)
    {
        var end = BlockEnd(lines, nameIndex);
        if (end < 0)
            return null;
        for (var j = end - 1; j > nameIndex; j--)
        {
            var m = Regex.Match(lines[j], @"^\s*\{\s*""(.*)""\s*\}\s*$");
            if (m.Success)
                return m.Groups[1].Value;
        }
        return null;
    }

    /// <summary>条目块结尾行号（名字行后的 '{' 的配对 '}'）；结构不符返回 -1</summary>
    static int BlockEnd(List<string> lines, int nameIndex)
    {
        var depth = 0;
        var started = false;
        for (var i = nameIndex + 1; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (!started)
            {
                if (t == "{")
                {
                    started = true;
                    depth = 1;
                }
                else if (t.Length != 0 && !t.StartsWith("//"))
                    break;
                continue;
            }
            if (t == "{")
                depth++;
            else if (t == "}")
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }
        return -1;
    }

    static bool ReplaceValue(List<string> lines, string cvar, string value)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (!string.Equals(LoneQuoted(lines[i]), cvar, StringComparison.OrdinalIgnoreCase))
                continue;
            var end = BlockEnd(lines, i);
            if (end < 0)
                continue;
            for (var j = end - 1; j > i; j--)
            {
                var m = Regex.Match(lines[j], @"^\s*\{\s*""(.*)""\s*\}\s*$");
                if (!m.Success)
                    continue;
                lines[j] = lines[j][..m.Groups[1].Index] + value + lines[j][(m.Groups[1].Index + m.Groups[1].Length)..];
                return true;
            }
        }
        return false;
    }
}
