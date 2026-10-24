using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunPurchaseScenarioUiTests(string output, Action<bool, string> require,
        Func<string, Task> screenshot, Func<Func<bool>, Task> wait)
    {
        void Click(Button b) { b.Focus(); b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
        if (!paused) Click(PauseButton);
        const string source = "工具尚未选，可能只用光谱工具甲，也可能只用音频插件乙，只选一种。";
        var facts = new List<SemanticFact> {
            new("工具", "compatibility", "uncertain", "future", "只用光谱工具甲", "", "", "可能只用光谱工具甲"),
            new("工具", "compatibility", "uncertain", "future", "只用音频插件乙", "", "", "可能只用音频插件乙") };
        foreach (var fact in facts) SemanticRequirements.Validate(source, fact);
        store.SaveSettings(store.Settings with { FutureNeeds = source, Requirements = null,
            SemanticRequirements = new(source, "synthetic-human-confirmed", "none", SemanticRequirements.Version, facts, DateTimeOffset.Now) });
        await RefreshAnalysis(); await wait(() => report?.SemanticRequirements?.Source == source);
        Click(ComparisonNav); await wait(() => ComparisonPage.Visibility == Visibility.Visible);
        var view = ComparisonPage;
        File.WriteAllText(Path.Combine(output, "NOTICE.txt"), "Isolated synthetic semantic facts, candidates and manual verdicts. No model call or hardware compatibility claim. Actual navigation, criteria import, cell editing, confirmation, comparison and legacy draft import; WPF visual renders do not cover OS mouse or file dialogs. Common startup checks use actual CPU/memory.");
        require(view.Workspace.Criteria.Count == 0 && view.Workspace.Candidates.Count == 0, "Uncertainty flow opens the real comparison entry with empty isolated data");
        Click(view.ImportCriteriaButton);
        var branches = view.Workspace.Criteria.Where(c => c.OriginId.StartsWith("semantic:", StringComparison.Ordinal)).ToArray();
        require(branches.Length == 2 && branches[0].Condition == branches[1].Condition && branches.All(c => !c.Reviewed)
            && view.CoverageConfirm.IsChecked != true, "Native criteria import preserves two unknown facts without auto confirmation");
        for (int i = 0; i < 2; i++)
        {
            Click(view.AddCandidateButton);
            await EditComparisonCell(view.CandidatesGrid, i, 0, i == 0 ? "合成光谱候选" : "合成音频候选");
            await EditComparisonCell(view.CandidatesGrid, i, 1, i == 0 ? 6000 : 8000);
            await EditComparisonCell(view.CandidatesGrid, i, 2, 32);
            await EditComparisonCell(view.CandidatesGrid, i, 7, "合成规格及报价，不是商品资料");
        }
        Click(view.AddCriterionButton); int memory = view.Workspace.Criteria.Count - 1;
        await EditComparisonCell(view.CriteriaGrid, memory, 1, "共同内存目标");
        await EditComparisonCell(view.CriteriaGrid, memory, 2, "memory");
        await EditComparisonCell(view.CriteriaGrid, memory, 4, 16);
        await EditComparisonCell(view.CriteriaGrid, memory, 5, "GB");
        for (int i = 0; i < view.Workspace.Criteria.Count; i++) await EditComparisonCell(view.CriteriaGrid, i, 0, true);
        Click(view.PrepareMeasurementsButton);
        async Task FillEvidence()
        {
            for (int i = 0; i < view.Workspace.Observations.Count; i++)
            {
                var o = view.Workspace.Observations[i];
                int candidate = view.Workspace.Candidates.FindIndex(c => c.Id == o.CandidateId);
                int branch = Array.FindIndex(branches, c => c.Id == o.CriterionId);
                await EditComparisonCell(view.ObservationsGrid, i, 3, branch < 0 || candidate == branch ? "pass" : "fail");
                await EditComparisonCell(view.ObservationsGrid, i, 5, "合成人工核对，同一合成任务设置");
                await EditComparisonCell(view.ObservationsGrid, i, 4, true);
            }
        }
        await FillEvidence(); view.CoverageConfirm.IsChecked = true; Click(view.CompareButton);
        require(view.Result is { ScenarioSummaries.Length: 2 } && view.Result.Candidates.All(c => c.State == "pass"), "Native comparison keeps common passes and creates two separate uncertain scenarios");
        require(view.Result!.ScenarioSummaries[0].Contains("光谱工具甲") && view.Result.ScenarioSummaries[0].Contains("6000")
            && !view.Result.ScenarioSummaries[0].Contains("8000") && view.Result.ScenarioSummaries[1].Contains("音频插件乙")
            && view.Result.ScenarioSummaries[1].Contains("8000"), "Each unknown option selects only its own matching synthetic candidate");
        require(view.ComparisonScenarios.Items.Count == 2 && view.ContextStatus.Text.Contains("待定选项分别比较"), "Rendered scenario list and instructions distinguish uncertain options");
        async Task Capture(string name)
        {
            await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.ContextIdle);
            view.ComparisonScenarios.BringIntoView(); await Task.Delay(150); UpdateLayout();
            var root = (FrameworkElement)Content;
            var bounds = view.ComparisonScenarios.TransformToAncestor(root).TransformBounds(new Rect(view.ComparisonScenarios.RenderSize));
            require(view.ComparisonScenarios.IsVisible && bounds.Top >= 0 && bounds.Bottom <= root.ActualHeight, "Complete scenario text is inside the visible window: " + name);
            await screenshot(name);
        }
        await Capture("separate-unknown-options");
        string html = view.ExportHtml(); File.WriteAllText(Path.Combine(output, "separate-options.html"), html);
        require(html.Contains("comparison-1.3") && html.Contains("光谱工具甲") && html.Contains("音频插件乙")
            && CandidateComparison.Load(store.Root).Version == "comparison-1.3", "Export and saved draft use the scope-safe schema");
        var previous = view.Result; view.CandidatesGrid.BringIntoView(); UpdateLayout();
        require(ReferenceEquals(previous, view.Result), "Display-only layout retains the computed result");
        foreach (var branch in branches) await EditComparisonCell(view.CriteriaGrid, view.Workspace.Criteria.IndexOf(branch), 6, "同时接到联合项目");
        require(view.Result == null && view.CoverageConfirm.IsChecked != true && branches.All(c => c.ReviewStamp != CandidateComparison.CriterionStamp(c)), "Actual trigger edits revoke result coverage and scope reviews");
        foreach (var branch in branches) await EditComparisonCell(view.CriteriaGrid, view.Workspace.Criteria.IndexOf(branch), 0, true);
        await FillEvidence(); view.CoverageConfirm.IsChecked = true; Click(view.CompareButton);
        require(view.Result is { ScenarioSummaries.Length: 1 } && view.Result.ScenarioSummaries[0].Contains("当前没有通过")
            && view.Result.Candidates.All(c => c.State == "pass"), "Shared explicit trigger requires both capabilities while common goals still pass");
        await Capture("explicit-joint-trigger");
        string legacyPath = Path.Combine(output, "legacy-1.2-original.json");
        using (var resource = typeof(MainWindow).Assembly.GetManifestResourceStream("PerfCompass.LegacyUncertainty.json")!)
        using (var file = File.Create(legacyPath)) resource.CopyTo(file);
        byte[] original = File.ReadAllBytes(legacyPath);
        var legacyRaw = JsonSerializer.Deserialize<PurchaseWorkspace>(original)!;
        var oldPicker = view.DraftImportPathProvider;
        try { view.DraftImportPathProvider = () => legacyPath; Click(view.ImportDraftButton); }
        finally { view.DraftImportPathProvider = oldPicker; }
        require(view.Workspace.Version == "comparison-1.3" && view.Workspace.Criteria.Where(CandidateComparison.IsUnresolvedCondition).All(c => !c.Reviewed)
            && view.CoverageConfirm.IsChecked != true, "Actual old-writer draft import clears obsolete unknown-scope reviews and coverage");
        object EvidenceFields(PurchaseObservation o) => new { o.CandidateId, o.CriterionId, o.Value, o.Verdict, o.Evidence, o.Measurement, o.Comparable, o.ProofStamp };
        require(original.SequenceEqual(File.ReadAllBytes(legacyPath)) && JsonSerializer.Serialize(legacyRaw.Observations.Select(EvidenceFields)) == JsonSerializer.Serialize(view.Workspace.Observations.Select(EvidenceFields)), "Legacy import preserves source bytes and all result evidence");
        require(view.Workspace.Observations.All(o => o.CandidateName == view.Workspace.Candidates.Single(c => c.Id == o.CandidateId).Name
            && o.CriterionName == view.Workspace.Criteria.Single(c => c.Id == o.CriterionId).Name), "Imported result labels derive from current referenced candidates and criteria even when legacy display copies were blank");
        Click(view.CompareButton);
        require(view.Result is { ScenarioSummaries.Length: 2 } && view.Result.Candidates.All(c => c.State == "unknown")
            && view.ContextStatus.Text.Contains("重新核对"), "Legacy evidence cannot silently become newly confirmed");
        await Capture("legacy-review-required");
    }
}
