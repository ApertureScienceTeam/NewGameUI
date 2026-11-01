using System.IO;

namespace Plugin;

// 临时诊断日志，排查输入链路用，验证后删除
public static class Log
{
    static readonly object _lock = new();
    const string Path = @"D:\mh_plugin.log";

    public static void Write(string s)
    {
        try
        {
            lock (_lock)
                File.AppendAllText(Path, $"{System.DateTime.Now:HH:mm:ss.fff} {s}\n");
        }
        catch
        {
        }
    }
}
