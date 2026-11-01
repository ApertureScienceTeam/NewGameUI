using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GoldSrc.Avalonia;
using Plugin;
using Plugin.Engine;
using Plugin.Views;

namespace NewGameUI;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime)
        {
            // 业务服务：订阅引擎钩子事件（预览进程没有引擎事件，保持空转）
            ConsoleService.Initialize();     // 控制台缓冲/历史/提交/condump
            GameMenuReplacer.Initialize();   // 原版 GameUI 页面替换 + 加载让位
            MatchEndWatcher.Initialize();    // 对局结束判定
            UiWindows.Initialize();          // 菜单/控制台/暂停/加载窗口的显隐编排

            // 没有"父窗口"容器：页面窗口都是各自独立的 Avalonia Window。
            // 注册只含主菜单：新建游戏 / 游戏选项 由主菜单按需打开（PageWindows）
            // 游戏里由 UiWindows 在菜单首次可见、vgui2 就绪后创建；
            // 预览没有引擎钩子，注册完直接显示。
            UiWindows.SetMenuWindows(
                // 全屏背景主菜单：位置/尺寸由 MainMenuWindow 构造函数自定（铺满游戏屏幕）
                () => new MainMenuWindow());

            // 关卡加载覆盖窗：替代原版 GameUI 进度条，加载开始时创建并显示
            UiWindows.SetLoadingWindow(() => new LoadingWindow());

            // 控制台：替代 GameUI 的 CConsoleDialog，显隐由引擎 IGameConsole 钩子驱动
            UiWindows.SetConsoleWindow(() => new ConsoleWindow());

#if !METAHOOK_PLUGINS
            UiWindows.ShowMenuWindows();
#endif
        }
        base.OnFrameworkInitializationCompleted();
    }
}
