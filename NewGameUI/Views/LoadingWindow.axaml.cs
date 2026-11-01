using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

using GoldSrc.Avalonia;
using Plugin.Engine;

namespace Plugin.Views;

/// <summary>
/// 关卡加载/资源下载覆盖窗（替代原版 GameUI 的 CLoadingDialog）。
///
/// 显示/隐藏由接入层的 GoldSrcWindows 管（加载开始显示、结束收起），
/// 内容直接订阅 <see cref="EngineBaseUI.LoadingProgressChanged"/> 刷新：
/// 关卡加载的进度值来自引擎 IGameUI::ContinueProgressBar，下载资源的进度
/// 来自槽 18（SetSecondaryProgress）、当前文件名来自槽 19（SetSubStatusText），
/// 状态文本来自 SetProgressBarStatusText，与原版进度条完全同源。
///
/// 进度条是纯 Avalonia 自绘（Panel + 两个 Border），填充宽度按轨道实际宽度
/// 等比换算，窗口尺寸变化时重算
/// </summary>
public partial class LoadingWindow : Window
{
	const int FallbackW = 1280;
	const int FallbackH = 720;

	double _fraction;
	double _secondaryFraction;

	/// <summary>轨道左右各留 2px 内边距，填充宽度按这个可用宽度换算</summary>
	const double BarInset = 4;

	public LoadingWindow()
	{
		InitializeComponent();

		var (w, h) = GoldSrcAvalonia.ScreenSize;
		if (w <= 0 || h <= 0)
			(w, h) = (FallbackW, FallbackH);
		Width = w;
		Height = h;
		Position = new PixelPoint(0, 0);

		// 标题文案走引擎本地化表（GameUI_Loading = "正在载入…"），查不到用中文兜底
		LoadingLabel.Text = EngineLocalize.FindOrDefault("GameUI_Loading", "正在载入");

		EngineBaseUI.LoadingProgressChanged += OnProgressChanged;
		Closed += (_, _) => EngineBaseUI.LoadingProgressChanged -= OnProgressChanged;
		TrackBar.SizeChanged += (_, _) => UpdateFillWidth();
		SecondaryTrackBar.SizeChanged += (_, _) => UpdateFillWidth();

		Apply(EngineBaseUI.LoadingProgress);
	}

	/// <summary>引擎线程回调：切到 UI 线程再动控件</summary>
	void OnProgressChanged(LoadingProgressInfo info)
	{
		if (!Dispatcher.UIThread.CheckAccess())
		{
			Dispatcher.UIThread.Post(() => Apply(info));
			return;
		}
		Apply(info);
	}

	void Apply(LoadingProgressInfo info)
	{
		_fraction = Math.Clamp(info.Fraction, 0, 1);
		_secondaryFraction = Math.Clamp(info.SecondaryFraction, 0, 1);

		var title = string.IsNullOrEmpty(info.Title) ? "游戏资源" : info.Title;
		TitleText.Text = title;

		// Status 现在是引擎原文（未翻译），先走本地化表；引擎没给状态文本时，
		// 用本地化键 GameUI_LoadingFilename（"正载入 %s1 ..."）兜底，
		// 这样进度条上的文字始终是当前语言
		var status = info.Status;
		if (!string.IsNullOrEmpty(status))
			status = EngineLocalize.Translate(status, title);
		if (string.IsNullOrEmpty(status) && !info.Error)
			status = EngineLocalize.FindFormat("GameUI_LoadingFilename", title, $"正载入 {title} ...");

		StatusText.Text = info.Error
			? (string.IsNullOrEmpty(info.Status) ? "载入失败" : $"载入失败：{info.Status}")
			: status;

		// 下载区块（副进度条 + 当前文件）：只在下载期间出现（同原版切双条布局）
		SecondaryBlock.IsVisible = info.SecondaryActive;
		var sub = string.IsNullOrEmpty(info.SubStatus) ? "" : EngineLocalize.Translate(info.SubStatus, title);
		SubStatusText.Text = sub;

		PercentText.Text = $"{_fraction * 100:0}%";
		PercentText.Foreground = info.Error
			? new SolidColorBrush(Color.Parse("#E8746E"))
			: (IBrush?)Resources["AccentBrush"] ?? Brushes.LightSkyBlue;
		FillBar.Background = info.Error
			? (IBrush?)Resources["BarFillErrorBrush"]
			: (IBrush?)Resources["BarFillBrush"];

		UpdateFillWidth();
	}

	void UpdateFillWidth()
	{
		var available = Math.Max(0, TrackBar.Bounds.Width - BarInset);
		FillBar.Width = available * _fraction;

		var secondaryAvailable = Math.Max(0, SecondaryTrackBar.Bounds.Width - BarInset);
		SecondaryFillBar.Width = secondaryAvailable * _secondaryFraction;
	}
}
