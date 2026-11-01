using Avalonia;
using GoldSrc.Avalonia;
using NewGameUI;

namespace Plugin.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
#if !METAHOOK_PLUGINS
        // 预览进程没有插件宿主，接入层日志直接打到 stdout 便于排查
        GoldSrcAvalonia.Log = s => Console.WriteLine(s);
#endif
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}