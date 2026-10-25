using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PerfCompass;

public partial class ComparisonView
{
    private bool triageRefreshQueued;
    internal CandidateTriageReport? Triage { get; private set; }
    internal Func<string?> TriageExportPathProvider = () =>
    {
        var dialog = new SaveFileDialog { Filter = "HTML 初筛报告|*.html", FileName = "PerfCompass-候选初筛.html" };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    };
    private void RetireTriage(string message)
    {
        // Keep the old occupied space while an editor owns the input. Clearing
        // stale text must not pull the grid being edited out from under the user.
        if (Triage != null && TriagePanel.ActualHeight > 0) TriagePanel.MinHeight = TriagePanel.ActualHeight;
        Triage = null; TriageCases.ItemsSource = null; TriageConditionalCases.ItemsSource = null;
        TriageConditionalExpander.Visibility = Visibility.Collapsed; TriageStatus.Text = message; ExportTriageButton.IsEnabled = false;
        TriageNotice.Text = CandidateTriage.Notice;
    }
    private void RefreshTriage()
    {
        if (contextPending || context == null) { RetireTriage("当前分析更新中，候选草稿可继续编辑。完成后自动初筛。"); return; }
        if (loadFailed) { RetireTriage("原草稿读取失败；先保留原件并核对草稿。当前不生成初筛。"); return; }
        if (new[] { CandidatesGrid, CriteriaGrid, ObservationsGrid }.Any(g => HasEditingCell(g) || HasErrors(g)))
        { RetireTriage("正在编辑；完成有效输入后自动更新初筛。"); return; }
        try
        {
            var scroll = TriageScroll();
            double oldHeight = TriagePanel.ActualHeight, oldOffset = scroll?.VerticalOffset ?? 0;
            bool preserveBelow = TriagePanel.IsVisible && scroll != null
                && TriagePanel.TransformToAncestor(scroll).TransformBounds(new Rect(TriagePanel.RenderSize)).Bottom <= 0;
            var expanded = TriageDetails(TriagePanel).Where(e => e.IsExpanded).Select(TriageDetailKey).Where(k => k.Length > 0).ToHashSet();
            Triage = CandidateTriage.Build(workspace, context);
            TriageCases.ItemsSource = Triage.Cases.Take(1).ToArray();
            TriageConditionalCases.ItemsSource = Triage.Cases.Skip(1).ToArray();
            TriageConditionalExpander.Visibility = Triage.Cases.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
            TriagePanel.UpdateLayout();
            foreach (var detail in TriageDetails(TriagePanel))
                if (expanded.Contains(TriageDetailKey(detail))) detail.IsExpanded = true;
            TriagePanel.MinHeight = 0; TriagePanel.UpdateLayout();
            // If the user is reading below this panel, preserve that reading
            // position when new candidates/conditions change the panel height.
            if (preserveBelow) scroll!.ScrollToVerticalOffset(Math.Max(0, oldOffset + TriagePanel.ActualHeight - oldHeight));
            TriageNotice.Text = Triage.Notice;
            TriageStatus.Text = workspace.Candidates.Count == 0 ? "添加或从规格摘录导入具体候选后，这里会自动给出比较顺序。"
                : Triage.Scope + " · 已按当前需求、已核对的手工条件和可用观测初筛";
            ExportTriageButton.IsEnabled = workspace.Candidates.Count > 0;
        }
        catch (ArgumentException ex) { RetireTriage("请先修正候选草稿：" + ex.Message); }
    }
    private ScrollViewer? TriageScroll()
    {
        for (var parent = VisualTreeHelper.GetParent(TriagePanel); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer scroll) return scroll;
        return null;
    }
    private static IEnumerable<Expander> TriageDetails(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Expander expander) yield return expander;
            foreach (var nested in TriageDetails(child)) yield return nested;
        }
    }
    private static string TriageDetailKey(Expander detail) => detail.DataContext switch
    {
        CandidateTriageRow row => "row:" + row.ScenarioKey + ":" + row.Id,
        CandidateTriageCase item => "case:" + item.ScenarioKey,
        _ => ""
    };
    private void QueueTriageRefresh()
    {
        RetireTriage("正在更新候选初筛。");
        if (triageRefreshQueued) return;
        triageRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => { triageRefreshQueued = false; RefreshTriage(); }));
    }
    private void ExportTriage_Click(object sender, RoutedEventArgs e)
    {
        if (contextPending || context == null || loadFailed || !Commit()) return;
        string? path = TriageExportPathProvider();
        if (path == null) return;
        // The picker may allow a scope change or new input while it is open.
        if (contextPending || context == null || loadFailed || !Commit())
        { ComparisonStatus.Text = "分析或候选已变化，请等待当前分析完成后重新导出初筛。"; return; }
        if (!Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase))
        { ComparisonStatus.Text = "初筛报告请保存为 .html 文件。"; return; }
        try
        {
            var current = CandidateTriage.Build(workspace, context);
            File.WriteAllText(path, CandidateTriage.Html(current), new UTF8Encoding(false));
            RefreshTriage(); ComparisonStatus.Text = "候选初筛已导出；正式条件核对与覆盖确认保持独立。";
        }
        catch (Exception ex) { store?.Log(ex); ComparisonStatus.Text = "初筛导出失败：" + ex.Message; }
    }
}
