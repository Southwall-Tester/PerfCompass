using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunHistoryQualityUiTests(string output, Action<bool, string> require, Func<string, Task> screenshot, Func<Func<bool>, Task> wait)
    {
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (!paused) Click(PauseButton);
        Click(SettingsNav); FutureBox.Text = ""; RequirementsConfirm.IsChecked = false; RepresentativeCheck.IsChecked = true;
        Click(SaveRequirementsButton); await wait(() => report!.Ready && !report.HistoryQuality.HasIssues);
        Click(ComparisonNav); await wait(() => ComparisonPage.Visibility == Visibility.Visible);
        Click(ComparisonPage.ImportCriteriaButton); ComparisonPage.CoverageConfirm.IsChecked = true;
        File.WriteAllText(Path.Combine(output, "history-setup-diagnostics.json"), JsonSerializer.Serialize(new
        { Expected = CandidateComparison.Stamp(report!), Actual = ComparisonPage.Workspace.ContextStamp, Checked = ComparisonPage.CoverageConfirm.IsChecked,
            Status = ComparisonPage.ComparisonStatus.Text, Context = ComparisonPage.ContextStatus.Text, Count = ComparisonPage.Workspace.Criteria.Count }));
        require(ComparisonPage.Workspace.ContextStamp == CandidateComparison.Stamp(report!) && ComparisonPage.CoverageConfirm.IsChecked == true && ComparisonPage.ContextStatus.Text.Contains("一致"),
            "Candidate coverage is explicitly checked against the current clean history context before fault injection");
        var clean = report!;
        using (var diagnostics = new HistoryDiagnostics(60))
        {
            diagnostics.Add("synthetic-context-check", 1, "synthetic new anomaly");
            ComparisonPage.SetContext(store, clean with { HistoryQuality = diagnostics.Build() });
            require(ComparisonPage.CoverageConfirm.IsChecked == false,
                "A synchronous context change revokes unsaved coverage before any navigation or binding refresh");
        }
        ComparisonPage.SetContext(store, clean); ComparisonPage.CoverageConfirm.IsChecked = true;
        string path = Directory.GetFiles(store.Root, "samples-*.jsonl").Order(StringComparer.Ordinal).Last(); byte[] original = File.ReadAllBytes(path);
        try
        {
            File.AppendAllText(path, "{synthetic damaged history tail"); await RefreshAnalysis(); await wait(() => report!.HistoryQuality.HasIssues);
            require(!report!.Ready && report.HistoryQuality.IssueCount == 1 && report.Cards.All(c => c.Status is not ("surplus" or "review" or "changed"))
                && report.DemandTrends.All(t => t.Status == "insufficient"), "Native analysis withdraws sizing and period conclusions after a real malformed history tail");
            Click(AdviceNav); HistoryQualityNotice.IsExpanded = true;
            AdvicePage.BringIntoView(new Rect(0, 0, AdvicePage.ActualWidth, 260));
            require(HistoryQualityNotice.Visibility == Visibility.Visible && HistoryQualityDetails.Text.Contains(Path.GetFileName(path)) && HistoryQualityDetails.Text.Contains("行"),
                "Global history notice exposes the affected file and line through native navigation");
            await screenshot("32-history-quality-warning");
            string html = ReportExporter.Html(report, live); File.WriteAllText(Path.Combine(output, "history-quality-report.html"), html);
            require(html.Contains("历史记录异常") && html.Contains(Path.GetFileName(path)) && html.Contains("暂不判断配置余量"),
                "Exported HTML preserves history diagnostics and qualification of configuration advice");
            Click(ComparisonNav); await wait(() => ComparisonPage.Visibility == Visibility.Visible);
            require(ComparisonPage.CoverageConfirm.IsChecked == false && ComparisonPage.ContextStatus.Text.Contains("历史读取有异常"),
                "History changes revoke previously checked candidate coverage even without a calculated result");
        }
        finally { File.WriteAllBytes(path, original); }
        await RefreshAnalysis(); await wait(() => !report!.HistoryQuality.HasIssues && report.Ready);
        require(HistoryQualityNotice.Visibility == Visibility.Collapsed && report!.Ready,
            "Restoring exact isolated fixture bytes clears diagnostics and recalculates evidence");
        require(ComparisonPage.CoverageConfirm.IsChecked == false, "Resolved history diagnostics do not silently restore old candidate coverage confirmation");
        Click(AdviceNav); AdvicePage.BringIntoView(new Rect(0, 0, AdvicePage.ActualWidth, 260)); await screenshot("33-history-quality-restored");
        var baseline = report!;
        Click(ComparisonNav); await wait(() => ComparisonPage.Visibility == Visibility.Visible);
        Click(ComparisonPage.ImportCriteriaButton); ComparisonPage.UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        ComparisonPage.CoverageConfirm.IsChecked = true;
        string copiedLine = File.ReadLines(path).First(line => JsonSerializer.Deserialize<Sample>(line) is { Active: true, Seconds: > 0 });
        try
        {
            File.AppendAllText(path, copiedLine + "\n"); await RefreshAnalysis(); await wait(() => report!.HistoryQuality.DuplicateCount == 1);
            File.WriteAllText(Path.Combine(output, "duplicate-context-diagnostics.json"), JsonSerializer.Serialize(new
            { Baseline = CandidateComparison.Stamp(baseline), Current = CandidateComparison.Stamp(report!), Workspace = ComparisonPage.Workspace.ContextStamp,
                Checked = ComparisonPage.CoverageConfirm.IsChecked, Status = ComparisonPage.ContextStatus.Text }));
            require(report!.Ready && report.ActiveHours == baseline.ActiveHours && report.SampleCount == baseline.SampleCount && !report.HistoryQuality.HasIssues,
                "Native history analysis excludes an exact copied row without inflating valid time or withdrawing genuine sufficient evidence");
            require(ComparisonPage.CoverageConfirm.IsChecked == true && CandidateComparison.Stamp(report) == CandidateComparison.Stamp(baseline),
                "An excluded copy preserves explicitly confirmed candidate coverage for unchanged effective evidence");
            Click(AdviceNav); HistoryQualityNotice.IsExpanded = true;
            AdvicePage.BringIntoView(new Rect(0, 0, AdvicePage.ActualWidth, 260));
            require(HistoryQualityNotice.Visibility == Visibility.Visible && HistoryQualityDetails.Text.Contains("相同记录已排除"),
                "Native duplicate notice explains that copied records count once while originals remain intact");
            await screenshot("34-history-duplicate");
            var overlapping = JsonSerializer.Deserialize<Sample>(copiedLine)! with { Cpu = 99, Gpu = 99 };
            File.AppendAllText(path, JsonSerializer.Serialize(overlapping) + "\n"); await RefreshAnalysis(); await wait(() => report!.HistoryQuality.HasIntervalConflicts);
            require(!report!.Ready && Math.Abs(report.ActiveHours - baseline.ActiveHours) < .0000001 && report.Cards.All(c => c.Status == "unknown") && report.History.Count == 0
                && report.DemandTrends.All(t => !t.LoadStatisticsAvailable && t.RecentP95 == null) && SceneProfileSummary.Text.Contains("不能相加"),
                "Native overlapping records retain unique time and withdraw ambiguous pressure statistics and plot");
            require(ComparisonPage.CoverageConfirm.IsChecked == false, "An overlapping interval revokes old candidate coverage through production refresh");
            Click(AdviceNav); HistoryQualityNotice.IsExpanded = true;
            AdvicePage.BringIntoView(new Rect(0, 0, AdvicePage.ActualWidth, 260)); await screenshot("35-history-overlap");
            string html = ReportExporter.Html(report, live); File.WriteAllText(Path.Combine(output, "history-overlap-report.html"), html);
            require(html.Contains("时间片重叠") && html.Contains("重叠时段的负载比例无法确认") && html.Contains("不能相加"),
                "Exported overlap report explains unknown load shares and nonadditive scene durations");
        }
        finally { File.WriteAllBytes(path, original); }
        await RefreshAnalysis(); await wait(() => report!.Ready && !report.HistoryQuality.HasNotices);
        require(HistoryQualityNotice.Visibility == Visibility.Collapsed && ComparisonPage.CoverageConfirm.IsChecked == false && File.ReadAllBytes(path).SequenceEqual(original),
            "Restoring exact isolated history clears duplicate and overlap notices without restoring revoked coverage");
        Click(AdviceNav); AdvicePage.BringIntoView(new Rect(0, 0, AdvicePage.ActualWidth, 260)); await screenshot("36-history-interval-restored");
        File.WriteAllText(Path.Combine(output, "history-quality-notice.txt"), "Synthetic malformed tail appended to an isolated UI-test history file while recording is paused. Exact captured fixture bytes restored in finally; no user file or production repair feature is used. Native navigation, analysis, coverage state and HTML export are exercised.");
    }
}
