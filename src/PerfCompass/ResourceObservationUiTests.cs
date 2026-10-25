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
    private async Task RunResourceObservationUiTests(string output, Action<bool, string> require,
        Func<string, Task> screenshot, Func<Func<bool>, Task> waitForAnalysis)
    {
        void Click(Button button)
        {
            // A user click moves keyboard focus out of the edited grid. A bare
            // routed event leaves delayed cell BringIntoView requests queued.
            button.Focus(); button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
        IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
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
            // A small sliver is not a readable title or status. Permit only the
            // subpixel difference caused by layout rounding at a clipping edge.
            return !visible.IsEmpty && visible.Width > 8 && visible.Height > 8
                && visible.Width >= bounds.Width - .5 && visible.Height >= bounds.Height - .5;
        }
        Border Card(string key) => AdviceList.Items.OfType<Border>().Single(card => card.Tag as string == key);
        async Task RevealLeadingArea(FrameworkElement anchor, IEnumerable<FrameworkElement> labels)
        {
            // Settle queued focus/scroll and generated item containers before
            // calculating the pixel offset against the final scroll extent.
            await Dispatcher.InvokeAsync(() => UpdateLayout(), System.Windows.Threading.DispatcherPriority.ContextIdle);
            double bottom = labels
                .Select(label => label.TransformToAncestor(anchor).TransformBounds(new Rect(label.RenderSize)).Bottom)
                .DefaultIfEmpty(0).Max();
            // Request the complete leading area containing both labels, not a
            // one-pixel border point which can sit at the viewport's bottom.
            anchor.BringIntoView(new Rect(0, 0, anchor.ActualWidth, Math.Min(anchor.ActualHeight, bottom + 12)));
            // The main page scrolls in pixels. Align the resource or comparison
            // result near its top so the screenshot also shows nearby evidence.
            for (var ancestor = VisualTreeHelper.GetParent(anchor); ancestor != null;
                ancestor = VisualTreeHelper.GetParent(ancestor))
                if (ancestor is ScrollViewer scroll)
                {
                    if (!scroll.CanContentScroll && scroll.Content is Visual content)
                    {
                        double top = anchor.TransformToAncestor(content).Transform(new Point()).Y;
                        scroll.ScrollToVerticalOffset(Math.Max(0, top - 12));
                    }
                    break;
                }
            await Task.Delay(100); UpdateLayout();
        }
        async Task PreserveVisibilityFailure(string capture, string key, string title, string status, TextBlock[] text)
        {
            // Capture the failed layout before throwing, including failures on
            // the very first resource card or the final comparison decision.
            await screenshot(capture + "-visibility-failure");
            var surface = (FrameworkElement)Content;
            File.WriteAllText(Path.Combine(output, capture + "-visibility-failure.json"), JsonSerializer.Serialize(new
            {
                Key = key, DisplayTitle = title, StatusLabel = status, Width, Height,
                Scroll = Descendants((DependencyObject)Content).OfType<ScrollViewer>().Select(s => new
                { s.VerticalOffset, s.ExtentHeight, s.ViewportHeight, s.CanContentScroll, s.IsKeyboardFocusWithin, Content = s.Content?.GetType().Name }).ToArray(),
                Text = text.Select(t => new
                {
                    t.Text, t.IsVisible, t.ActualWidth, t.ActualHeight, FullyInViewport = InViewport(t),
                    Bounds = t.TransformToAncestor(surface).TransformBounds(new Rect(t.RenderSize)).ToString()
                }).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        async Task ShowCard(string key, string capture)
        {
            var advice = report!.Cards.Single(card => card.Key == key);
            var card = Card(key);
            UpdateLayout();
            var text = Descendants(card).OfType<TextBlock>().ToArray();
            var title = text.FirstOrDefault(t => t.Text == advice.DisplayTitle);
            var status = text.FirstOrDefault(t => t.Text == advice.StatusLabel);
            await RevealLeadingArea(card, new[] { title, status }.OfType<TextBlock>());
            bool visible = title != null && status != null && InViewport(title) && InViewport(status);
            if (!visible)
                await PreserveVisibilityFailure(capture, key, advice.DisplayTitle, advice.StatusLabel, text);
            require(visible,
                $"Rendered {key} observation title and status are visible inside the current viewport before capture");
            await screenshot(capture);
        }
        async Task ShowGuidance(string capture, bool lowerGpu)
        {
            UpdateLayout();
            var decisions = PurchaseGuidanceList.Items.OfType<ConfigurationDecision>().ToArray();
            var gpuDecision = decisions.Single(d => d.Key == "gpu");
            require(decisions.Length == 4 && (gpuDecision.Direction == "优先比较较低档显卡") == lowerGpu,
                "The actual purchase guidance changes the GPU decision when a heavy task replaces low readings");
            var row = Descendants(PurchaseGuidanceList).OfType<Border>().Single(b => b.Tag as string == "gpu");
            var details = Descendants(row).OfType<Expander>().Single();
            details.IsExpanded = true; UpdateLayout();
            var texts = Descendants(row).OfType<TextBlock>().ToArray();
            var direction = texts.SingleOrDefault(t => t.Text == gpuDecision.Direction);
            if (direction == null)
            {
                await PreserveVisibilityFailure(capture, "gpu-guidance", gpuDecision.Direction, gpuDecision.Level, texts);
                throw new InvalidOperationException("The purchase direction is missing from its rendered text control.");
            }
            var nextStep = texts.Single(t => t.Text == gpuDecision.NextStep);
            var reason = texts.Single(t => t.Text == gpuDecision.Reason);
            var scope = texts.Single(t => t.Text == gpuDecision.Scope);
            await RevealLeadingArea(row, new[] { direction, nextStep, reason, scope });
            require(new[] { direction, nextStep, reason, scope }.All(InViewport),
                "Purchase direction, next step, evidence and scope are readable in the rendered viewport");
            var html = ReportExporter.Html(report!, live);
            require(new[] { gpuDecision.Direction, gpuDecision.NextStep, gpuDecision.Reason, gpuDecision.Scope }
                .All(value => html.Contains(WebUtility.HtmlEncode(value)))
                && html.IndexOf("当前配置取舍", StringComparison.Ordinal) < html.IndexOf("核心/3D观测来源", StringComparison.Ordinal),
                "The exported report leads with the same purchase decision and preserves its evidence and scope");
            await screenshot(capture);
            details.IsExpanded = false;
        }
        void SaveReport(string name, AnalysisReport current, params Advice[] expected)
        {
            string html = ReportExporter.Html(current, live);
            File.WriteAllText(Path.Combine(output, name + ".html"), html);
            File.WriteAllText(Path.Combine(output, name + ".json"), JsonSerializer.Serialize(current,
                new JsonSerializerOptions { WriteIndented = true }));
            require(expected.All(card => card.Observation is { } observation
                    && html.Contains(WebUtility.HtmlEncode(card.DisplayTitle))
                    && html.Contains(WebUtility.HtmlEncode(card.StatusLabel))
                    && html.Contains(WebUtility.HtmlEncode(observation.Evidence))
                    && html.Contains(WebUtility.HtmlEncode(observation.Scope))
                    && html.Contains(WebUtility.HtmlEncode(card.DisplaySuggestion))),
                "HTML export uses the same observation title, evidence, scope and action as the native resource cards");
        }

        if (!paused) Click(PauseButton);
        require(paused && RecordState.Text.Contains("已暂停") && collector != null
            && Environment.GetCommandLineArgs().Contains("--ui-test"),
            "Resource observation fixture pauses production recording inside the guarded isolated UI-test session");
        const string scene = "资源初步判断（合成10分钟任务）";
        var now = DateTimeOffset.Now;
        var beginning = new DateTimeOffset(now.Date.AddDays(-1).AddHours(12), now.Offset);
        Sample Frame(int index) => new()
        {
            Time = beginning.AddSeconds((index + 1) * 10), HardwareId = collector!.HardwareId,
            Scene = scene, Seconds = 10, Active = true, ObservationVersion = ActivityRules.Version,
            Cpu = 5, CoreMax = 15, CpuCoverage = 10, CoreCoverage = 10, CpuHighSeconds = 0,
            MemoryUsedGb = 8, MemoryPeakGb = 9, MemoryTotalGb = 32, CommitGb = 10,
            MemoryCoverage = 10, CommitCoverage = 10, MemoryHighSeconds = 0,
            Gpu = 3, GpuPeak = 3, GpuCoverage = 10, GpuHeavySeconds = 0, GpuHighSeconds = 0,
            GpuMetricSource = "GPU Core", GpuEngineVersion = GpuEngineEvidence.Version,
            GpuEngines = [new("core", ["GPU Core"], 3) { Complete = true }],
            VramUsedGb = .5, VramTotalGb = 8, DiskUsedGb = 100, DiskTotalGb = 1000, MaxDiskPercent = 10
        };
        await Task.Run(() =>
        {
            for (int index = 0; index < 60; index++) store.Append(Frame(index));
        });
        Click(AdviceNav); await RefreshAnalysis();
        await waitForAnalysis(() => report!.SceneProfiles.Any(profile => profile.Name == scene));
        AnalysisSceneCombo.SelectedItem = AnalysisSceneCombo.Items.OfType<ComboBoxItem>()
            .Single(item => item.Tag as string == scene);
        await waitForAnalysis(() => report!.SelectedScene == scene && report.SampleCount == 60);
        var light = report!;
        var configuration = ConfigurationDraft.Build(light);
        require(ConfigurationDraftPanel.Visibility == Visibility.Visible
            && ConfigurationDraftPanel.DataContext is ConfigurationDraft draft && draft.Baseline.Rows.Length == 4
            && ConfigurationDraftList.Items.OfType<ConfigurationDraftRow>().Select(r => r.Recommendation)
                .SequenceEqual(configuration.Baseline.Rows.Select(r => r.Recommendation)),
            "Actual analysis entry publishes a four-component configuration draft from the selected scene");
        UpdateLayout();
        var configurationLabels = Descendants(ConfigurationDraftList).OfType<TextBlock>().Where(t => configuration.Baseline.Rows.Any(r => r.Recommendation == t.Text)).ToArray();
        await RevealLeadingArea(ConfigurationDraftPanel, configurationLabels);
        require(configurationLabels.Length == 4 && configurationLabels.All(InViewport),
            "All four configuration recommendations are readable together in the actual viewport");
        File.WriteAllText(Path.Combine(output, "configuration-draft.json"), JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true }));
        await screenshot("90-next-computer-configuration");
        require(ModelReferenceList.Items.Count > 0 && ModelReferenceList.Items.Count == configuration.Baseline.References.Items.Length,
            "Actual analysis shows reference models without importing products or creating candidates");
        UpdateLayout();
        var referenceLabels = Descendants(ModelReferenceList).OfType<TextBlock>()
            .Where(t => configuration.Baseline.References.Items.Any(r => r.Name == t.Text)).ToArray();
        await RevealLeadingArea(ModelReferenceList, referenceLabels);
        require(referenceLabels.Length == ModelReferenceList.Items.Count && referenceLabels.All(InViewport),
            "Reference model names are readable in the actual analysis viewport");
        await screenshot("91-reference-models");
        ConfigurationDraftEvidence.IsExpanded = true;
        await RefreshAnalysis();
        require(ConfigurationDraftEvidence.IsExpanded && ConfigurationDraftList.Items.Count == 4,
            "Ordinary refresh preserves configuration evidence expansion and does not create review confirmation");
        ConfigurationDraftEvidence.IsExpanded = false;
        var lightCards = new[] { "cpu", "memory", "gpu" }.Select(key => light.Cards.Single(card => card.Key == key)).ToArray();
        require(!light.Ready && !light.RepresentativeConfirmed && light.ActiveDays == 1
            && Math.Abs(light.ActiveHours - 1d / 6) < 1e-7 && !light.HistoryQuality.HasIssues
            && lightCards.All(card => card.Observation is { State: "light", ObservedHours: > 0, Coverage: >= .95 }
                && card.Observation.Scope.Contains(scene) && card.UsesObservation
                && card.DisplayTitle == card.Observation.Title && card.StatusLabel == "初步判断"),
            "One isolated ten-minute synthetic task produces concrete CPU memory and GPU light observations without purchase readiness or representative confirmation");
        require(AnalysisSummary.Text.Contains("初步判断") && CoverageLabel.Text.Contains(scene)
            && lightCards.All(card => Descendants(Card(card.Key)).OfType<TextBlock>().Any(text => text.Text == card.DisplayTitle)),
            "Native analysis summary and all three resource card titles reflect the selected task observations instead of the old generic waiting titles");
        SaveReport("resource-observation-light", light, lightCards);
        AdvicePage.BringIntoView(new Rect(0, 0, AdvicePage.ActualWidth, 10));
        await screenshot("70-purchase-guidance-overview");
        await ShowGuidance("67-purchase-guidance-light", true);
        Click(GuidanceCompareButton);
        await waitForAnalysis(() => ComparisonPage.Visibility == Visibility.Visible);
        require(ComparisonPage.Workspace.Candidates.Count == 0 && ComparisonPage.Workspace.Criteria.Count == 0
            && ComparisonPage.CoverageConfirm.IsChecked != true,
            "The purchase guidance comparison action opens the workspace without inventing candidates or confirmation");
        Click(AdviceNav); await RefreshAnalysis();
        await waitForAnalysis(() => AdvicePage.Visibility == Visibility.Visible && report!.SelectedScene == scene);
        await ShowCard("cpu", "55-resource-observation-cpu-light");
        await ShowCard("memory", "55-resource-observation-memory-light");
        var capacityExpander = Descendants(Card("memory")).OfType<Expander>().Single(e => e.Tag as string == "memory-capacity");
        capacityExpander.IsExpanded = true; UpdateLayout();
        var capacityPanel = (FrameworkElement)capacityExpander.Content;
        var capacityPreview = MemoryCapacityPreview.From(report!)!;
        require(capacityPreview != null && Descendants(capacityPanel).OfType<TextBlock>().Any(t => t.Text == capacityPreview.Baseline)
            && capacityPreview.Rows.All(r => Descendants(capacityPanel).OfType<TextBlock>().Any(t => t.Text == r.CapacityLabel)),
            "Native memory capacity comparison displays real analyzed units baseline and candidate differences");
        capacityPanel.BringIntoView(); await screenshot("71-memory-capacity-comparison");
        require(ReportExporter.Html(report!, live).Contains("容量与已测峰值对照") && ComparisonPage.Workspace.Criteria.Count == 0,
            "Capacity comparison exports with its scope and does not invent a candidate requirement");
        await RefreshAnalysis(); await waitForAnalysis(() => report!.SelectedScene == scene);
        require(Descendants(Card("memory")).OfType<Expander>().Single(e => e.Tag as string == "memory-capacity").IsExpanded,
            "Ordinary analysis refresh preserves the capacity comparison expansion");
        await ShowCard("gpu", "55-resource-observation-gpu-light");
        var storageExpander=Descendants(Card("disk")).OfType<Expander>().Single(e=>e.Tag as string=="storage-capacity");
        storageExpander.IsExpanded=true;UpdateLayout();
        var storagePreview=StorageCapacityPreview.From(report!)!;var storagePanel=(FrameworkElement)storageExpander.Content;
        require(storagePreview!=null && storagePreview.Rows.All(r=>Descendants(storagePanel).OfType<TextBlock>().Any(t=>t.Text==r.ConvertedLabel))
            && Descendants(storagePanel).OfType<TextBlock>().Any(t=>t.Text==storagePreview.Period),
            "Native storage comparison shows converted nominal capacity and the actual snapshot date");
        storagePanel.BringIntoView();await screenshot("72-storage-capacity-comparison");
        require(ReportExporter.Html(report!,live).Contains("标称容量与已测卷占用") && ComparisonPage.Workspace.Criteria.Count==0,
            "Storage comparison exports its conditional volume scope without creating a capacity requirement");
        await RefreshAnalysis();await waitForAnalysis(()=>report!.SelectedScene==scene);
        require(Descendants(Card("disk")).OfType<Expander>().Single(e=>e.Tag as string=="storage-capacity").IsExpanded,
            "Ordinary analysis refresh preserves the storage capacity comparison expansion");
        var gpuPurchaseExpander = Descendants(Card("gpu")).OfType<Expander>().Single(e => e.Tag as string == "gpu-purchase");
        gpuPurchaseExpander.IsExpanded = true; UpdateLayout();
        var gpuPurchasePanel = (FrameworkElement)gpuPurchaseExpander.Content;
        var gpuPlan = GpuPurchasePlan.Build(report!);
        var gpuTexts = Descendants(gpuPurchasePanel).OfType<TextBlock>().Select(t => t.Text).ToArray();
        require(gpuPlan.Mode == "light" && gpuPurchaseExpander.Header as string == gpuPlan.Title
            && gpuPlan.Routes.All(r => gpuTexts.Contains(r.Name + " · " + r.Order)) && gpuTexts.Contains(gpuPlan.NextStep),
            "GPU purchasing compares integrated lower discrete and current systems using the actual analysis plan");
        gpuPurchasePanel.BringIntoView(); await screenshot("73-gpu-purchasing-routes");
        require(ReportExporter.Html(report!, live).Contains("核显与独显的比较顺序") && ComparisonPage.Workspace.Criteria.Count == 0,
            "GPU purchasing exports the same plan without manufacturing candidate conditions or fit results");
        await RefreshAnalysis(); await waitForAnalysis(() => report!.SelectedScene == scene);
        require(Descendants(Card("gpu")).OfType<Expander>().Single(e => e.Tag as string == "gpu-purchase").IsExpanded,
            "Ordinary refresh preserves the GPU purchasing route expansion");
        string oldGpuTitle = lightCards.Single(card => card.Key == "gpu").DisplayTitle;

        await Task.Run(() => store.Append(Frame(0) with
        {
            Time = beginning.AddDays(-8), Gpu = 95, GpuPeak = 95,
            GpuHeavySeconds = 10, GpuHighSeconds = 10, GpuEngines = []
        }));
        WindowCombo.SelectedIndex = 2;
        await waitForAnalysis(() => report!.WindowDays == 30 && report.SampleCount == 61);
        require(report!.Cards.Single(card => card.Key == "gpu") is { UsesRetainedWorkload: true, Observation.State: "light" }
            && report.Cards.Single(card => card.Key == "gpu").RetainedWorkload!.Scope.Contains("所选 30 天"),
            "Changing the actual window to thirty days retains the older GPU task while preserving recent quiet observations");
        await ShowGuidance("69-purchase-guidance-retained-task", false);
        SaveReport("purchase-guidance-retained-task", report!);
        WindowCombo.SelectedIndex = 0;
        await waitForAnalysis(() => report!.WindowDays == 7 && report.SampleCount == 60);
        require(report!.Cards.Single(card => card.Key == "gpu").RetainedWorkload == null
            && PurchaseGuidanceList.Items.OfType<ConfigurationDecision>().Single(d => d.Key == "gpu").Direction == "优先比较较低档显卡",
            "Returning to seven days removes only the out-of-window task and restores the scoped GPU comparison direction");

        var partial = Frame(60) with
        {
            GpuEngines =
            [
                new("core", ["GPU Core"], 3) { Complete = true },
                new("compute", ["D3D Compute_0", "D3D Compute_1"], 96)
                {
                    Complete = false, Peak = 96, ObservedSeconds = 10, CoverageSeconds = 0,
                    HeavySeconds = 10, HighSeconds = 10
                }
            ]
        };
        await Task.Run(() => store.Append(partial));
        await RefreshAnalysis();
        await waitForAnalysis(() => report!.SelectedScene == scene && report.SampleCount == 61
            && report.Cards.Single(card => card.Key == "gpu").Observation?.State == "busy");
        var busy = report!; var gpu = busy.Cards.Single(card => card.Key == "gpu");
        var compute = busy.GpuEngineProfiles.Single(profile => profile.Kind == "compute");
        require(!busy.Ready && !busy.RepresentativeConfirmed && !busy.HistoryQuality.HasIssues
            && gpu.Observation is { State: "busy" } observation && observation.Evidence.Contains("96.0%")
            && observation.Evidence.Contains("部分读数") && observation.Scope.Contains(scene)
            && gpu.UsesObservation && gpu.DisplayTitle != oldGpuTitle && gpu.StatusLabel == "初步判断"
            && compute.Peak == 96 && compute.CompleteHours == 0 && Math.Abs(compute.ObservedHours - 10d / 3600) < 1e-7
            && busy.SceneProfiles.Single(profile => profile.Name == scene).GpuP95 == 3,
            "A single known 96-percent partial compute node replaces GPU light observation while core P95 stays low and whole-task readiness stays false");
        require(!Descendants(Card("gpu")).OfType<TextBlock>().Any(text => text.Text == oldGpuTitle)
            && busy.Cards.Where(card => card.Key is "cpu" or "memory").All(card => card.Observation?.State == "light"),
            "The GPU card no longer displays its old light title and independent CPU or memory observations remain scoped to their own readings");
        SaveReport("resource-observation-busy", busy, gpu);
        await ShowCard("gpu", "56-resource-observation-partial-compute-busy");
        double originalWidth = Width, originalHeight = Height; var originalState = WindowState;
        try
        {
            WindowState = WindowState.Normal; Width = MinWidth; Height = MinHeight;
            await ShowGuidance("68-purchase-guidance-busy-minimum-window", false);
            await ShowCard("gpu", "57-resource-observation-minimum-window");
        }
        finally { Width = originalWidth; Height = originalHeight; WindowState = originalState; }

        var comparisonButton = Descendants(Card("gpu")).OfType<Button>()
            .Single(button => button.Tag as string == "compare:gpu");
        comparisonButton.BringIntoView(); await Task.Delay(100); UpdateLayout();
        require(comparisonButton.Content?.ToString() == "比较候选配置" && comparisonButton.IsEnabled && InViewport(comparisonButton),
            "The busy GPU observation exposes a visible enabled real candidate-comparison action");
        Click(comparisonButton);
        await waitForAnalysis(() => ComparisonPage.Visibility == Visibility.Visible && PageTitle.Text == "候选配置");
        var view = ComparisonPage;
        require(view.Workspace.Candidates.Count == 0 && view.Workspace.Criteria.Count == 0 && view.CoverageConfirm.IsChecked != true,
            "The resource card action opens the real empty comparison workspace without manufacturing candidates or confirmation");
        Click(view.AddCandidateButton);
        await EditComparisonCell(view.CandidatesGrid, 0, 0, "合成单候选（尚未验证）");
        await EditComparisonCell(view.CandidatesGrid, 0, 1, 6000);
        await EditComparisonCell(view.CandidatesGrid, 0, 2, 32);
        await EditComparisonCell(view.CandidatesGrid, 0, 5, "合成 CPU（非商品）");
        await EditComparisonCell(view.CandidatesGrid, 0, 6, "合成 GPU（非商品）");
        await EditComparisonCell(view.CandidatesGrid, 0, 7, "独立 UI 合成输入，不是商品报价或实测依据");
        Click(view.AddCriterionButton);
        await EditComparisonCell(view.CriteriaGrid, 0, 1, "原有合成任务条件（仍待核对）");
        string originalCriterion = ((PurchaseCriterion)view.CriteriaGrid.Items[0]).Id;
        Click(view.ImportCriteriaButton);
        require(view.Workspace.Candidates.Count == 1 && view.Workspace.Criteria.Count > 1
            && view.Workspace.Criteria.Any(criterion => criterion.Id == originalCriterion && !criterion.Reviewed)
            && view.Workspace.Criteria.Any(criterion => criterion.OriginId == "gpu-engine:compute")
            && view.Workspace.Criteria.All(criterion => !criterion.Reviewed) && view.CoverageConfirm.IsChecked != true,
            "Importing the actual busy-engine observation preserves the existing condition and leaves every condition and coverage unreviewed");
        Click(view.CompareButton);
        require(view.Result is { Candidates.Count: 1 } && view.Result.Candidates.Single().State == "unknown"
            && view.Workspace.Criteria.All(criterion => !criterion.Reviewed) && view.CoverageConfirm.IsChecked != true
            && view.ComparisonResults.Items.Count == 1 && view.ComparisonSummary.Text == view.Result.Summary,
            "The real compare button evaluates one candidate instead of requiring two while retaining unknown results for unreviewed conditions");
        var reopened = CandidateComparison.Load(store.Root);
        require(reopened.Candidates.Count == 1 && !reopened.CoverageConfirmed
            && reopened.Criteria.Any(criterion => criterion.Id == originalCriterion)
            && reopened.Criteria.All(criterion => !criterion.Reviewed),
            "Saving the single-candidate comparison does not silently confirm the original or newly imported conditions");
        File.WriteAllText(Path.Combine(output, "resource-observation-single-candidate.html"), view.ExportHtml());
        UpdateLayout();
        require(view.Result is { Candidates.Count: 1 } && view.ComparisonResults.Items.Count == 1,
            "Creating display-mode grid bindings during layout preserves the computed single-candidate result");
        var resultText = Descendants(view.ComparisonResults).OfType<TextBlock>().ToArray();
        string candidateName = view.Result!.Candidates.Single().Name;
        const string unknownLabel = "待验证，暂不能确认满足需求";
        var resultTitle = resultText.FirstOrDefault(text => text.Text == candidateName);
        var resultStatus = resultText.FirstOrDefault(text => text.Text == unknownLabel);
        await RevealLeadingArea(view.ComparisonResults, new[] { resultTitle, resultStatus }.OfType<TextBlock>());
        bool resultVisible = resultTitle != null && resultStatus != null && InViewport(resultTitle) && InViewport(resultStatus);
        if (!resultVisible)
            await PreserveVisibilityFailure("57-resource-observation-single-candidate", "comparison", candidateName, unknownLabel, resultText);
        require(resultVisible,
            "The single-candidate name and unknown decision are fully rendered inside the viewport before capture");
        await screenshot("57-resource-observation-single-candidate");
        foreach (var edit in new (DataGrid Grid, int Column, object Value, string Kind)[]
        {
            (view.CandidatesGrid, 1, 6100, "text"),
            (view.CriteriaGrid, 2, "price", "selection"),
            (view.CriteriaGrid, 0, true, "checkbox")
        })
        {
            view.CoverageConfirm.IsChecked = true;
            Click(view.CompareButton);
            await EditComparisonCell(edit.Grid, 0, edit.Column, edit.Value);
            require(view.Result == null && view.ComparisonResults.Items.Count == 0
                && view.CoverageConfirm.IsChecked != true && !view.Workspace.CoverageConfirmed,
                $"Editing a real {edit.Kind} cell still invalidates the previous result and coverage confirmation");
        }
        await RunCandidateDecisionUiTests(output, require, screenshot);
        await RunCandidateTriageUiTests(output, require, screenshot, waitForAnalysis);
        File.WriteAllText(Path.Combine(output, "resource-observation-NOTICE.txt"),
            "The initial three common checks use actual local CPU/memory collection. This flow then pauses recording and appends 60 ten-second synthetic rows, one earlier 95% core row and one partial 96% compute-engine row, only to the guarded empty UI-test data directory. The explicitly synthetic selected scene excludes startup telemetry. Seven/thirty-day selection and the rendered purchasing direction are exercised; these are synthesized records, not days of actual use. Native store/history analysis, visible text and scope, actual comparison actions and grids, saved review state and HTML export are checked. Candidate names and prices are synthetic; no candidate condition or task is claimed verified. No workload, model, hardware setting or production history is changed.");
    }
}
