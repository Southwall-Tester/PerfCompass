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
        string path = Path.Combine(store.Root, $"samples-{DateTime.Today:yyyy-MM-dd}.jsonl"); byte[] original = File.ReadAllBytes(path);
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
        File.WriteAllText(Path.Combine(output, "history-quality-notice.txt"), "Synthetic malformed tail appended to an isolated UI-test history file while recording is paused. Exact captured fixture bytes restored in finally; no user file or production repair feature is used. Native navigation, analysis, coverage state and HTML export are exercised.");
    }
}
