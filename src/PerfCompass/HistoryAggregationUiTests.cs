using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunHistoryAggregationUiTests(string output, Action<bool, string> require,
        Func<string, Task> screenshot, Func<Func<bool>, Task> waitForAnalysis)
    {
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        bool Near(double? actual, double expected) => actual.HasValue && Math.Abs(actual.Value - expected) < 1e-7;
        bool InViewport(FrameworkElement element)
        {
            if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
            var surface = (FrameworkElement)Content;
            var bounds = element.TransformToAncestor(surface).TransformBounds(new Rect(element.RenderSize));
            var visible = bounds;
            visible.Intersect(new Rect(surface.RenderSize));
            for (var ancestor = VisualTreeHelper.GetParent(element); ancestor != null && ancestor != surface;
                ancestor = VisualTreeHelper.GetParent(ancestor))
                if (ancestor is FrameworkElement frame && (frame.ClipToBounds || frame is ScrollContentPresenter))
                    visible.Intersect(frame.TransformToAncestor(surface).TransformBounds(new Rect(frame.RenderSize)));
            return !visible.IsEmpty && visible.Width > 8 && visible.Height > 8
                && visible.Width >= bounds.Width - .5 && visible.Height >= bounds.Height - .5;
        }
        bool HasMetricDrawing(Drawing? drawing)
        {
            bool MetricBrush(Brush? brush) => brush is SolidColorBrush solid
                && (solid.Color == Color.FromRgb(0, 127, 115) || solid.Color == Color.FromRgb(99, 120, 200)
                    || solid.Color == Color.FromRgb(195, 140, 55));
            return drawing switch
            {
                DrawingGroup group => group.Children.Any(HasMetricDrawing),
                GeometryDrawing geometry => MetricBrush(geometry.Brush) || MetricBrush(geometry.Pen?.Brush),
                _ => false
            };
        }
        async Task<bool> CaptureHistory(string name)
        {
            UpdateLayout();
            // The chart and details share this stack. Reveal their whole region,
            // then align its top in the physical page scroller, never a 1px point.
            var panel = (FrameworkElement)VisualTreeHelper.GetParent(HistoryChart);
            double top = HistoryChart.TransformToAncestor(panel).Transform(new Point()).Y;
            double bottom = HistoryPeriodDetails.TransformToAncestor(panel)
                .TransformBounds(new Rect(HistoryPeriodDetails.RenderSize)).Bottom;
            panel.BringIntoView(new Rect(0, Math.Max(0, top - 8), panel.ActualWidth, bottom - top + 16));
            for (var ancestor = VisualTreeHelper.GetParent(panel); ancestor != null;
                ancestor = VisualTreeHelper.GetParent(ancestor))
                if (ancestor is ScrollViewer scroll)
                {
                    if (!scroll.CanContentScroll && scroll.Content is Visual content)
                        scroll.ScrollToVerticalOffset(Math.Max(0,
                            HistoryChart.TransformToAncestor(content).Transform(new Point()).Y - 12));
                    break;
                }
            await Task.Delay(100); UpdateLayout();
            var elements = new FrameworkElement[] { HistoryChart, HistoryPeriodCombo, HistoryPeriodDetails };
            bool visible = elements.All(InViewport);
            await screenshot(visible ? name : name + "-visibility-failure");
            if (!visible)
            {
                var surface = (FrameworkElement)Content;
                File.WriteAllText(Path.Combine(output, name + "-visibility-failure.json"), JsonSerializer.Serialize(new
                {
                    Width, Height, SelectedScene = report?.SelectedScene,
                    SelectedPeriod = (HistoryPeriodCombo.SelectedItem as HistoryPoint)?.PeriodLabel,
                    Details = HistoryPeriodDetails.Text,
                    Elements = elements.Select(element => new
                    {
                        element.Name, element.IsVisible, element.ActualWidth, element.ActualHeight,
                        FullyInViewport = InViewport(element),
                        Bounds = element.TransformToAncestor(surface).TransformBounds(new Rect(element.RenderSize)).ToString()
                    }).ToArray()
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            return visible;
        }
        async Task SelectScene(string name)
        {
            Click(AdviceNav);
            AnalysisSceneCombo.SelectedItem = AnalysisSceneCombo.Items.OfType<ComboBoxItem>()
                .Single(item => item.Tag as string == name);
            await waitForAnalysis(() => report!.SelectedScene == name);
        }

        if (!paused) Click(PauseButton);
        require(paused && collector != null && RecordState.Text.Contains("已暂停")
            && Environment.GetCommandLineArgs().Contains("--ui-test"),
            "History aggregation fixture pauses recording inside the guarded isolated UI-test session");
        const string scene = "小时加权校验（合成任务）";
        const string emptyScene = "无有效读数校验（合成零时长）";
        var localHour = DateTime.Today.AddDays(-1).AddHours(13);
        var firstHour = new DateTimeOffset(localHour, TimeZoneInfo.Local.GetUtcOffset(localHour));
        var beginning = firstHour.AddMinutes(17);
        Sample Frame(DateTimeOffset time, double seconds, double load, double coverage) => new()
        {
            Time = time, HardwareId = collector!.HardwareId, Scene = scene, Seconds = seconds, Active = true,
            ObservationVersion = ActivityRules.Version,
            Cpu = load, CoreMax = load, CpuCoverage = coverage, CoreCoverage = coverage,
            MemoryTotalGb = 32, MemoryUsedGb = 32 * load / 100, MemoryPeakGb = 32 * load / 100,
            MemoryCoverage = coverage, CommitCoverage = 0,
            Gpu = load, GpuPeak = load, GpuCoverage = coverage, GpuMetricSource = "GPU Core",
            GpuEngineVersion = GpuEngineEvidence.Version, GpuEngines = []
        };
        var rows = new[]
        {
            Frame(beginning.AddSeconds(1), 1, 90, 1),
            Frame(beginning.AddSeconds(10), 9, 10, 9),
            // This record covers :59:55 through :00:05. Its four known GPU
            // seconds have no saved positions, so each hour receives an explicit
            // two-second estimate. Numeric CPU/memory values with zero coverage
            // must not contribute to either hour.
            Frame(firstHour.AddHours(1).AddSeconds(5), 10, 100, 0) with
            { Gpu = 60, GpuPeak = 60, GpuCoverage = 4 },
            new Sample
            {
                Time = firstHour.AddHours(2).AddMinutes(17), HardwareId = collector!.HardwareId,
                Scene = emptyScene, Seconds = 0, Active = false, ObservationVersion = ActivityRules.Version,
                GpuEngineVersion = GpuEngineEvidence.Version, GpuEngines = []
            }
        };
        File.WriteAllText(Path.Combine(output, "history-aggregation-fixture.json"),
            JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "history-aggregation-NOTICE.txt"),
            "The common startup checks use actual local CPU/memory collection. Recording is then paused. All following history rows are synthetic and saved only to the guarded independent UI-test data directory. A dedicated selected scene excludes startup telemetry. Yesterday's 1-second 90% and 9-second 10% readings should produce 18% over 10 observed seconds, not 50%. A separate 10-second record crossing an hour has zero CPU/memory coverage and four known GPU seconds; its GPU coverage is proportionally allocated as two seconds per hour and explicitly marked estimated. A separate zero-duration scene has no historical means. This tests store/history analysis, hour selection, rendering and HTML consistency; it is not a real workload, long-term test or physical sensor accuracy measurement.");
        await Task.Run(() => { foreach (var row in rows) store.Append(row); });
        Click(AdviceNav); await RefreshAnalysis();
        await waitForAnalysis(() => report!.SceneProfiles.Any(profile => profile.Name == scene)
            && report.SceneProfiles.Any(profile => profile.Name == emptyScene));
        await SelectScene(scene);
        var scoped = report!;
        require(scoped.SampleCount == 3 && scoped.History.Count == 2 && !scoped.Ready
            && !scoped.HistoryQuality.HasIssues && !scoped.HistoryQuality.HasIntervalConflicts
            && scoped.History[0].Time == firstHour && scoped.History[1].Time == firstHour.AddHours(1),
            "Production history analysis isolates three synthetic records into their two actual hour intervals");
        var first = scoped.History[0]; var second = scoped.History[1];
        require(Near(first.Cpu, 18) && Near(first.Memory, 18) && Near(first.Gpu, 25)
            && Near(first.CpuObservedSeconds, 10) && Near(first.MemoryObservedSeconds, 10)
            && Near(first.GpuObservedSeconds, 12) && !first.CpuCoverageEstimated && !first.MemoryCoverageEstimated,
            "Hour means use per-resource observed seconds and exclude numeric CPU or memory readings with zero coverage");
        require(first.GpuCoverageEstimated && second.GpuCoverageEstimated && Near(second.Gpu, 60)
            && Near(second.GpuObservedSeconds, 2) && second.Cpu == null && second.Memory == null
            && Near(second.CpuObservedSeconds, 0) && Near(second.MemoryObservedSeconds, 0)
            && first.GpuReadingLabel.Contains("部分读数") && second.GpuReadingLabel.Contains("部分读数")
            && !first.CpuReadingLabel.Contains("部分读数") && !first.MemoryReadingLabel.Contains("部分读数"),
            "Cross-hour partial GPU coverage is visibly estimated while absent CPU and memory remain unknown");

        Click(HistoryNav); HistoryPeriodCombo.SelectedItem = first;
        await RefreshAnalysis(); await waitForAnalysis(() => report!.SelectedScene == scene && report.History.Count == 2);
        bool firstVisible = await CaptureHistory("58-history-weighted-first-hour");
        require(HistoryPage.Visibility == Visibility.Visible && HistorySummary.Text.Contains(scene)
            && HistoryPeriodCombo.IsEnabled && HistoryPeriodCombo.DisplayMemberPath == nameof(HistoryPoint.PeriodLabel)
            && HistoryPeriodCombo.Items.OfType<HistoryPoint>().Select(point => point.Time)
                .SequenceEqual(new[] { second.Time, first.Time })
            && HistoryPeriodCombo.SelectedItem is HistoryPoint selectedFirst && selectedFirst.Time == first.Time
            && HistoryPeriodDetails.Text == first.DetailsLabel && firstVisible
            && HasMetricDrawing(VisualTreeHelper.GetDrawing(HistoryChart)),
            "Real hour selection survives refresh and renders the complete weighted readings with their chart");
        HistoryPeriodCombo.SelectedItem = HistoryPeriodCombo.Items.OfType<HistoryPoint>()
            .Single(point => point.Time == second.Time);
        bool secondVisible = await CaptureHistory("59-history-partial-second-hour");
        require(HistoryPeriodDetails.Text == second.DetailsLabel && secondVisible
            && HistoryPeriodDetails.Text.Contains("无可用均值") && !HistoryPeriodDetails.Text.Contains(first.CpuReadingLabel),
            "Selecting the next hour replaces all visible resource details including missing readings and partial coverage");

        string html = ReportExporter.Html(report!, live);
        File.WriteAllText(Path.Combine(output, "history-aggregation-report.html"), html);
        File.WriteAllText(Path.Combine(output, "history-aggregation-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        require(html.Contains("历史小时均值与观测时长") && new[] { first, second }.All(point =>
            new[] { point.PeriodLabel, point.CpuReadingLabel, point.MemoryReadingLabel, point.GpuReadingLabel }
                .All(label => html.Contains(WebUtility.HtmlEncode(label)))),
            "HTML exports the same two hour labels weighted means observed durations and partial-coverage markers as the native view");

        await SelectScene(emptyScene); Click(HistoryNav); UpdateLayout();
        require(report!.SampleCount == 1 && report.History.Count == 0 && !report.HistoryQuality.HasIssues
            && !HistoryPeriodCombo.IsEnabled && HistoryPeriodCombo.Items.Count == 0
            && HistoryPeriodCombo.SelectedItem == null && HistorySummary.Text.Contains(emptyScene)
            && HistoryPeriodDetails.Text == "当前范围没有可显示的小时均值"
            && !HistoryPeriodDetails.Text.Contains(second.GpuReadingLabel),
            "Switching to the zero-duration scene disables hour selection and clears the previous resource details");
        bool emptyVisible = await CaptureHistory("60-history-empty-scope");
        string emptyHtml = ReportExporter.Html(report!, live);
        File.WriteAllText(Path.Combine(output, "history-aggregation-empty-report.html"), emptyHtml);
        require(emptyVisible && VisualTreeHelper.GetDrawing(HistoryChart) != null
            && !HasMetricDrawing(VisualTreeHelper.GetDrawing(HistoryChart))
            && !emptyHtml.Contains("历史小时均值与观测时长") && !emptyHtml.Contains(WebUtility.HtmlEncode(first.PeriodLabel)),
            "The rendered empty chart and exported report contain no old hour curves or details");
    }
}
