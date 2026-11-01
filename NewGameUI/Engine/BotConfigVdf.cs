using System.Text;
using GoldSrc.Avalonia;

namespace Plugin.Engine;

/// <summary>
/// cstrike\CSBotConfig.vdf 读写：新建游戏对话框的跨重启地图记忆。
///
/// 原版 GameUI.dll 构造 CCreateMultiplayerGameDialog 时会无条件创建
/// KeyValues("CSBotConfig") 并 LoadFromFile(fs, "CSBotConfig.vdf", "CONFIG")
/// （0x1EBB7），随后取 "map" 键恢复地图下拉（0x1EEECC）；但只有 CZ 分支在
/// 开局时 SetString("map") + SaveToFile 写回（0x1F2CD/0x1F2E0），CS 1.6 的
/// Start 路径从不写该文件，恢复路径因此一直空转。
/// 这里按 CZ 同款格式补上写侧：跨重启记住上次开的地图；原版 UI 读同一份
/// 文件时同样能恢复（复活的正是它自己的恢复路径，无格式偏差）。
///
/// 文件为 vgui KeyValues 平面键值（"根名" { "key" "value" ... }，原版 bot_*
/// 键保留不破坏）；编码按引擎惯例用系统 ANSI（中文系统 = GBK）。
/// </summary>
public static class BotConfigVdf
{
    const string RootName = "CSBotConfig";
    const string FileName = "CSBotConfig.vdf";

    static readonly object Lock = new();

    /// <summary>读顶层键值；文件或键缺失返回 null</summary>
    public static string? GetValue(string key)
    {
        lock (Lock)
        {
            var path = FilePath();
            if (path == null || !File.Exists(path))
                return null;
            try
            {
                var tokens = new List<string>();
                foreach (var line in File.ReadAllLines(path, EngineCommands.Ansi))
                    Tokenize(line, tokens);
                return ParsePairs(tokens).TryGetValue(key, out var v) ? v : null;
            }
            catch (Exception ex)
            {
                GoldSrcAvalonia.Log?.Invoke($"BotConfigVdf.GetValue({key}) failed: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>更新或插入顶层键并落盘；其余键与顺序保留，文件不存在时新建</summary>
    public static void SetValue(string key, string value)
    {
        lock (Lock)
        {
            var dir = SavedGameConfig.GameDirectory;
            if (dir == null)
                return;
            var path = Path.Combine(dir, FileName);
            try
            {
                var entries = new List<(string Key, string Value)>();
                if (File.Exists(path))
                {
                    var tokens = new List<string>();
                    foreach (var line in File.ReadAllLines(path, EngineCommands.Ansi))
                        Tokenize(line, tokens);
                    entries = ParseEntries(tokens);
                }
                var replaced = false;
                for (var i = 0; i < entries.Count; i++)
                {
                    if (!string.Equals(entries[i].Key, key, StringComparison.OrdinalIgnoreCase))
                        continue;
                    entries[i] = (entries[i].Key, value);
                    replaced = true;
                }
                if (!replaced)
                    entries.Add((key, value));
                var sb = new StringBuilder();
                sb.AppendLine($"\"{RootName}\"");
                sb.AppendLine("{");
                foreach (var (k, v) in entries)
                    sb.AppendLine($"\t\"{Escape(k)}\"\t\"{Escape(v)}\"");
                sb.AppendLine("}");
                File.WriteAllText(path, sb.ToString(), EngineCommands.Ansi);
            }
            catch (Exception ex)
            {
                GoldSrcAvalonia.Log?.Invoke($"BotConfigVdf.SetValue({key}) failed: {ex.Message}");
            }
        }
    }

    static string? FilePath()
    {
        var dir = SavedGameConfig.GameDirectory;
        return dir == null ? null : Path.Combine(dir, FileName);
    }

    /// <summary>单行分词：带引号 token（支持 \" 与 \\ 转义）、孤立花括号、裸 token；// 注释截断</summary>
    static void Tokenize(string line, List<string> tokens)
    {
        int i = 0, n = line.Length;
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(line[i]))
                i++;
            if (i >= n || line[i] == '/' && i + 1 < n && line[i + 1] == '/')
                break;
            if (line[i] == '"')
            {
                var sb = new StringBuilder();
                var j = i + 1;
                while (j < n && line[j] != '"')
                {
                    if (line[j] == '\\' && j + 1 < n && (line[j + 1] == '"' || line[j + 1] == '\\'))
                    {
                        sb.Append(line[j + 1]);
                        j += 2;
                        continue;
                    }
                    sb.Append(line[j]);
                    j++;
                }
                tokens.Add(sb.ToString());
                i = j + 1;
            }
            else if (line[i] == '{' || line[i] == '}')
            {
                tokens.Add(line[i].ToString());
                i++;
            }
            else
            {
                var j = i;
                while (j < n && !char.IsWhiteSpace(line[j]) && line[j] != '"')
                    j++;
                tokens.Add(line[i..j]);
                i = j;
            }
        }
    }

    /// <summary>根块内的全部顶层 "key" "value" 对（保持出现顺序）；带子块的键跳过</summary>
    static List<(string Key, string Value)> ParseEntries(List<string> tokens)
    {
        var entries = new List<(string, string)>();
        var open = tokens.IndexOf("{");
        if (open < 0)
            return entries;
        var i = open + 1;
        while (i < tokens.Count && tokens[i] != "}")
        {
            if (tokens[i] == "{" || i + 1 >= tokens.Count)
            {
                i++;
                continue;
            }
            if (tokens[i + 1] == "{")
            {
                i = SkipBlock(tokens, i + 1);
                continue;
            }
            if (tokens[i + 1] != "}")
            {
                entries.Add((tokens[i], tokens[i + 1]));
                i += 2;
                continue;
            }
            i++;
        }
        return entries;
    }

    static Dictionary<string, string> ParsePairs(List<string> tokens)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in ParseEntries(tokens))
            map[k] = v;
        return map;
    }

    /// <summary>tokens[index] 为 '{'，返回配对 '}' 之后的下标</summary>
    static int SkipBlock(List<string> tokens, int index)
    {
        var depth = 0;
        for (var i = index; i < tokens.Count; i++)
        {
            if (tokens[i] == "{")
                depth++;
            else if (tokens[i] == "}" && --depth == 0)
                return i + 1;
        }
        return tokens.Count;
    }

    static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
