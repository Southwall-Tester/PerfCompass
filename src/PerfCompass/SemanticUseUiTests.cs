using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunSemanticUseUiTests(string output, Action<bool, string> require, Func<string, Task> screenshot, Func<Func<bool>, Task> wait)
    {
        void Click(Button b) { b.Focus(); b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
        IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            { var child = VisualTreeHelper.GetChild(parent, i); if (child is T value) yield return value; foreach (var nested in Children<T>(child)) yield return nested; }
        }
        ComboBox UseControl(SemanticFactEditor editor) => Children<ComboBox>(SemanticEditors).Single(c => c.DataContext == editor && System.Windows.Automation.AutomationProperties.GetName(c) == "购机用途");
        void SelectUse(SemanticFactEditor editor, string use)
        { var control = UseControl(editor); control.SetCurrentValue(Selector.SelectedValueProperty, use); control.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource(); }
        if (!paused) Click(PauseButton);
        var now = DateTimeOffset.Now.AddMinutes(-1); string hardware = collector!.HardwareId;
        const string scene = "购机用途合成任务";
        for (int i = 0; i < 30; i++) store.Append(new Sample { Time = now.AddSeconds(-i * 10), Seconds = 10, Active = true, HardwareId = hardware, Scene = scene,
            ObservationVersion = ActivityRules.Version, Cpu = 5, CoreMax = 20, MemoryTotalGb = 64, MemoryUsedGb = 8, MemoryPeakGb = 8, CommitGb = 10, Gpu = 3, GpuPeak = 3, GpuMetricSource = "GPU Core" });
        Click(AdviceNav); await RefreshAnalysis();
        AnalysisSceneCombo.SelectedItem = AnalysisSceneCombo.Items.OfType<ComboBoxItem>().Single(c => c.Tag as string == scene);
        await wait(() => report?.SelectedScene == scene);
        var facts = new List<SemanticFact> {
            new("混音", "experience", "required", "past", "卡顿", "", "", "昨天混音卡顿，下一台仍要解决"),
            new("直播", "experience", "excluded", "current", "丢帧", "", "", "我不允许直播丢帧"),
            new("离线渲染", "experience", "excluded", "current", "快", "", "", "离线渲染不要求快"),
            new("OrbitalStack", "workload", "conditional", "future", "运行OrbitalStack", "", "接课", "如果接课才运行OrbitalStack"),
            new("内存", "resource", "required", "current", "至少32", "GB", "", "内存至少32GB") };
        string source = string.Join("；", facts.Select(f => f.Evidence)) + "；附注：旧任务只作背景";
        Click(SettingsNav); FutureBox.Text = source; SemanticPanel.IsExpanded = true;
        ShowSemanticDraft(new(source, "synthetic-source-facts", "本地测试，无模型调用", facts, [])); UpdateLayout();
        Click(Children<Button>(SemanticPanel).Single(b => b.Content as string == "手工补充事项")); UpdateLayout();
        var supplement = SemanticEditors.Items.Cast<SemanticFactEditor>().Last();
        void WriteField(string field, string value)
        {
            var box = Children<TextBox>(SemanticEditors).Single(b => b.DataContext == supplement && b.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == field);
            box.SetCurrentValue(TextBox.TextProperty, value); box.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        }
        WriteField(nameof(SemanticFactEditor.Subject), "旧任务"); WriteField(nameof(SemanticFactEditor.Value), "只作背景"); WriteField(nameof(SemanticFactEditor.Evidence), "附注：旧任务只作背景");
        SelectUse(supplement, "omit");
        require(SemanticEditors.Items.Count == 6 && supplement.DecisionUse == "omit" && !supplement.Include, "The actual manual-add entry supplements an omitted source statement without invoking a model or selecting it");
        var editors = SemanticEditors.Items.Cast<SemanticFactEditor>().ToArray();
        require(editors.Take(3).All(e => e.DecisionUse == "clarify") && editors.All(e => !e.Include), "Past and negative source facts render with pending use and remain unselected");
        foreach (var box in Children<CheckBox>(SemanticEditors)) { box.SetCurrentValue(ToggleButton.IsCheckedProperty, true); box.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource(); }
        SemanticReviewConfirm.IsChecked = true; Click(SaveSemanticButton);
        await wait(() => report?.SemanticRequirements?.Facts.Count == 6);
        require(report!.SemanticUseNotice.Length > 0 && report.Cards.Single(c => c.Key == "gpu").Status == "meaning", "Saving facts with an explicitly unresolved purpose keeps the real analysis in use review");
        require(CandidateComparison.Drafts(report).Count(c => c.OriginId.StartsWith("semantic:") && c.Condition.Length > 0) == 4, "Past negative and course-dependent statements retain separate conditional goals");
        SelectUse(editors[0], "avoid");
        require(SemanticReviewConfirm.IsChecked == false && editors[0].Timeframe == "past" && editors[0].Evidence == facts[0].Evidence, "The actual purpose selector invalidates review without rewriting source tense or quote");
        SelectUse(editors[1], "avoid"); SelectUse(editors[2], "omit"); SelectUse(editors[3], "require"); SelectUse(editors[4], "require");
        SemanticReviewConfirm.IsChecked = true; Click(SaveSemanticButton);
        await wait(() => report?.SemanticRequirements?.Facts[0].DecisionUse == "avoid" && report.SemanticUseNotice.Length == 0);
        require(store.Settings.SemanticRequirements!.ContractVersion == SemanticUse.PlanVersion && new LocalStore(store.Root).Settings.SemanticRequirements!.Facts[2].DecisionUse == "omit", "Real save and settings reopen retain the distinct review contract and chosen purposes");
        var goals = CandidateComparison.Drafts(report!).Where(c => c.OriginId.StartsWith("semantic:")).ToList();
        require(goals.Count == 4 && goals.Count(c => c.Name.Contains("必须避免")) == 2 && goals.All(c => !c.Name.Contains("离线渲染")), "Required and forbidden goals propagate while a waived speed target stays out of the candidate checklist");
        require(goals.Single(c => c.Metric == "memory").Target == 32 && goals.Single(c => c.Name.Contains("OrbitalStack")).Condition == "接课", "Positive quantities and actual trigger conditions keep their existing meaning");
        require(report!.Cards.Single(c => c.Key == "gpu").Status == "requirements" && report.SemanticRequirements!.Facts[0].Timeframe == "past", "A past experience explicitly selected for resolution constrains downgrade without changing chronology");
        UseControl(editors[0]).BringIntoView(); await screenshot("semantic-use-review");

        CandidateComparison.Save(store.Root, new() { Candidates = [new() { Name = "合成候选", MemoryGb = 64, Source = "合成规格，非商品验证" }] });
        Click(ComparisonNav); var view = ComparisonPage; Click(view.ImportCriteriaButton);
        require(view.Workspace.Criteria.Count(c => c.OriginId.StartsWith("semantic:")) == 4 && view.Workspace.Criteria.All(c => !c.Reviewed) && view.CoverageConfirm.IsChecked == false, "Real candidate import keeps four semantic goals and observed-scene checks unreviewed without confirming coverage");
        for (int i = 0; i < view.CriteriaGrid.Items.Count; i++) await EditComparisonCell(view.CriteriaGrid, i, 0, true);
        Click(view.PrepareMeasurementsButton);
        for (int i = 0; i < view.ObservationsGrid.Items.Count; i++)
        {
            await EditComparisonCell(view.ObservationsGrid, i, 3, "pass");
            await EditComparisonCell(view.ObservationsGrid, i, 5, "合成手工核对，非商品验证");
            await EditComparisonCell(view.ObservationsGrid, i, 4, true);
        }
        view.CoverageConfirm.IsChecked = true; Click(view.CompareButton);
        require(view.Result?.Candidates.Single().State == "pass" && view.Result.ScenarioSummaries.Any(s => s.Contains("接课")), "Reviewed synthetic candidate satisfies required checks while the course goal remains a separate scenario");
        var memory = view.Workspace.Criteria.Single(c => c.Metric == "memory"); string evidence = memory.Evidence; int observations = view.Workspace.Observations.Count;
        Click(SettingsNav); SelectUse(editors[4], "omit"); SemanticReviewConfirm.IsChecked = true; Click(SaveSemanticButton);
        await wait(() => report?.SemanticRequirements?.Facts[4].DecisionUse == "omit");
        Click(ComparisonNav);
        require(view.Result == null && view.CoverageConfirm.IsChecked == false && view.SemanticSourceNotice.Visibility == Visibility.Visible, "Changing a saved purpose retires the result and exposes the obsolete imported goal");
        Click(view.ImportCriteriaButton);
        view.CoverageConfirm.IsChecked = true; Click(view.CompareButton);
        require(view.Result == null && view.ComparisonStatus.Text.Contains("源需求") && view.Workspace.Criteria.Contains(memory), "An obsolete source is an explicit input-review problem and its data is retained");
        await screenshot("semantic-source-changed");
        view.CriteriaGrid.SelectedItem = memory; Click(view.DetachCriterionButton);
        require(memory.OriginId == "" && !memory.Reviewed && memory.Target == 32 && memory.Evidence == evidence && view.Workspace.Observations.Count == observations && view.CoverageConfirm.IsChecked == false,
            "The actual independent-target action preserves the original target evidence and measurements while revoking confirmation");
        require(view.SemanticSourceNotice.Visibility == Visibility.Collapsed, "The obsolete-source notice clears after the source relationship is explicitly removed");
        int index = view.Workspace.Criteria.IndexOf(memory);
        await EditComparisonCell(view.CriteriaGrid, index, 1, "独立内存任务目标"); await EditComparisonCell(view.CriteriaGrid, index, 4, 16d);
        await EditComparisonCell(view.CriteriaGrid, index, 7, "合成独立任务内存目标16GB"); await EditComparisonCell(view.CriteriaGrid, index, 0, true);
        for (int i = 0; i < view.CriteriaGrid.Items.Count; i++) await EditComparisonCell(view.CriteriaGrid, i, 0, true);
        view.CoverageConfirm.IsChecked = true; Click(view.CompareButton);
        require(view.Result?.Candidates.Single().State == "pass", "A separately edited and explicitly reviewed independent goal works beside the retained conditional checks");

        Click(SettingsNav); UpdateLayout();
        var subject = Children<TextBox>(SemanticEditors).Single(b => b.DataContext == editors[0] && System.Windows.Automation.AutomationProperties.GetName(b) == "候选事项名称");
        SemanticReviewConfirm.IsChecked = true; subject.SetCurrentValue(TextBox.TextProperty, "修改的混音事项"); subject.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        require(editors[0].DecisionUse == "clarify" && SemanticReviewConfirm.IsChecked == false, "An actual source-fact edit clears both its old purchasing use and whole-source confirmation");
        subject.SetCurrentValue(TextBox.TextProperty, facts[0].Subject); subject.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); SelectUse(editors[0], "avoid");
        SemanticReviewConfirm.IsChecked = true; Click(SaveSemanticButton); await RefreshAnalysis();
        string export = Path.Combine(output, "use-report-" + Guid.NewGuid().ToString("N") + ".html"); var picker = ReportExportPathForTest;
        try { ReportExportPathForTest = () => (export, false); Click(AdviceNav); Click(ExportAnalysisButton); await ReportExportCompletion.WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { ReportExportPathForTest = picker; }
        string html = File.ReadAllText(export);
        require(html.Contains("购机用途：必须避免") && html.Contains("购机用途：不作为购机条件") && html.Contains("过去"), "Actual report export preserves background sources chronology and the chosen prohibition");
        var configuration = (ConfigurationDraft)ConfigurationDraftPanel.DataContext;
        require(configuration.Conditions.Any(c => c.Label == "接课" && c.Source.Contains("如果接课才运行OrbitalStack"))
            && html.Contains("下一台电脑配置草案") && html.Contains("条件原文："),
            "Saved conditional purpose reaches both the automatic configuration draft and exported report with its source");
        ConfigurationDraftConditions.IsExpanded = true; UpdateLayout();
        ConfigurationDraftConditions.BringIntoView(new Rect(0, 0, ConfigurationDraftConditions.ActualWidth, 100));
        await screenshot("configuration-conditional-source");
        require(Children<TextBlock>(ConfigurationDraftConditions).Any(t => t.IsVisible && t.Text == "接课")
            && Children<TextBlock>(ConfigurationDraftConditions).Any(t => t.IsVisible && t.Text.Contains("条件原文：如果接课才运行OrbitalStack")),
            "Native conditional configuration exposes the trigger and original source together");
        ConfigurationDraftConditions.IsExpanded = false;

        // Exercise the real initialization routine against legacy saved facts.
        var legacy = new SemanticPlan(source, "synthetic-legacy", "local", SemanticRequirements.Version, facts, DateTimeOffset.Now);
        store.SaveSettings(store.Settings with { FutureNeeds = source, SemanticRequirements = legacy });
        byte[] original = File.ReadAllBytes(Path.Combine(store.Root, "settings.json"));
        Click(SettingsNav); InitializeSemanticSettings(); UpdateLayout(); await RefreshAnalysis();
        var loaded = SemanticEditors.Items.Cast<SemanticFactEditor>().ToArray();
        require(loaded.Take(3).All(e => e.DecisionUse == "clarify") && SemanticReviewConfirm.IsChecked == false && report!.SemanticUseNotice.Length > 0, "Legacy initialization preserves facts and requests use review instead of silently retiring past or excluded statements");
        require(File.ReadAllBytes(Path.Combine(store.Root, "settings.json")).SequenceEqual(original), "Displaying a legacy review does not rewrite its stored source or settings");
        File.WriteAllText(Path.Combine(output, "NOTICE.txt"), "Synthetic curated fact drafts, not a model inference run. Actual native selectors, save, import, review, comparison, source detachment and report export under an empty independent root. Common startup CPU/memory are real; workload history and candidate checks are synthetic. Semantic model quality, physical mouse/dialog interaction and cross-machine accuracy are not established.");
    }
}
