using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunCandidateTriageUiTests(string output, Action<bool, string> require,
        Func<string, Task> screenshot, Func<Func<bool>, Task> wait)
    {
        void Click(Button b) { b.Focus(); b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
        IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t) yield return t;
                foreach (var nested in Children<T>(child)) yield return nested;
            }
        }
        var view = ComparisonPage;
        if (!paused) Click(PauseButton);
        const string scene = "候选初筛合成任务";
        store.SaveSettings(store.Settings with { FutureNeeds = "", Requirements = null, SemanticRequirements = null });
        var start = DateTimeOffset.Now.AddHours(-6);
        await Task.Run(() =>
        {
            for (int i = 0; i < 60; i++) store.Append(new Sample { Time = start.AddSeconds(i * 10), HardwareId = collector!.HardwareId,
                Scene = scene, Seconds = 10, Active = true, ObservationVersion = ActivityRules.Version,
                Cpu = 12, CoreMax = 30, MemoryTotalGb = 32, MemoryUsedGb = 5, CommitGb = 7,
                Gpu = 3, GpuPeak = 3, GpuMetricSource = "GPU Core", DiskUsedGb = 600, DiskTotalGb = 1000,
                DiskCapacityFieldsPresent = true, MaxDiskPercent = 60 });
        });
        Click(AdviceNav); WindowCombo.SelectedIndex = 0; await RefreshAnalysis();
        AnalysisSceneCombo.SelectedItem = AnalysisSceneCombo.Items.OfType<ComboBoxItem>().Single(c => c.Tag as string == scene);
        await wait(() => report?.SelectedScene == scene && report.WindowDays == 7);
        PurchaseCandidate Product(string name, string gpu, double storage, double price) => new() { Name = name, Cpu = "合成CPU",
            Gpu = gpu, MemoryGb = 32, VramGb = 0, StorageGb = storage, PriceCny = price, Source = "合成报价，非真实商品",
            SpecificationSource = "合成规格，非硬件实测" };
        var discrete = Product("合成独显对照", "独显 · 合成GPU", 1000, 5000);
        var integrated = Product("合成核显方案", "核显 · 合成GPU", 1000, 7000);
        var small = Product("合成小容量方案", "核显 · 合成GPU", 128, 2000);
        PurchaseCriterion Goal(string condition, double value, string direction)
        {
            var c = new PurchaseCriterion { Name = "合成内存目标", Metric = "memory", Target = value, Unit = "GB", Direction = direction,
                Condition = condition, Evidence = "合成已核对条件，不是模型抽取" }; c.Reviewed = true; return c;
        }
        string imported = Path.Combine(output, "triage-input-" + Guid.NewGuid().ToString("N") + ".json");
        CandidateComparison.SaveFile(imported, new() { Candidates = [discrete, integrated, small], Criteria = [Goal("合成训练", 64, "min"), Goal("合成出差", 16, "max")] });
        Click(ComparisonNav); await wait(() => view.Visibility == Visibility.Visible && view.Triage != null);
        var oldImport = view.DraftImportPathProvider;
        try { view.DraftImportPathProvider = () => imported; Click(view.ImportDraftButton); }
        finally { view.DraftImportPathProvider = oldImport; }
        await wait(() => view.Triage?.Cases[0].Candidates.Length == 3);
        require(view.Triage!.Cases[0].Candidates.Select(c => c.Name).SequenceEqual(new[] { discrete.Name, integrated.Name, small.Name }),
            "Actual import orders a lower sourced quote before a higher integrated quote and keeps a below-footprint option separate");
        require(view.Workspace.Criteria.Count == 2 && !view.Workspace.CoverageConfirmed && view.Result == null,
            "Automatic concrete-model screening neither creates criteria nor confirms coverage or a formal result");
        require(view.TriageConditionalExpander.Visibility == Visibility.Visible && view.TriageConditionalCases.Items.Count == 2
            && view.Triage.Cases.Skip(1).All(c => c.Notice.Length == 0),
            "Native triage preserves independent trigger scenarios rather than merging their bounds");
        view.TriageExpander.IsExpanded = true; UpdateLayout();
        var firstName = Children<TextBlock>(view.TriageCases).First(t => t.Text == discrete.Name);
        require(firstName.IsVisible && Children<TextBlock>(view.TriageCases).Any(t => t.Text.Contains("若保留这些占用")),
            "Rendered initial screening shows a concrete model and the conditional storage migration tradeoff");
        for (var parent = VisualTreeHelper.GetParent(view.TriagePanel); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer scroll && scroll.Content is Visual content)
            { scroll.ScrollToVerticalOffset(Math.Max(0, view.TriagePanel.TransformToAncestor(content).Transform(new Point()).Y - 10)); break; }
        await screenshot("91-concrete-candidate-triage");
        var detail = Children<Expander>(view.TriageCases).First(e => e.DataContext is CandidateTriageRow r && r.Id == integrated.Id);
        detail.IsExpanded = true; await RefreshAnalysis();
        require(Children<Expander>(view.TriageCases).Any(e => e.IsExpanded && e.DataContext is CandidateTriageRow r && r.Id == integrated.Id),
            "Ordinary refresh preserves the expanded evidence for the same concrete candidate and scenario");
        foreach (var e in Children<Expander>(view.TriageCases).ToArray()) e.IsExpanded = false;
        await EditComparisonCell(view.CandidatesGrid, 0, 1, 8000);
        await wait(() => view.Triage?.Cases[0].Candidates[0].Name == integrated.Name);
        require(view.Triage!.Cases[0].Candidates[0].Quote.Contains("7000") && !view.Workspace.CoverageConfirmed,
            "A real committed quote edit updates concrete-model ordering without inheriting confirmation");
        view.CandidatesGrid.SelectedItem = view.CandidatesGrid.Items[0];
        view.CandidatesGrid.CurrentCell = new(view.CandidatesGrid.Items[0], view.CandidatesGrid.Columns[0]); view.CandidatesGrid.Focus();
        require(view.CandidatesGrid.BeginEdit(), "A real candidate cell begins editing for stale-screen protection");
        require(view.Triage == null && view.TriageCases.Items.Count == 0 && !view.ExportTriageButton.IsEnabled,
            "Beginning a real edit immediately retires old screening and disables its export");
        view.CandidatesGrid.CancelEdit(DataGridEditingUnit.Cell); view.CandidatesGrid.CancelEdit(DataGridEditingUnit.Row);
        await wait(() => view.Triage != null);
        string exported = Path.Combine(output, "triage-report-" + Guid.NewGuid().ToString("N") + ".html");
        string before = JsonSerializer.Serialize(view.Workspace); var oldPicker = view.TriageExportPathProvider;
        try { view.TriageExportPathProvider = () => exported; Click(view.ExportTriageButton); }
        finally { view.TriageExportPathProvider = oldPicker; }
        require(File.Exists(exported) && File.ReadAllText(exported).Contains("合成训练") && File.ReadAllText(exported).Contains("候选自动初筛")
            && before == JsonSerializer.Serialize(view.Workspace),
            "The actual initial-screen export preserves independent scenarios and does not change reviewed workspace state");
        string blocked = Path.Combine(output, "triage-blocked-" + Guid.NewGuid().ToString("N") + ".html");
        try
        {
            view.TriageExportPathProvider = () => { Click(AdviceNav); WindowCombo.SelectedIndex = 2; return blocked; };
            Click(view.ExportTriageButton);
            require(!File.Exists(blocked) && view.Triage == null && !view.ExportTriageButton.IsEnabled,
                "A scope change inside the export picker prevents writing the old initial-screen report");
        }
        finally { view.TriageExportPathProvider = oldPicker; }
        await wait(() => report?.WindowDays == 30 && report.SelectedScene == scene);
        Click(ComparisonNav); await wait(() => view.Triage?.Scope.Contains("30 天") == true);
        require(view.Triage!.Cases[0].Candidates.Length == 3 && view.Triage.Scope.Contains(scene),
            "The automatic initial screen resumes only with the latest scope and keeps the saved concrete candidates");
        File.WriteAllText(Path.Combine(output, "triage-NOTICE.txt"), "Only synthetic candidates, prices, reviewed conditions and disjoint synthetic task rows under the guarded test root. Real native import, editing, invalidation, source expansion, export and scope-change paths. No real stock, quote accuracy, hardware suitability or model semantic accuracy is asserted.");
    }
}
