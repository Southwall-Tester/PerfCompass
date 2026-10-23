using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;

namespace PerfCompass;

public partial class ComparisonView : UserControl
{
    private LocalStore? store;
    private AnalysisReport? context;
    private PurchaseWorkspace workspace = new();
    private PurchaseComparison? comparison;
    private bool updating, loaded, loadFailed;
    internal PurchaseWorkspace Workspace => workspace;
    internal PurchaseComparison? Result => comparison;
    internal Action? OpenTaskMeasurement;
    internal Func<string?> MeasurementPathPicker = () => { var dialog = new OpenFileDialog { Filter = "任务测量 JSON|*.json", CheckFileExists = true }; return dialog.ShowDialog() == true ? dialog.FileName : null; };
    public ComparisonView()
    {
        InitializeComponent();
        foreach (var grid in new[] { CandidatesGrid, CriteriaGrid, ObservationsGrid })
        {
            foreach (var column in grid.Columns.OfType<DataGridTextColumn>()) column.ElementStyle = (Style)FindResource("TableText");
            grid.BeginningEdit += (_, _) => Invalidate();
        }
        // Display-mode combo boxes also emit selection events when WPF creates
        // their bindings during layout. Only actual cell editing invalidates a
        // result; scrolling or realizing a row must not erase it.
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, e) => { if (IsEditingCell(e.OriginalSource)) Invalidate(); }));
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, e) => { if (IsEditingCell(e.OriginalSource)) Invalidate(); }));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, e) => { if (IsEditingCell(e.OriginalSource)) Invalidate(); }));
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, e) => { if (e.OriginalSource is ComboBox && IsEditingCell(e.OriginalSource)) Invalidate(); }));
        CoverageConfirm.Checked += (_, _) => { if (!updating) DropResult(); };
        CoverageConfirm.Unchecked += (_, _) => { if (!updating) DropResult(); };
    }
    private static bool IsEditingCell(object source)
    {
        for (var current = source as DependencyObject; current != null; current = VisualTreeHelper.GetParent(current))
            if (current is DataGridCell cell) return cell.IsEditing;
        return false;
    }
    internal void RefreshLoadedContext(LocalStore localStore, AnalysisReport report)
    {
        if (loaded) SetContext(localStore, report);
    }
    internal void SetContext(LocalStore localStore, AnalysisReport report)
    {
        store = localStore; context = report;
        if (!loaded)
        {
            loaded = true;
            try { workspace = CandidateComparison.Load(store.Root); Bind(); }
            catch (Exception e) { loadFailed = true; store.Log(e); ComparisonStatus.Text = "比较文件无法读取，已保留原文件。请检查 data/comparison.json；当前禁止覆盖保存。"; CompareButton.IsEnabled = false; }
        }
        RefreshContextStatus(report);
        if ((workspace.CoverageConfirmed || CoverageConfirm.IsChecked == true) && workspace.ContextStamp != CandidateComparison.Stamp(report)
            || comparison != null && comparison.ContextStamp != CandidateComparison.Stamp(report)) Invalidate();
    }
    private void RefreshContextStatus(AnalysisReport report)
    {
        ContextStatus.Text = $"最近 {report.WindowDays} 天 / {report.SelectedScene ?? "全部场景"} · " + (workspace.ContextStamp == CandidateComparison.Stamp(report) ? "条件范围与当前已确认需求一致。" : "当前需求或近期任务尚未纳入这份比较，请导入并核对。");
        if (report.HistoryQuality.HasIssues) ContextStatus.Text += " 历史读取有异常；请补核无法读取的任务，比较仅限已核对条件。";
    }
    private void Bind()
    {
        updating = true;
        try
        {
            CandidatesGrid.ItemsSource = new ObservableCollection<PurchaseCandidate>(workspace.Candidates);
            CriteriaGrid.ItemsSource = new ObservableCollection<PurchaseCriterion>(workspace.Criteria);
            ObservationsGrid.ItemsSource = new ObservableCollection<PurchaseObservation>(workspace.Observations);
            foreach (var observation in workspace.Observations)
            {
                var candidate = workspace.Candidates.Single(c => c.Id == observation.CandidateId);
                var criterion = workspace.Criteria.Single(c => c.Id == observation.CriterionId);
                observation.SignatureFactory = () => CandidateComparison.ObservationStamp(candidate, criterion);
            }
            CoverageConfirm.IsChecked = workspace.CoverageConfirmed;
        }
        finally { updating = false; }
    }
    private void Invalidate()
    {
        if (updating || !loaded) return;
        updating = true;
        try { CoverageConfirm.IsChecked = false; workspace.CoverageConfirmed = false; DropResult(); }
        finally { updating = false; }
    }
    private void DropResult() { comparison = null; ComparisonResults.ItemsSource = null; ComparisonTradeoffs.ItemsSource = null; ComparisonScenarios.ItemsSource = null; ComparisonConflicts.ItemsSource = null; ComparisonSummary.Text = ""; ExportComparisonButton.IsEnabled = false; }
    private bool Commit()
    {
        foreach (var grid in new[] { CandidatesGrid, CriteriaGrid, ObservationsGrid })
            if (!grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true) || HasErrors(grid)) { ComparisonStatus.Text = "请先修正表格中格式不正确的数值。"; return false; }
        workspace.Candidates = CandidatesGrid.Items.Cast<PurchaseCandidate>().ToList();
        workspace.Criteria = CriteriaGrid.Items.Cast<PurchaseCriterion>().ToList();
        workspace.Observations = ObservationsGrid.Items.Cast<PurchaseObservation>().ToList();
        return true;
    }
    private static bool HasErrors(DependencyObject parent)
    {
        if (Validation.GetHasError(parent)) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) if (HasErrors(VisualTreeHelper.GetChild(parent, i))) return true;
        return false;
    }
    private void AddCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (!Commit()) return;
        if (workspace.Candidates.Count >= 3) { ComparisonStatus.Text = "最多比较三套配置。"; return; }
        string name = Enumerable.Range(1, 100).Select(i => "候选 " + i).First(n => workspace.Candidates.All(c => c.Name != n));
        workspace.Candidates.Add(new() { Name = name }); Invalidate(); Bind();
    }
    private void RemoveCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (!Commit() || CandidatesGrid.SelectedItem is not PurchaseCandidate candidate) return;
        workspace.Candidates.Remove(candidate); workspace.Observations.RemoveAll(o => o.CandidateId == candidate.Id); Invalidate(); Bind();
    }
    private void ImportCriteria_Click(object sender, RoutedEventArgs e)
    {
        if (!Commit() || context == null) return;
        var drafts = CandidateComparison.Drafts(context);
        // Keep manual edits and measurements; add only genuinely new source/name pairs.
        var additions = drafts.Where(c => !workspace.Criteria.Any(old => old.OriginId == c.OriginId)).ToList();
        if (workspace.Criteria.Count + additions.Count > 40) { ComparisonStatus.Text = "导入后超过 40 项，请先整理现有条件。"; return; }
        workspace.Criteria.AddRange(additions); workspace.ContextStamp = CandidateComparison.Stamp(context);
        foreach (var c in workspace.Criteria) c.Reviewed = false;
        Invalidate(); Bind(); RefreshContextStatus(context); ComparisonStatus.Text = $"新增 {additions.Count} 项。原有条件与结果已保留；需求变化后请逐项重新核对，包括已经不适用的旧条件。";
    }
    private void AddCriterion_Click(object sender, RoutedEventArgs e)
    {
        if (!Commit()) return;
        if (workspace.Criteria.Count >= 40) { ComparisonStatus.Text = "最多 40 项条件。"; return; }
        workspace.Criteria.Add(new()); Invalidate(); Bind();
    }
    private void RemoveCriterion_Click(object sender, RoutedEventArgs e)
    {
        if (!Commit() || CriteriaGrid.SelectedItem is not PurchaseCriterion criterion) return;
        workspace.Criteria.Remove(criterion); workspace.Observations.RemoveAll(o => o.CriterionId == criterion.Id); Invalidate(); Bind();
    }
    private void PrepareMeasurements_Click(object sender, RoutedEventArgs e)
    {
        if (!Commit()) return;
        var pairs = workspace.Candidates.SelectMany(candidate => workspace.Criteria.Where(c => c.Metric is "check" or "task").Select(c => (candidate, criterion: c))).ToList();
        workspace.Observations = pairs.Select(pair =>
        {
            var found = workspace.Observations.FirstOrDefault(o => o.CandidateId == pair.candidate.Id && o.CriterionId == pair.criterion.Id)
                ?? new() { CandidateId = pair.candidate.Id, CriterionId = pair.criterion.Id };
            found.CandidateName = pair.candidate.Name; found.CriterionName = pair.criterion.Name; return found;
        }).ToList();
        Invalidate(); Bind(); ComparisonStatus.Text = "结果行已更新。请填写原始测量或核对依据，并确认各候选测试条件一致。";
    }
    private void OpenTaskMeasurement_Click(object sender, RoutedEventArgs e) => OpenTaskMeasurement?.Invoke();
    private void ImportTaskMeasurement_Click(object sender, RoutedEventArgs e)
    {
        if (!Commit() || ObservationsGrid.SelectedItem is not PurchaseObservation observation) { ComparisonStatus.Text = "先在结果表中选择要导入的候选与任务行。"; return; }
        var criterion = workspace.Criteria.Single(c => c.Id == observation.CriterionId);
        if (criterion.Metric != "task" || criterion.Unit is not ("秒" or "s" or "seconds" or "毫秒" or "ms")) { ComparisonStatus.Text = "所选行需为实际任务测量，单位设为秒/s 或毫秒/ms。"; return; }
        string? path = MeasurementPathPicker(); if (path == null) return;
        try
        {
            var result = TaskMeasurements.Load(path);
            if (!TaskMeasurements.Comparable(result)) { ComparisonStatus.Text = "测量未完整成功，或电源状态改变/未知；请检查后重新测量。"; return; }
            if (criterion.MeasurementProtocol.Length > 0 && criterion.MeasurementProtocol != result.ProtocolFingerprint) { ComparisonStatus.Text = "程序、输入或测试协议不同，不能并入同一目标。请新建目标保留不同协议的测量。"; return; }
            criterion.MeasurementProtocol = result.ProtocolFingerprint; criterion.Reviewed = false;
            observation.Measurement = result; observation.Value = result.MedianSeconds * (criterion.Unit is "毫秒" or "ms" ? 1000 : 1);
            observation.Evidence = TaskMeasurements.Evidence(result); observation.Comparable = false; observation.ProofStamp = "";
            Invalidate(); Bind();
            ComparisonStatus.Text = $"已导入 {result.Recipe.Name}。来源机器：{result.Machine.Cpu} / {result.Machine.Gpu}。请核对它对应所选候选、任务步骤与实际产物，重新确认目标及可比性。";
        }
        catch (ArgumentException ex) { ComparisonStatus.Text = ex.Message; }
        catch (System.Text.Json.JsonException) { ComparisonStatus.Text = "测量文件格式不正确，未导入。"; }
        catch (Exception ex) { store?.Log(new InvalidOperationException("Measurement import failed: " + ex.GetType().Name)); ComparisonStatus.Text = "无法读取测量文件，未导入。"; }
    }
    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (!Commit() || context == null || store == null || loadFailed) return;
        try
        {
            workspace.CoverageConfirmed = CoverageConfirm.IsChecked == true;
            CandidateComparison.Save(store.Root, workspace);
            if (workspace.Candidates.Count == 0) { DropResult(); ComparisonStatus.Text = "草稿已保存；添加一套候选即可核对目标，添加更多候选后可比较报价。"; return; }
            comparison = CandidateComparison.Evaluate(workspace, context);
            ComparisonSummary.Text = comparison.Summary;
            ComparisonConflicts.ItemsSource = comparison.Conflicts;
            ComparisonTradeoffs.ItemsSource = comparison.Tradeoffs;
            ComparisonScenarios.ItemsSource = comparison.ScenarioSummaries;
            ComparisonResults.ItemsSource = comparison.Candidates.Select(a => new { a.Name, StatusLabel = a.State == "pass" ? "必需条件通过 · 条件需求另行核对" : a.State == "fail" ? "存在不满足的必需条件" : "待验证，暂不能确认满足需求", StatusBrush = a.State == "pass" ? "#007F73" : a.State == "fail" ? "#BC7132" : "#677A87", Checks = a.Checks.Select(c => new { c.Name, c.EvidenceKind, c.EvidenceLabel, Reading = c.EvidenceLabel + " · " + (c.State == "pass" ? "满足：" : c.State == "fail" ? "不满足：" : "待验证：") + c.Reading, c.Evidence, ConditionLabel = c.Condition.Length > 0 ? "触发条件：" + c.Condition : "必需条件" }) }).ToList();
            ExportComparisonButton.IsEnabled = true; ComparisonStatus.Text = "已保存目标核对结果；各项已标明规格资料、人工记录或重复测量依据。";
        }
        catch (ArgumentException ex) { ComparisonStatus.Text = ex.Message; }
        catch (Exception ex) { store.Log(ex); ComparisonStatus.Text = "比较或保存失败，请检查数据目录权限。"; }
    }
    internal string ExportHtml()
    {
        if (comparison == null) throw new InvalidOperationException("请先重新比较。");
        return CandidateComparison.Html(workspace, comparison);
    }
    private void ExportComparison_Click(object sender, RoutedEventArgs e)
    {
        if (comparison == null) return;
        var dialog = new SaveFileDialog { Filter = "HTML 报告|*.html", FileName = "PerfCompass-候选比较.html" };
        if (dialog.ShowDialog() != true) return;
        try { File.WriteAllText(dialog.FileName, ExportHtml(), new System.Text.UTF8Encoding(false)); ComparisonStatus.Text = "比较报告已导出。"; }
        catch (Exception ex) { store?.Log(ex); ComparisonStatus.Text = "导出失败，请检查目标目录。"; }
    }
}
