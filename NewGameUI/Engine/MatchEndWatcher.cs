namespace Plugin.Engine;

/// <summary>
/// 对局结束监视（业务判定，跑在 FramePump 上）：对局已不在（断开连接/被踢出等）
/// 时调 <see cref="GameMode.NotifyMatchEnded"/>，菜单回到初始形态。
/// 判据是关卡名持续为空：地图重载（换图/入队重开）期间关卡名会短暂为空，
/// 必须持续为空才判定，否则重载瞬间就把"进入过对局"清掉，
/// 回游戏/暂停菜单随之失效。
/// </summary>
public static class MatchEndWatcher
{
    static int s_levelEmptySince;

    public static void Initialize() => EngineHooks.FramePump += OnFrame;

    static void OnFrame()
    {
        try
        {
            if (!GameMode.EnteredGame)
                return;
            var level = EngineCommands.GetLevelName();
            // 加载期间关卡名本来就没有，不算"对局结束"信号
            if (level is null || level.Length != 0 || EngineBaseUI.LoadingProgress.Active)
            {
                s_levelEmptySince = 0;
                return;
            }
            if (s_levelEmptySince == 0)
            {
                s_levelEmptySince = Environment.TickCount;
                return;
            }
            if (unchecked(Environment.TickCount - s_levelEmptySince) < 1500)
                return;
            s_levelEmptySince = 0;
            Log.Write("MatchEndWatcher: 关卡名持续为空，判定对局结束");
            GameMode.NotifyMatchEnded();
        }
        catch (Exception ex)
        {
            Log.Write($"MatchEndWatcher: {ex.Message}");
        }
    }
}
