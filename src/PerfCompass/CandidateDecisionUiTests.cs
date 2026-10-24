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
    private async Task RunCandidateDecisionUiTests(string output, Action<bool, string> require, Func<string, Task> screenshot)
    {
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Task Cell(DataGrid grid, int row, int column, object value) => EditComparisonCell(grid, row, column, value);
        IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
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
            return !visible.IsEmpty && visible.Width > 8 && visible.Height > 8
                && visible.Width >= bounds.Width - .5 && visible.Height >= bounds.Height - .5;
        }
        async Task<bool> Capture(string name, FrameworkElement anchor, params TextBlock?[] labels)
        {
            UpdateLayout();
            double bottom = labels.OfType<TextBlock>().Select(label =>
                label.TransformToVisual(anchor).TransformBounds(new Rect(label.RenderSize)).Bottom).DefaultIfEmpty(0).Max();
            anchor.BringIntoView(new Rect(0, 0, anchor.ActualWidth, bottom + 12));
            for (var ancestor = VisualTreeHelper.GetParent(anchor); ancestor != null;
                ancestor = VisualTreeHelper.GetParent(ancestor))
                if (ancestor is ScrollViewer scroll)
                {
                    if (!scroll.CanContentScroll && scroll.Content is Visual content)
                        scroll.ScrollToVerticalOffset(Math.Max(0, anchor.TransformToAncestor(content).Transform(new Point()).Y - 12));
                    break;
                }
            await Task.Delay(100); UpdateLayout();
            bool visible = labels.All(label => label != null && InViewport(label));
            await screenshot(visible ? name : name + "-visibility-failure");
            if (!visible)
            {
                var surface = (FrameworkElement)Content;
                File.WriteAllText(Path.Combine(output, name + "-visibility-failure.json"), JsonSerializer.Serialize(new
                {
                    Width, Height, Summary = ComparisonPage.ComparisonSummary.Text,
                    Labels = labels.Select(label => label == null ? null : new
                    {
                        label.Text, label.IsVisible, label.ActualWidth, label.ActualHeight,
                        FullyInViewport = InViewport(label),
                        Bounds = label.TransformToAncestor(surface).TransformBounds(new Rect(label.RenderSize)).ToString()
                    }).ToArray()
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            return visible;
        }

        if (!paused) Click(PauseButton);
        Click(ComparisonNav);
        var view = ComparisonPage;
        require(paused && collector != null && Environment.GetCommandLineArgs().Contains("--ui-test")
            && view.Visibility == Visibility.Visible && view.Workspace.Candidates.Count == 1,
            "Candidate decision extension reuses the isolated synthetic workspace with recording paused");
        File.WriteAllText(Path.Combine(output, "candidate-decision-NOTICE.txt"),
            "This extension reuses the independent observation UI-test workspace while collection stays paused. All candidates, quotations, sources, manual checks and task times are synthetic. Existing conditions are removed through the real controls; all new values are entered through actual grid editing and comparison buttons. The check verifies quote-source gating and removal of unreviewed conditional task benefits. No workload or model is run, no physical task outcome or commercial quotation is verified, and no production history is changed.");
        for (int remaining = view.CriteriaGrid.Items.Count; remaining > 0; remaining--)
        {
            view.CriteriaGrid.SelectedIndex = 0;
            Click(view.RemoveCriterionButton);
            if (view.CriteriaGrid.Items.Count != remaining - 1) throw new InvalidOperationException("The real remove-condition action did not commit.");
        }
        const string candidateA = "合成候选 A（非商品）", candidateB = "合成候选 B（非商品）";
        const string requiredName = "合成必需任务：兼容性核对";
        const string taskName = "合成条件任务：统一项目耗时", condition = "接到合成视频项目时";
        const string passLabel = "必需条件通过 · 条件需求另行核对";
        await Cell(view.CandidatesGrid, 0, 0, candidateA);
        await Cell(view.CandidatesGrid, 0, 1, 6000);
        await Cell(view.CandidatesGrid, 0, 7, "");
        Click(view.AddCandidateButton);
        await Cell(view.CandidatesGrid, 1, 0, candidateB);
        await Cell(view.CandidatesGrid, 1, 1, 9000);
        await Cell(view.CandidatesGrid, 1, 2, 32);
        await Cell(view.CandidatesGrid, 1, 5, "合成 CPU（非商品）");
        await Cell(view.CandidatesGrid, 1, 6, "合成 GPU（非商品）");
        await Cell(view.CandidatesGrid, 1, 7, "");
        Click(view.AddCriterionButton);
        await Cell(view.CriteriaGrid, 0, 1, requiredName);
        await Cell(view.CriteriaGrid, 0, 2, "check");
        await Cell(view.CriteriaGrid, 0, 7, "合成需求：指定软件可运行");
        await Cell(view.CriteriaGrid, 0, 8, "同版本软件与合成输入，人工核对启动结果");
        await Cell(view.CriteriaGrid, 0, 0, true);
        Click(view.PrepareMeasurementsButton);
        for (int index = 0; index < view.ObservationsGrid.Items.Count; index++)
        {
            await Cell(view.ObservationsGrid, index, 3, "pass");
            await Cell(view.ObservationsGrid, index, 5, "合成人工核对记录，非真实软件验证");
            await Cell(view.ObservationsGrid, index, 4, true);
        }
        void Compare()
        {
            view.CoverageConfirm.IsChecked = true;
            Click(view.CompareButton);
            UpdateLayout();
        }
        bool RequiredPasses() => view.Result is { Candidates.Count: 2 } result
            && result.Candidates.All(candidate => candidate.State == "pass"
                && candidate.Checks.Single(check => check.Name == requiredName).State == "pass");
        bool SaveReport(string name)
        {
            string html = view.ExportHtml();
            File.WriteAllText(Path.Combine(output, name + ".html"), html);
            File.WriteAllText(Path.Combine(output, name + ".json"), JsonSerializer.Serialize(new
            { view.Workspace, view.Result }, new JsonSerializerOptions { WriteIndented = true }));
            return view.Result != null && html.Contains(WebUtility.HtmlEncode(view.Result.Summary))
                && view.Result.Tradeoffs.All(tradeoff => html.Contains(WebUtility.HtmlEncode(tradeoff)))
                && view.Result.Candidates.SelectMany(candidate => candidate.Checks)
                    .All(check => html.Contains(WebUtility.HtmlEncode(check.Reading)));
        }
        Compare();
        var missingText = Descendants(view.ComparisonResults).OfType<TextBlock>().ToArray();
        bool missingVisible = await Capture("61-candidate-missing-quote-source", view.ComparisonSummary,
            view.ComparisonSummary, missingText.FirstOrDefault(text => text.Text == candidateA),
            missingText.FirstOrDefault(text => text.Text == passLabel));
        bool missingHtml = SaveReport("candidate-decision-missing-sources");
        require(RequiredPasses() && view.Workspace.Candidates.All(candidate => string.IsNullOrWhiteSpace(candidate.Source))
            && view.Result!.Summary.Contains("待补报价来源") && !view.Result.Summary.Contains("可少花")
            && !view.Result.Summary.Contains("3000") && view.Result.Tradeoffs.Length == 0
            && view.ComparisonTradeoffs.Items.Count == 0 && missingVisible && missingHtml,
            "Two manually passed tasks with unsourced quotes remain visible passes without invented price savings or tradeoffs");

        await Cell(view.CandidatesGrid, 0, 7, "合成报价单 A 2026-10-23（非实际商品）");
        Compare();
        require(RequiredPasses() && view.Result!.Summary.Contains("待补报价来源：" + candidateB)
            && !view.Result.Summary.Contains("可少花") && !view.Result.Summary.Contains("3000")
            && view.Result.Tradeoffs.Length == 0 && SaveReport("candidate-decision-one-source"),
            "Supplying only one quote source does not create a price difference against the unsourced candidate");

        await Cell(view.CandidatesGrid, 1, 7, "合成报价单 B 2026-10-23（非实际商品）");
        Compare();
        require(RequiredPasses() && view.Result!.Summary.Contains("可少花 3000 元")
            && !view.Result.Summary.Contains("待补报价来源") && view.Result.Tradeoffs.Length == 1
            && view.Result.Tradeoffs[0].Contains("多花 3000 元") && SaveReport("candidate-decision-complete-sources"),
            "Supplying both quote sources restores the 3000-yuan difference consistently in the native result and HTML");

        view.ShortlistExpander.IsExpanded = true; UpdateLayout();
        var shortlistText = Descendants(view.ComparisonShortlists).OfType<TextBlock>().ToArray();
        var budgetSummary = shortlistText.SingleOrDefault(t => t.Text == view.Result!.Shortlists[0].Summary);
        bool shortlistVisible = await Capture("74-purchasing-shortlist-budget", view.ComparisonShortlists, budgetSummary,
            shortlistText.SingleOrDefault(t => t.Text == "尚无任务收益对照"));
        require(view.Result!.Shortlists[0].Options.Single(o => o.Name == candidateA).Label == "预算优先"
            && view.Result.Shortlists[0].Options.Single(o => o.Name == candidateB).Label == "尚无任务收益对照"
            && shortlistVisible && view.ExportHtml().Contains(WebUtility.HtmlEncode(view.Result.Shortlists[0].Summary)),
            "Actual purchasing shortlist recommends the sourced budget option without calling extra specifications task benefits");
        view.ShortlistExpander.IsExpanded = false;

        Click(view.AddCriterionButton);
        int taskRow = view.CriteriaGrid.Items.Count - 1;
        await Cell(view.CriteriaGrid, taskRow, 1, taskName);
        await Cell(view.CriteriaGrid, taskRow, 2, "task");
        await Cell(view.CriteriaGrid, taskRow, 3, "max");
        await Cell(view.CriteriaGrid, taskRow, 4, 12);
        await Cell(view.CriteriaGrid, taskRow, 5, "秒");
        await Cell(view.CriteriaGrid, taskRow, 6, condition);
        await Cell(view.CriteriaGrid, taskRow, 7, "合成条件需求，非实际用户任务");
        await Cell(view.CriteriaGrid, taskRow, 8, "同版本工具与输入、相同模式，手填合成耗时");
        await Cell(view.CriteriaGrid, taskRow, 0, true);
        string taskId = ((PurchaseCriterion)view.CriteriaGrid.Items[taskRow]).Id;
        string candidateAId = ((PurchaseCandidate)view.CandidatesGrid.Items[0]).Id;
        Click(view.PrepareMeasurementsButton);
        for (int index = 0; index < view.ObservationsGrid.Items.Count; index++)
        {
            var observation = (PurchaseObservation)view.ObservationsGrid.Items[index];
            if (observation.CriterionId != taskId) continue;
            await Cell(view.ObservationsGrid, index, 2, observation.CandidateId == candidateAId ? 9 : 5);
            await Cell(view.ObservationsGrid, index, 5, "同协议合成手填耗时，非实际重复测量");
            await Cell(view.ObservationsGrid, index, 4, true);
        }
        Compare();
        require(RequiredPasses() && view.Result!.Candidates.All(candidate =>
                candidate.Checks.Single(check => check.Name == taskName) is { State: "pass", EvidenceKind: "manual-measurement" })
            && view.Result.Tradeoffs.Single().Contains(taskName + "：9 → 5 秒（条件：" + condition + "）")
            && SaveReport("candidate-decision-reviewed-condition"),
            "A reviewed conditional task displays its manual 9-to-5-second comparison with the explicit trigger condition");

        view.ShortlistExpander.IsExpanded = true; UpdateLayout();
        var branchPanel = (FrameworkElement)view.ComparisonShortlists.ItemContainerGenerator.ContainerFromIndex(1);
        var branchText = Descendants(branchPanel).OfType<TextBlock>().ToArray();
        var branchDetail = view.Result!.Shortlists[1].Options.Single(o => o.Name == candidateB);
        bool branchVisible = await Capture("75-purchasing-shortlist-conditional", branchPanel,
            branchText.SingleOrDefault(t => t.Text == view.Result.Shortlists[1].Summary),
            branchText.SingleOrDefault(t => t.Text == branchDetail.Detail));
        require(view.Result.Shortlists.Length == 2 && view.Result.Shortlists[0].Options.Single(o => o.Name == candidateB).Label == "尚无任务收益对照"
            && branchDetail.Label == "记录到任务优势" && branchDetail.Detail.Contains("手填任务值 9 → 5 秒") && branchVisible,
            "Actual shortlist keeps conditional task gains separate from the base budget and renders their manual provenance");
        view.ShortlistExpander.IsExpanded = false;

        await Cell(view.CriteriaGrid, taskRow, 0, false);
        Compare();
        var finalText = Descendants(view.ComparisonResults).OfType<TextBlock>().ToArray();
        var finalTitle = finalText.FirstOrDefault(text => text.Text == candidateA);
        bool unreviewedVisible = await Capture("62-candidate-conditional-unreviewed", finalTitle ?? view.ComparisonSummary,
            finalTitle, finalText.FirstOrDefault(text => text.Text == passLabel),
            finalText.FirstOrDefault(text => text.Text == taskName),
            finalText.FirstOrDefault(text => text.Text.Contains("待验证：条件已修改或尚未核对")));
        var saved = CandidateComparison.Load(store.Root);
        require(RequiredPasses() && view.Result!.Candidates.All(candidate =>
                candidate.Checks.Single(check => check.Name == taskName).State == "unknown")
            && view.Result.Tradeoffs.All(tradeoff => !tradeoff.Contains(taskName) && !tradeoff.Contains("9 → 5"))
            && !Descendants(view.ComparisonTradeoffs).OfType<TextBlock>().Any(text => text.Text.Contains(taskName))
            && saved.Criteria.Single(criterion => criterion.Id == taskId).Reviewed == false
            && SaveReport("candidate-decision-unreviewed-condition") && unreviewedVisible
            && paused && !TaskMeasurementPage.Running,
            "Unreviewing the conditional task preserves required passes but removes its benefit from visible decisions saved state and HTML while recording remains paused");
        require(view.Result!.Shortlists[0].Options.Any(o => o.Name == candidateA && o.Label == "预算优先")
            && view.Result.Shortlists[1].Options.All(o => o.CandidateId.Length == 0)
            && !view.ExportHtml().Contains("手填任务值 9 → 5"),
            "Unreviewed conditional goals retract their purchasing recommendation without erasing the independent base budget");

        const string excerpt = "Model: 合成摘录候选（非商品）\nProcessor: 合成 CPU\nGraphics: 合成 GPU 8GB GDDR7\nMemory: 2x 16GB DDR5\nMax Memory: Up to 128GB\nStorage: 1TB SSD";
        var oldResult = view.Result;
        var specExpander = Descendants(view).OfType<Expander>().Single(e => e.Header as string == "从规格摘录添加候选");
        specExpander.IsExpanded = true;
        view.SpecSourceBox.Text = "合成规格资料，不含报价"; view.SpecTextBox.Text = excerpt;
        string importedExcerpt = view.SpecTextBox.Text;
        Click(view.SpecPreviewButton); UpdateLayout();
        var previewHeading = Descendants(view.SpecPreviewPanel).OfType<TextBlock>().Single(t => t.Text.StartsWith("核对后的候选字段"));
        bool specVisible = await Capture("76-specification-preview", view.SpecPreviewPanel, previewHeading);
        require(ReferenceEquals(oldResult, view.Result) && view.SpecMemoryBox.Text == "32" && view.SpecStorageBox.Text == "1000"
            && view.SpecVramBox.Text == "8" && view.SpecAddButton.IsEnabled && specVisible && InViewport(view.SpecMemoryBox),
            "Actual specification preview extracts installed values without invalidating the existing comparison or selecting maximum options");
        view.SpecMemoryBox.Text = "未完成的修订";
        require(view.TrySaveDraft(force: true) && CandidateComparison.Load(store.Root).SpecificationDraft.Memory == "未完成的修订",
            "Specification draft autosave accepts incomplete capacity input without losing original text");
        view.DraftImportPathProvider = () => Path.Combine(store.Root, "comparison.json");
        Click(view.ImportDraftButton); UpdateLayout();
        require(view.SpecMemoryBox.Text == "未完成的修订" && view.SpecTextBox.Text == importedExcerpt && view.SpecAddButton.IsEnabled
            && view.CoverageConfirm.IsChecked != true && view.Result == null,
            "Actual draft import restores editable specification fields and clears comparison coverage confirmation");
        view.SpecMemoryBox.Text = "32"; view.SpecSourceBox.Text += "（修订来源）";
        require(!view.SpecAddButton.IsEnabled && view.SpecMemoryBox.Text == "32",
            "Editing the source disables stale specification application while preserving manually edited fields");
        Click(view.SpecPreviewButton); Click(view.SpecAddButton); UpdateLayout();
        var imported = view.Workspace.Candidates.Single(c => c.Name == "合成摘录候选（非商品）");
        require(view.Workspace.Candidates.Count == 3 && imported.MemoryGb == 32 && imported.VramGb == 8 && imported.StorageGb == 1000
            && imported.PriceCny == null && imported.Source == "" && imported.SpecificationSource.Contains(importedExcerpt)
            && view.Workspace.SpecificationDraft.Text == "" && view.Result == null && view.CoverageConfirm.IsChecked != true
            && view.TrySaveDraft(force: true) && CandidateComparison.Load(store.Root).Candidates.Single(c => c.Id == imported.Id).SpecificationSource == imported.SpecificationSource,
            "Actual add action preserves excerpt provenance without creating a quote task result or coverage confirmation");
        await Cell(view.CandidatesGrid, 2, 1, 5000);
        foreach (var goal in new[] { (Name: "合成独立规格内存", Metric: "memory", Direction: "min", Target: 16, Unit: "GB"),
            (Name: "合成独立报价预算", Metric: "price", Direction: "max", Target: 10000, Unit: "元") })
        {
            Click(view.AddCriterionButton); int index = view.CriteriaGrid.Items.Count - 1;
            await Cell(view.CriteriaGrid, index, 1, goal.Name); await Cell(view.CriteriaGrid, index, 2, goal.Metric);
            await Cell(view.CriteriaGrid, index, 3, goal.Direction); await Cell(view.CriteriaGrid, index, 4, goal.Target);
            await Cell(view.CriteriaGrid, index, 5, goal.Unit); await Cell(view.CriteriaGrid, index, 0, true);
        }
        Compare();
        var importedResult = view.Result!.Candidates.Single(c => c.Id == imported.Id);
        require(importedResult.Checks.Single(c => c.Name == "合成独立规格内存").State == "pass"
            && importedResult.Checks.Single(c => c.Name == "合成独立报价预算").State == "unknown"
            && view.Result.Shortlists[0].Options.All(o => o.CandidateId != imported.Id),
            "Actual comparison uses imported specifications for capacity goals while an entered price still needs its own quote source");
        var sourceExpander = Descendants(view.ComparisonResults).OfType<Expander>().Single(e => e.Tag as string == "candidate-specification"
            && e.Content is TextBlock t && t.Text == imported.SpecificationSource);
        sourceExpander.IsExpanded = true; UpdateLayout();
        bool sourceVisible = await Capture("77-specification-provenance", sourceExpander, (TextBlock)sourceExpander.Content);
        require(sourceVisible && view.ExportHtml().Contains(WebUtility.HtmlEncode(importedExcerpt))
            && importedResult.Checks.Single(c => c.Name == "合成独立规格内存").Evidence.Contains("完整原文见")
            && paused && !TaskMeasurementPage.Running,
            "Native result and HTML preserve the complete specification excerpt without repeating it in every numeric check or executing a task");
    }
}
