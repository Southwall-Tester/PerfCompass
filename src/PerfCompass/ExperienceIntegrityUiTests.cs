using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunExperienceIntegrityUiTests(string output, Action<bool, string> require, Func<string, Task> screenshot, Func<Func<bool>, Task> wait)
    {
        void Click(Button b) { b.Focus(); b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
        if (!paused) Click(PauseButton);
        var now = DateTimeOffset.Now.AddMinutes(-1); string hardware = collector!.HardwareId;
        const string scene = "体验完整性合成任务";
        for (int i = 0; i < 30; i++) store.Append(new Sample { Time = now.AddSeconds(-i * 10), Seconds = 10, Active = true, HardwareId = hardware, Scene = scene,
            ObservationVersion = ActivityRules.Version, Cpu = 5, CoreMax = 20, MemoryTotalGb = 64, MemoryUsedGb = 8, MemoryPeakGb = 8, CommitGb = 10, Gpu = 3, GpuPeak = 3, GpuMetricSource = "GPU Core" });
        var value = ExperienceAnalyzer.Create(now, hardware, scene, "任务等待过久", "合成体验：等待很久 <review>", []);
        store.AppendExperience(value); string path = Directory.GetFiles(store.Root, "experience-*.jsonl").Single(); byte[] original = File.ReadAllBytes(path);
        Click(AdviceNav); await RefreshAnalysis();
        AnalysisSceneCombo.SelectedItem = AnalysisSceneCombo.Items.OfType<ComboBoxItem>().Single(c => c.Tag as string == scene);
        await wait(() => report?.SelectedScene == scene);
        require(!report!.ExperienceQuality.HasIssues && report.ExperienceEvents.Count == 1 && PurchaseGuidance.Build(report).Single(d => d.Key == "gpu").Direction.Contains("体验问题"), "A valid synthetic pending experience reaches actual purchasing guidance");
        CandidateComparison.Save(store.Root, new() { ContextStamp = CandidateComparison.Stamp(report), Candidates = [new() { Name = "合成内存候选", MemoryGb = 32, Source = "合成规格，非商品验证" }], Criteria = [new() { Name = "手工核对内存条件", Metric = "memory", Target = 16, Unit = "GB", Reviewed = true }] });
        Click(ComparisonNav); var view = ComparisonPage; view.CoverageConfirm.IsChecked = true; Click(view.CompareButton);
        require(view.Result?.Candidates.Single().State == "pass", "An independently checked candidate target is valid before experience corruption");
        File.WriteAllText(path, "{damaged"); await RefreshAnalysis();
        require(report!.ExperienceQuality.HasIssues && report.ExperienceEvents.Count == 0 && !report.Ready, "The production reader diagnoses a damaged experience instead of claiming a clean empty history");
        require(view.Result == null && view.CoverageConfirm.IsChecked == false, "Experience read damage revokes the previous candidate scope and coverage confirmation");
        require(report.Cards.Single(c => c.Key == "gpu").Observation?.State == "light" && report.History.Count > 0, "Independent low telemetry and history remain visible as observations");
        require(!PurchaseGuidance.Build(report).Any(d => d.Direction.Contains("较低档")), "Damaged experience history cannot silently restore a downgrade direction");
        Click(AdviceNav); await RefreshAnalysis(); ExperienceQualityNotice.IsExpanded = true;
        require(ExperienceQualityNotice.Visibility == Visibility.Visible && ExperienceQualityDetails.Text.Contains(Path.GetFileName(path)) && ExperienceQualityDetails.Text.Contains("第 1 行"), "The actual analysis page names the damaged file and line");
        await screenshot("experience-damaged-analysis");
        string exported = Path.Combine(output, "experience-report-" + Guid.NewGuid().ToString("N") + ".html");
        var picker = ReportExportPathForTest;
        try { ReportExportPathForTest = () => (exported, false); Click(ExportAnalysisButton); await ReportExportCompletion.WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { ReportExportPathForTest = picker; }
        require(File.ReadAllText(exported).Contains("体验记录读取异常") && File.ReadAllText(exported).Contains(Path.GetFileName(path)), "Actual HTML report export preserves experience diagnostics");
        Click(HistoryNav); await RefreshAnalysis();
        require(ExperienceHistoryQualityNotice.Visibility == Visibility.Visible && ExperienceHistoryQualityNotice.Text.Contains("第 1 行"), "The actual experience history page exposes the same read failure");
        await screenshot("experience-damaged-history");
        require(File.ReadAllText(path) == "{damaged", "Viewing analysis history and exporting leaves the damaged original unchanged");
        Click(ComparisonNav); view.CoverageConfirm.IsChecked = true; Click(view.CompareButton);
        require(view.Result?.Context.Any(t => t.Contains("体验记录")) == true, "A newly explicit manual comparison keeps its missing-experience limitation in the result");
        string stamp = CandidateComparison.Stamp(report!); File.WriteAllText(path, "{different-damage"); await RefreshAnalysis();
        require(CandidateComparison.Stamp(report!) != stamp && view.Result == null && view.CoverageConfirm.IsChecked == false, "Changed damaged bytes invalidate a manually reconfirmed comparison even at the same issue count");
        File.WriteAllBytes(path, original); await RefreshAnalysis();
        require(!report!.ExperienceQuality.HasIssues && report.ExperienceEvents.Single().Note == value.Note && view.CoverageConfirm.IsChecked == false, "Restoring the original valid event clears diagnosis without automatically restoring revoked confirmation");
        File.AppendAllText(path, "{tail"); await RefreshAnalysis();
        Click(HistoryNav); await RefreshAnalysis();
        Button? FindResolve(DependencyObject node)
        {
            if (node is Button button && button.Tag is ExperienceEvent e && e.Id == value.Id) return button;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                if (FindResolve(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
            return null;
        }
        UpdateLayout(); var resolve = FindResolve(ExperienceList);
        require(resolve != null, "The actual readable experience row exposes its resolve control beside the damaged-file warning");
        Click(resolve!);
        await wait(() => report?.ExperienceEvents.SingleOrDefault()?.Resolved == true);
        require(report!.ExperienceQuality.HasIssues && !PurchaseGuidance.Build(report).Any(d => d.Direction.Contains("较低档")), "Resolving a readable experience does not erase a separate damaged line or restore downgrade advice");
        File.WriteAllText(path, JsonSerializer.Serialize(value with { Resolved = true }) + "\n"); await RefreshAnalysis();
        require(!report!.ExperienceQuality.HasIssues && PurchaseGuidance.Build(report).Single(d => d.Key == "gpu").Direction.Contains("较低档"), "A clean history with the experience explicitly resolved restores the independently measured light direction");
        using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await RefreshAnalysis();
            require(report!.ExperienceQuality.HasIssues && report.History.Count > 0, "A real exclusive file lock reports unreadable experience while retaining telemetry");
        }
        await RefreshAnalysis();
        require(!report!.ExperienceQuality.HasIssues, "Releasing the controlled lock permits a clean retry without modifying the event");
        File.WriteAllText(Path.Combine(output, "NOTICE.txt"), "Synthetic experience and history rows under an empty isolated test root. Startup CPU/memory are actual. Corrupt tails, a real file lock and restored fixtures test data integrity, not physical disk failure or hardware purchasing accuracy. The actual resolve row control raises its routed click; this is not a physical mouse/file-dialog test.");
    }
}
