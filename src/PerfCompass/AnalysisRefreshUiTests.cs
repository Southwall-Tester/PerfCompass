using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunAnalysisRefreshUiTests(string output, Action<bool, string> require, Func<Func<bool>, Task> wait)
    {
        // Export destinations must be fresh even when the diagnostics output
        // folder retains evidence from a preceding run.
        string exportDirectory = Path.Combine(output, "exports-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exportDirectory);
        void Capture(string name)
        {
            UpdateLayout(); var visual = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name + ".png")); png.Save(file);
        }
        void Click(Button b) { b.Focus(); b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
        if (!paused) Click(PauseButton);
        var now = DateTimeOffset.Now.AddMinutes(-1); string hardware = collector!.HardwareId;
        for (int i = 0; i < 30; i++) store.Append(new Sample
        {
            Time = now.AddSeconds(-i * 10), Seconds = 10, Active = true, HardwareId = hardware, Scene = "合成范围任务",
            ObservationVersion = ActivityRules.Version, Cpu = 5, CoreMax = 20, MemoryTotalGb = 64, MemoryUsedGb = 8, MemoryPeakGb = 8,
            CommitGb = 10, Gpu = 3, GpuPeak = 3, GpuMetricSource = "GPU Core"
        });
        store.Append(new Sample { Time = now.AddDays(-8), Seconds = 10, Active = true, HardwareId = hardware, Scene = "合成范围任务",
            ObservationVersion = ActivityRules.Version, Cpu = 20, CoreMax = 50, MemoryTotalGb = 64, MemoryUsedGb = 12, CommitGb = 14,
            Gpu = 96, GpuPeak = 96, GpuMetricSource = "GPU Core" });
        Click(AdviceNav); await RefreshAnalysis(); await wait(() => report?.WindowDays == 7);
        AnalysisSceneCombo.SelectedItem = AnalysisSceneCombo.Items.OfType<ComboBoxItem>().Single(c => c.Tag as string == "合成范围任务");
        await wait(() => report?.SelectedScene == "合成范围任务" && report.WindowDays == 7);
        require(PurchaseGuidance.Build(report!).Single(d => d.Key == "gpu").Direction == "优先比较较低档显卡", "Initial synthetic seven-day context has a measured light GPU comparison direction");
        File.WriteAllText(Path.Combine(output, "NOTICE.txt"), "Synthetic light and older-heavy rows only under the guarded UI-test root. A real LocalStore gate delays the production analysis reader; no sleeps or device changes manufacture the result. The common startup checks use real CPU/memory. This diagnoses async context ownership, not hardware accuracy or a user purchasing recommendation.");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var holding = Task.Run(() => store.WithDataLock(() => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Diagnostic gate was not released."); return true; }));
        await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5)));
        Task first = Task.CompletedTask;
        try
        {
            first = RefreshAnalysis();
            WindowCombo.SelectedItem = WindowCombo.Items.OfType<ComboBoxItem>().Single(c => c.Tag.ToString() == "90");
            Task waiter = RefreshAnalysis();
            var snapshot = new { SelectedDays = windowDays, PublishedDays = report?.WindowDays, WaiterCompleted = waiter.IsCompleted, OwnerCompleted = first.IsCompleted,
                CurrentDirections = PurchaseGuidanceList.Items.OfType<ConfigurationDecision>().Select(d => new { d.Key, d.Direction, d.Scope }).ToArray() };
            File.WriteAllText(Path.Combine(output, "blocked-context.json"), JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            // Render the state synchronously before releasing the real reader.
            // Awaiting a dispatcher delay could let another callback enter the gate.
            UpdateLayout(); var visual = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(output, "blocked-context.png"))) png.Save(file);
            require(!waiter.IsCompleted && !first.IsCompleted, "A queued refresh waits for the latest selected context instead of returning the previous result");
            require(report == null && !PurchaseGuidanceList.Items.OfType<ConfigurationDecision>().Any(), "Changing analysis scope immediately retires old purchasing directions while the reader is pending");
            require(ConfigurationDraftPanel.Visibility == Visibility.Collapsed && ConfigurationDraftPanel.DataContext == null,
                "A pending scope change retires the old automatic configuration draft with the other purchasing advice");
            require(ComparisonPage.Triage == null && ComparisonPage.TriageCases.Items.Count == 0 && !ComparisonPage.ExportTriageButton.IsEnabled,
                "A pending scope change also retires concrete candidate screening and its export");
        }
        finally { release.Set(); await holding; await first.WaitAsync(TimeSpan.FromSeconds(30)); }
        await wait(() => report?.WindowDays == 90);
        require(report!.Cards.Single(c => c.Key == "gpu").RetainedWorkload != null
            && PurchaseGuidance.Build(report).Single(d => d.Key == "gpu").Direction.Contains("保留所选时段"), "Latest ninety-day result retains the older heavy GPU task after the blocked refresh finishes");

        async Task Until(Func<bool> condition)
        {
            for (int i = 0; i < 400; i++) { if (condition()) return; await Task.Delay(10); }
            throw new TimeoutException("Lifecycle condition did not occur.");
        }
        (Task Read, Task Entered, Action Release) Park()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); bool firstRead = true;
            BeforeAnalysisPublishForTest = _ => { if (!firstRead) return Task.CompletedTask; firstRead = false; entered.TrySetResult(); return released.Task; };
            return (RefreshAnalysis(), entered.Task, () => released.TrySetResult());
        }
        var candidate = new PurchaseCandidate { Name = "合成候选", MemoryGb = 32, Source = "合成规格，不是商品验证" };
        var criterion = new PurchaseCriterion { Name = "独立内存条件", Metric = "memory", Target = 16, Unit = "GB", Reviewed = true };
        CandidateComparison.Save(store.Root, new() { ContextStamp = CandidateComparison.Stamp(report), CoverageConfirmed = true, Candidates = [candidate], Criteria = [criterion] });
        // First navigation can occur before its requested analysis is ready.
        // Load the draft then, so a later SetContext never overwrites new edits.
        var boot = Park();
        try
        {
            await boot.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            WindowCombo.SelectedItem = WindowCombo.Items.OfType<ComboBoxItem>().Single(c => c.Tag.ToString() == "14");
            Click(ComparisonNav);
            require(ComparisonPage.Workspace.Candidates.Count == 1 && !ComparisonPage.CompareButton.IsEnabled, "First comparison navigation loads a saved draft even while current analysis is pending");
            await EditComparisonCell(ComparisonPage.CandidatesGrid, 0, 0, "首次加载等待中的编辑"); Click(ComparisonPage.SaveDraftButton);
        }
        finally { boot.Release(); await boot.Read.WaitAsync(TimeSpan.FromSeconds(30)); BeforeAnalysisPublishForTest = null; }
        require(ComparisonPage.Workspace.Candidates.Single().Name == "首次加载等待中的编辑", "Initial analysis completion cannot reload over a draft edited during first navigation");
        WindowCombo.SelectedItem = WindowCombo.Items.OfType<ComboBoxItem>().Single(c => c.Tag.ToString() == "90"); await wait(() => report?.WindowDays == 90);
        var view = ComparisonPage; view.CoverageConfirm.IsChecked = true; Click(view.CompareButton);
        require(view.Result?.Candidates.Single().State == "pass", "An existing synthetic comparison starts valid in the current scope");
        var cached = view.Result;
        var routine = Park();
        try
        {
            await routine.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            require(ReferenceEquals(cached, view.Result) && view.CoverageConfirm.IsChecked == true && view.CompareButton.IsEnabled,
                "Ordinary refresh with unchanged inputs keeps a valid comparison and coverage");
        }
        finally { routine.Release(); await routine.Read.WaitAsync(TimeSpan.FromSeconds(30)); BeforeAnalysisPublishForTest = null; }
        require(ReferenceEquals(cached, view.Result), "Publishing unchanged classifications preserves the previous comparison result");

        var published = new List<AnalysisReport>(); AnalysisPublishedForTest = value => published.Add(value);
        var oldPicker = ReportExportPathForTest; string destination = Path.Combine(exportDirectory, "latest-context.json"); int pickerCalls = 0;
        var changed = Park();
        const string future = "接下来每天需要处理新的本地视频任务";
        try
        {
            await changed.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Click(SettingsNav); FutureBox.Text = future; RequirementsConfirm.IsChecked = false; Click(SaveRequirementsButton);
            require(store.Settings.FutureNeeds == future && report == null, "The real settings save retires an older read before it can publish");
            Click(ComparisonNav);
            require(view.Result == null && !view.CompareButton.IsEnabled && !view.ImportCriteriaButton.IsEnabled
                && !view.ExportComparisonButton.IsEnabled && !view.CoverageConfirm.IsEnabled, "Pending input changes block old comparison import evaluation confirmation and export");
            Capture("comparison-pending");
            int goals = view.Workspace.Criteria.Count; Click(view.CompareButton); Click(view.ImportCriteriaButton);
            require(view.Result == null && view.Workspace.Criteria.Count == goals, "Pending handlers stay guarded even if a routed event is raised directly");
            try { view.ExportHtml(); require(false, "Pending export should not return old HTML"); }
            catch (InvalidOperationException) { require(true, "Direct comparison export rejects the pending context"); }
            await EditComparisonCell(view.CandidatesGrid, 0, 0, "等待期间保留的编辑"); Click(view.SaveDraftButton);
            require(CandidateComparison.Load(store.Root).Candidates.Single().Name == "等待期间保留的编辑" && !view.HasUnsavedDraft,
                "Editing and saving a candidate draft still works while analysis waits");
            Click(AdviceNav); ReportExportPathForTest = () => { pickerCalls++; return (destination, true); }; Click(ExportAnalysisButton);
            require(!ReportExportCompletion.IsCompleted && pickerCalls == 0 && !File.Exists(destination), "Report export waits for current analysis before asking for a destination");
        }
        finally { changed.Release(); await changed.Read.WaitAsync(TimeSpan.FromSeconds(30)); BeforeAnalysisPublishForTest = null; }
        await ReportExportCompletion.WaitAsync(TimeSpan.FromSeconds(30));
        require(published.Count > 0 && published.All(p => p.FutureNeeds == future), "A queued obsolete result never publishes between the saved requirement and the latest read");
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(destination)))
            require(pickerCalls == 1 && doc.RootElement.GetProperty("WindowDays").GetInt32() == 90 && doc.RootElement.GetProperty("FutureNeeds").GetString() == future,
                "Actual JSON export contains the latest range and saved requirements");
        require(CandidateComparison.Load(store.Root).Candidates.Single().Name == "等待期间保留的编辑", "Applying the refreshed context preserves the draft saved during the wait");

        // Same filter/settings, new durable experience: this needs its own input
        // revision rather than only checking a Settings record for equality.
        Click(LiveNav); var sceneChoice = new ComboBoxItem { Content = "合成范围任务" }; SceneCombo.Items.Add(sceneChoice); SceneCombo.SelectedItem = sceneChoice;
        await RefreshAnalysis(); published.Clear(); var experience = Park();
        try
        {
            await experience.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            ExperienceNote.Text = "合成体验：刚才任务等待很久"; Click(RecordExperienceButton);
            await Until(() => ExperienceStatus.Text.StartsWith("已保存", StringComparison.Ordinal));
            require(report == null && !view.CompareButton.IsEnabled, "A newly saved experience invalidates an already-read snapshot even with the same filter");
        }
        finally { experience.Release(); await experience.Read.WaitAsync(TimeSpan.FromSeconds(30)); BeforeAnalysisPublishForTest = null; }
        await wait(() => report?.ExperienceEvents.Count == 1 && RecordExperienceButton.IsEnabled);
        require(published.Count > 0 && published.All(p => p.ExperienceEvents.Any(e => e.Note == "合成体验：刚才任务等待很久")), "Only results containing the new durable experience publish after its save");

        // Inject an analysis-stage failure, not a physical device or disk fault.
        BeforeAnalysisPublishForTest = _ => Task.FromException(new IOException("Synthetic analysis publication failure; no device or disk settings changed."));
        try
        {
            await RefreshAnalysis(); Click(AdviceNav); await RefreshAnalysis();
            require(report == null && AdviceList.Items.Count == 0 && PurchaseGuidanceList.Items.Count == 0 && !view.CompareButton.IsEnabled,
                "A failed analysis retires previous purchasing results and comparison authority");
            Capture("analysis-failed");
            string failedPath = Path.Combine(exportDirectory, "must-not-export.json"); pickerCalls = 0;
            ReportExportPathForTest = () => { pickerCalls++; return (failedPath, true); }; Click(ExportAnalysisButton);
            await ReportExportCompletion.WaitAsync(TimeSpan.FromSeconds(30));
            require(pickerCalls == 0 && !File.Exists(failedPath), "Failed analysis cannot export the previous successful report");
            require(CandidateComparison.Load(store.Root).Candidates.Single().Name == "等待期间保留的编辑", "Failure preserves the saved candidate draft");
        }
        finally { BeforeAnalysisPublishForTest = null; ReportExportPathForTest = oldPicker; AnalysisPublishedForTest = null; }
        Click(RefreshAnalysisButton); await wait(() => report?.WindowDays == 90);
        require(!StatusLine.Text.Contains("分析读取失败") && view.CompareButton.IsEnabled && report!.FutureNeeds == future,
            "The real retry restores current analysis actions and clears only its resolved failure notice");

        var rapidPublished = new List<int>(); AnalysisPublishedForTest = p => rapidPublished.Add(p.WindowDays); var rapid = Park();
        try
        {
            await rapid.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            foreach (string days in new[] { "7", "14", "30" }) WindowCombo.SelectedItem = WindowCombo.Items.OfType<ComboBoxItem>().Single(c => c.Tag.ToString() == days);
            Task last = RefreshAnalysis(); require(report == null && !last.IsCompleted, "Rapid range changes share a pending completion for the last requested input");
        }
        finally { rapid.Release(); await rapid.Read.WaitAsync(TimeSpan.FromSeconds(30)); BeforeAnalysisPublishForTest = null; AnalysisPublishedForTest = null; }
        require(report?.WindowDays == 30 && rapidPublished.Count > 0 && rapidPublished.All(d => d == 30), "Rapid intermediate ranges never publish as the final selection");
        string pickerChangedPath = Path.Combine(exportDirectory, "picker-changed-context.json");
        ReportExportPathForTest = () => { WindowCombo.SelectedItem = WindowCombo.Items.OfType<ComboBoxItem>().Single(c => c.Tag.ToString() == "7"); return (pickerChangedPath, true); };
        Click(ExportAnalysisButton); await ReportExportCompletion.WaitAsync(TimeSpan.FromSeconds(30));
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(pickerChangedPath)))
            require(doc.RootElement.GetProperty("WindowDays").GetInt32() == 7, "An input change while choosing the export path triggers revalidation before writing");
        int cancellations = 0; ReportExportPathForTest = () => { cancellations++; return null; };
        Click(ExportAnalysisButton); await ReportExportCompletion.WaitAsync(TimeSpan.FromSeconds(30)); ReportExportPathForTest = oldPicker;
        require(cancellations == 1 && report?.WindowDays == 7 && CandidateComparison.Load(store.Root).Candidates.Single().Name == "等待期间保留的编辑", "Cancelling report export preserves the current analysis and saved candidate draft");
        Capture("analysis-current");
    }
}
