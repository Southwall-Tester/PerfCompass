using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunSamplingClockUiTests(string output, Action<bool, string> require, Func<string, Task> screenshot, Func<Func<bool>, Task> wait)
    {
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var original = collector!.TimingReadOverride; var healthy = live!; DateTimeOffset interruptedAt;
        try
        {
            // Only this Collector's time input changes. The operating-system clock is untouched.
            collector.TimingReadOverride = () => { var instant = SamplingInstant.Now(); return instant with { Timestamp = instant.Timestamp + Stopwatch.Frequency * 5 }; };
            await wait(() => live!.Sample.Time > healthy.Sample.Time && live.Sample.SamplingIssue == "clock-change");
            interruptedAt = live!.Sample.Time;
            require(live.Sample.Seconds == 0 && live.Sample.Cpu == null && live.Sample.CoreMax == null && live.Sample.MemoryTotalGb > 0,
                "Actual collector rejects elapsed/calendar disagreement without losing the instantaneous memory reading");
            require(SamplingTimingBanner.Visibility == Visibility.Visible && SamplingTimingBanner.Text.Contains("经过时间不一致")
                && RecordState.Text.Contains("采样时间中断") && experienceContext.All(s => s.Seconds == 0) && livePoints.Count == 1,
                "Native timing notice clears earlier experience and live-plot context at an interruption");
            require(store.Read(7, collector.HardwareId).Any(s => s.Time == interruptedAt && s.Seconds == 0 && s.SamplingIssue == "clock-change"),
                "Production collection loop immediately persists the zero-duration interruption separately from measured batches");
            await screenshot("37-sampling-clock-interrupted");
        }
        finally { collector.TimingReadOverride = original; }
        var interrupted = live!.Sample.Time;
        await wait(() => live!.Sample.Time > interrupted && live.Sample.SamplingIssue == "" && live.Sample.Seconds > 0);
        require(SamplingTimingBanner.Visibility == Visibility.Collapsed && live!.Sample.Cpu.HasValue && live.Sample.Seconds <= 15,
            "Real collector recovers after rebuilding the timing and CPU baselines");
        Click(HistoryNav); await wait(() => HistoryPage.Visibility == Visibility.Visible && report!.SamplingGapCount > 0);
        require(SamplingGapLabel.Visibility == Visibility.Visible && SamplingGapLabel.Text.Contains("未计入有效使用"),
            "Historical analysis exposes saved sampling interruptions through native navigation");
        string html = ReportExporter.Html(report!, live); File.WriteAllText(Path.Combine(output, "sampling-clock-report.html"), html);
        require(html.Contains("采样中断") && html.Contains("未作为空闲负载"), "Exported report preserves the distinction between missed sampling and idle work");
        await screenshot("38-sampling-clock-history");
        Click(LiveNav);
        File.WriteAllText(Path.Combine(output, "sampling-clock-notice.txt"), "Synthetic divergence is injected into this real Collector's high-resolution timestamp input on isolated UI-test data. The actual Windows clock, sleep state and power settings are never changed. Production storage, timing validation, native notice, context reset, recovery and HTML are exercised. This is not a physical sleep/resume or Windows time-service correction test.");
    }
}
