using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunUiTests()
    {
        var output = Path.GetFullPath("artifacts/ui"); Directory.CreateDirectory(output);
        var results = new List<string>();
        File.WriteAllText(Path.Combine(output, "results.txt"), "RUNNING " + DateTimeOffset.Now.ToString("O"));
        void Require(bool value, string name) { results.Add((value ? "PASS " : "FAIL ") + name); File.AppendAllText(Path.Combine(output, "results.txt"), "\n" + results[^1]); if (!value) throw new Exception(name); }
        async Task WaitForAnalysis(Func<bool> ready)
        {
            for (int i = 0; i < 200; i++) { if (!analyzing && !refreshRequested && ready()) return; await Task.Delay(50); }
            throw new TimeoutException("Analysis did not reach the expected scope.");
        }
        async Task Screenshot(string name)
        {
            await Task.Delay(200);
            UpdateLayout();
            var visual = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
        }
        try
        {
            for (int i = 0; i < 30 && live == null; i++) await Task.Delay(500);
            Require(live?.Sample.Cpu is >= 0 and <= 100, "Real CPU sample available");
            Require(live?.Sample.MemoryTotalGb > 0, "Real memory sample available");
            Require(MemoryDetail.Text.Contains("GiB"), "Real memory telemetry is displayed in binary units");
            await Task.Delay(6000); await Screenshot("01-live");
            File.WriteAllText(Path.Combine(output, "hardware.json"), JsonSerializer.Serialize(live, new JsonSerializerOptions { WriteIndented = true }));
            PauseButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Require(paused && PauseButton.Content.ToString() == "继续记录", "Pause button changes collection state");
            int before = store.Read(7).Count(); await Task.Delay(11000);
            Require(store.Read(7).Count() == before, "Paused recording writes no new samples");
            SceneCombo.SelectedIndex = 1;
            Require(store.Settings.Scene == "开发编译", "Scene selection persists");
            AdviceNav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await RefreshAnalysis(); await Task.Delay(300);
            Require(AdvicePage.Visibility == Visibility.Visible, "Advice navigation opens real page");
            await Screenshot("02-advice-empty");
            // Synthetic long-term trace is written only under the explicitly isolated test data directory.
            await Task.Run(() => {
            for (int day = 2; day >= 0; day--)
                for (int i = 0; i < 1250; i++)
                    store.Append(new Sample { Time = DateTimeOffset.Now.AddDays(-day).AddSeconds(-(1250-i) * 10), HardwareId = collector!.HardwareId,
                        Seconds = 10, Active = true, Cpu = 8, CoreMax = 25, MemoryTotalGb = 64, MemoryUsedGb = 9, MemoryPeakGb = 10, CommitGb = 11,
                        Gpu = 5, VramUsedGb = 1, DiskTotalGb = 1024, DiskUsedGb = 200, MaxDiskPercent = 20, Scene = "开发编译" });
            });
            store.SaveSettings(store.Settings with { Representative = true });
            await RefreshAnalysis(); await Task.Delay(300);
            Require(report!.Ready, "Representative long-term trace becomes eligible");
            Require(report.Cards.Any(c => c.Status == "surplus"), "Surplus guidance rendered through production analyzer");
            if (AdviceList.Items[0] is Border b && b.Child is StackPanel p) p.Children.OfType<Expander>().First().IsExpanded = true;
            await Screenshot("03-advice-populated");
            await Task.Run(() => {
                for (int i = 0; i < 10; i++) store.Append(new Sample { Time = DateTimeOffset.Now.AddSeconds(-100 + i * 10), HardwareId = collector!.HardwareId,
                    Seconds = 10, Active = true, Scene = "游戏娱乐", Cpu = 60, CoreMax = 100, MemoryTotalGb = 64, MemoryUsedGb = 20, CommitGb = 24,
                    Gpu = 100, VramUsedGb = 5, DiskTotalGb = 1024, DiskUsedGb = 200, MaxDiskPercent = 20 });
            });
            await RefreshAnalysis(); await WaitForAnalysis(() => report!.SceneProfiles.Any(p => p.Name == "游戏娱乐"));
            Require(report!.Cards.Single(c => c.Key == "gpu").Status == "fit", "Rare GPU task blocks global downgrade review");
            Require(SceneProfileList.Items.Count >= 2, "Scenario profiles reach the rendered UI");
            AnalysisSceneCombo.SelectedItem = AnalysisSceneCombo.Items.OfType<ComboBoxItem>().Single(x => x.Tag as string == "游戏娱乐");
            await WaitForAnalysis(() => report!.SelectedScene == "游戏娱乐");
            Require(report!.SampleCount == 10 && !report.Ready && report.ActiveHours < .1, "Scenario selector recomputes its own evidence");
            Require(CoverageLabel.Text.Contains("分钟") && report.DurationLabel != "0 分钟", "Brief task duration remains visible");
            Require(HistorySummary.Text.Contains("游戏娱乐") && report.History.Count <= 2, "History follows selected scenario");
            await Screenshot("07-scene-analysis");
            string scopedHtml = ReportExporter.Html(report, live);
            File.WriteAllText(Path.Combine(output, "scoped-report.html"), scopedHtml);
            Require(scopedHtml.Contains("当前建议范围：游戏娱乐") && scopedHtml.Contains("场景明细"), "Report preserves selected scenario and profiles");
            AnalysisSceneCombo.SelectedIndex = 0;
            await WaitForAnalysis(() => report!.SelectedScene == null && report.Ready);
            Require(report!.SampleCount >= 3760, "Returning to all scenes restores full evidence");
            await Task.Run(() => {
                foreach (int day in new[] { 8, 9, 1, 2 })
                    for (int i = 0; i < 180; i++) store.Append(new Sample { Time = DateTimeOffset.Now.AddDays(-day).AddHours(-5).AddSeconds(-i * 10),
                        HardwareId = collector!.HardwareId, Seconds = 10, Active = true, Scene = day < 7 ? "AI 与虚拟机" : "日常办公",
                        Cpu = 8, CoreMax = 25, MemoryTotalGb = 64, MemoryUsedGb = 9, CommitGb = 11, Gpu = day < 7 ? 85 : 2,
                        VramUsedGb = day < 7 ? 5 : 1, DiskTotalGb = 1024, DiskUsedGb = 200, MaxDiskPercent = 20 });
            });
            await RefreshAnalysis(); await WaitForAnalysis(() => report!.DemandTrends.Any(t => t.Key == "gpu" && t.Days == 7 && t.Status == "increased"));
            Require(DemandTrendHeader.Text.Contains("显卡增加"), "Recent GPU demand shift reaches the UI");
            Require(report!.Cards.Single(c => c.Key == "gpu").Status == "changed", "Recent workload overrides historical sizing in the UI");
            WindowCombo.SelectedItem = WindowCombo.Items.OfType<ComboBoxItem>().Single(x => x.Tag.ToString() == "90");
            await WaitForAnalysis(() => report!.WindowDays == 90);
            Require(report!.Cards.Single(c => c.Key == "gpu").Status == "changed", "Long history view preserves recent demand priority");
            DemandTrendExpander.IsExpanded = true;
            await Screenshot("08-recent-demand-shift");
            var temporalHtml = ReportExporter.Html(report, live);
            File.WriteAllText(Path.Combine(output, "recent-demand-report.html"), temporalHtml);
            Require(temporalHtml.Contains("此前 7 天") && temporalHtml.Contains("近期增加") && temporalHtml.Contains("AI 与虚拟机"), "Exported report explains recent demand shift");
            Require(RetentionCombo.Items.OfType<ComboBoxItem>().Any(x => x.Tag.ToString() == "90"), "Longer context retention is available");
            DemandTrendExpander.IsExpanded = false;
            WindowCombo.SelectedIndex = 0;
            await WaitForAnalysis(() => report!.WindowDays == 7);
            HistoryNav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Task.Delay(400); await Screenshot("04-history");
            SettingsNav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Screenshot("05-settings");
            Width = 960; Height = 640;
            LiveNav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Screenshot("06-minimum-window");
            File.WriteAllText(Path.Combine(output, "synthetic-report.html"), ReportExporter.Html(report, live));
            ReportExporter.Csv(Path.Combine(output, "samples.csv"), store.Read(7, collector!.HardwareId));
            Require(new FileInfo(Path.Combine(output, "samples.csv")).Length > 1000, "CSV exported");
            PauseButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Require(!paused, "Resume button restores recording");
            DateTimeOffset resumeAt = DateTimeOffset.Now; await Task.Delay(14000);
            Require(store.Read(7).Any(s => s.Time > resumeAt && s.Seconds <= 15), "Resume excludes pause duration");
            SettingsNav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            FutureBox.Text = "准备做 AI";
            ParseNeedsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Require(requirementDraft?.NeedsAiChoice == true && AiModePanel.Visibility == Visibility.Visible, "Ambiguous AI description asks for execution mode");
            RequirementsConfirm.IsChecked = true;
            SaveRequirementsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Require(store.Settings.Requirements == null && RequirementsStatus.Text.Contains("明确 AI"), "Ambiguous AI cannot silently become a hardware constraint");
            AiModeCombo.SelectedItem = AiModeCombo.Items.OfType<ComboBoxItem>().Single(i => i.Tag.ToString() == "cloud");
            SaveRequirementsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await WaitForAnalysis(() => report!.ConfirmedRequirements?.AiMode == "cloud");
            Require(store.Settings.Requirements?.AiMode == "cloud", "Confirmed structured requirement reaches saved settings and analysis");
            await Screenshot("09-structured-needs");
            FutureBox.Text = "不玩游戏，只做办公";
            Require(RequirementsConfirm.IsChecked == false && requirementDraft == null, "Editing source clears prior confirmation");
            ParseNeedsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Require(requirementDraft!.Facts.Single(f => f.Key == "game").Excluded, "Negated task reaches the rendered review");
            RequirementsConfirm.IsChecked = true;
            SaveRequirementsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await WaitForAnalysis(() => report!.ConfirmedRequirements?.Source == FutureBox.Text);
            LiveNav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            ExperienceEntry.IsExpanded = true; ExperienceNote.Text = "刚才编译很慢 <script>";
            RecordExperienceButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await WaitForAnalysis(() => report!.ExperienceEvents.Count == 1 && RecordExperienceButton.IsEnabled);
            Require(report!.ExperienceEvents.Single().ParsedDescription.Contains("开发编译"), "Experience description is parsed and linked to analysis");
            HistoryNav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await WaitForAnalysis(() => ExperienceList.Items.Count == 1); await Screenshot("10-experience-record");
            var experienceHtml = ReportExporter.Html(report!, live);
            File.WriteAllText(Path.Combine(output, "experience-report.html"), experienceHtml);
            Require(experienceHtml.Contains("体验与负载线索") && experienceHtml.Contains("&lt;script&gt;") && experienceHtml.Contains("已确认的结构化需求"), "Report combines confirmed needs and escaped experience evidence");
            UpdateLayout();
            Button? resolveButton = null;
            void FindResolve(DependencyObject parent)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                {
                    var child = VisualTreeHelper.GetChild(parent, i);
                    if (child is Button { Tag: ExperienceEvent } b) resolveButton = b;
                    FindResolve(child);
                }
            }
            FindResolve(ExperienceList);
            Require(resolveButton != null, "Experience resolution is available through its real control");
            resolveButton!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await WaitForAnalysis(() => report!.ExperienceEvents.Single().Resolved);
            Require(new LocalStore(store.Root).ReadExperiences(7, collector!.HardwareId).Single().Resolved, "Resolved experience survives reopening");
            await RunSemanticUiTests(output, Require, Screenshot, WaitForAnalysis);
            await RunTaskMeasurementUiTests(output, Require, Screenshot, WaitForAnalysis);
            await RunQuantityGoalUiTests(output, Require, Screenshot, WaitForAnalysis);
            await RunProviderReplayUiTests(Require, Screenshot, WaitForAnalysis);
            await RunDataProtectionUiTests(output, Require);
            File.WriteAllText(Path.Combine(output, "NOTICE.txt"), "01-live.png and hardware.json use real telemetry. Populated analysis and historical captures use isolated synthetic test data; they are not a recommendation for this computer.");
        }
        catch (Exception e) { store.Log(e); results.Add("FAIL " + e); }
        finally { File.WriteAllLines(Path.Combine(output, "results.txt"), results); Environment.ExitCode = results.Any(x => x.StartsWith("FAIL")) ? 1 : 0; Exit(); }
    }
}
