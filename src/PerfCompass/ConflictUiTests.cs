using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunConflictUiTests(string output, Action<bool, string> require, Func<string, Task> screenshot, Func<Func<bool>, Task> wait)
    {
        void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Click(ComparisonNav); await wait(() => ComparisonPage.Visibility == Visibility.Visible);
        var view = ComparisonPage;
        var obsolete = CandidateComparison.OutdatedSemanticCriteria(view.Workspace, report!).ToArray();
        int existingGoals = view.Workspace.Criteria.Count;
        string existingEvidence = System.Text.Json.JsonSerializer.Serialize(view.Workspace.Observations);
        foreach (var criterion in obsolete)
        { view.CriteriaGrid.SelectedItem = criterion; Click(view.DetachCriterionButton); }
        require(CandidateComparison.OutdatedSemanticCriteria(view.Workspace, report!).Count == 0
            && view.Workspace.Criteria.Count == existingGoals && obsolete.All(c => !c.Reviewed)
            && System.Text.Json.JsonSerializer.Serialize(view.Workspace.Observations) == existingEvidence,
            "Conflict flow explicitly reviews stale goals left by earlier source changes and retains all original evidence");
        foreach (var spec in new[] { (Name: "内存最低配置", Direction: "min", Target: 64), (Name: "内存最高配置", Direction: "max", Target: 32) })
        {
            Click(view.AddCriterionButton); int row = view.CriteriaGrid.Items.Count - 1;
            await EditComparisonCell(view.CriteriaGrid, row, 1, spec.Name);
            await EditComparisonCell(view.CriteriaGrid, row, 2, "memory");
            await EditComparisonCell(view.CriteriaGrid, row, 3, spec.Direction);
            await EditComparisonCell(view.CriteriaGrid, row, 4, spec.Target);
            await EditComparisonCell(view.CriteriaGrid, row, 5, "GB");
            await EditComparisonCell(view.CriteriaGrid, row, 0, true);
        }
        view.CoverageConfirm.IsChecked = true; Click(view.CompareButton);
        require(view.Result?.Conflicts.Any(c => c.Metric == "memory" && c.Condition.Length == 0) == true
            && view.ComparisonConflicts.Items.Count > 0 && view.ComparisonSummary.Text.Contains("数值冲突")
            && view.Result.Candidates.All(c => c.State == "unknown"), "Native comparison identifies contradictory input instead of rejecting every candidate");
        view.ComparisonConflicts.BringIntoView(); await screenshot("25-conflicting-requirements");
        string html = view.ExportHtml(); File.WriteAllText(Path.Combine(output, "conflict-report.html"), html);
        require(html.Contains("条件数值冲突") && html.Contains("内存最高配置") && html.Contains("64 GB") && html.Contains("32 GB"), "Export preserves named conflict bounds and their units");
        int upperRow = view.CriteriaGrid.Items.Count - 1;
        await EditComparisonCell(view.CriteriaGrid, upperRow, 4, 128);
        require(view.Result == null && view.ComparisonConflicts.Items.Count == 0 && !view.ExportComparisonButton.IsEnabled, "Editing a conflicting bound clears old diagnostics and disables stale export");
        await EditComparisonCell(view.CriteriaGrid, upperRow, 0, true);
        view.CoverageConfirm.IsChecked = true; Click(view.CompareButton);
        require(view.Result != null && view.Result.Conflicts.Length == 0 && view.ComparisonConflicts.Items.Count == 0, "Repaired numerical constraints clear their conflict through the real comparison action");
    }
}
