using System.Windows;

namespace PerfCompass;

public static partial class CandidateComparison
{
    public static List<PurchaseCriterion> OutdatedSemanticCriteria(PurchaseWorkspace workspace, AnalysisReport report)
    {
        // A portable workspace can have no local semantic plan. An existing
        // local plan, however, must not silently reuse a changed goal's review.
        if (report.SemanticRequirements == null) return report.SemanticUseSignature.Length > 0
            ? workspace.Criteria.Where(c => c.OriginId.StartsWith("semantic:", StringComparison.Ordinal)).ToList() : [];
        var current = Drafts(report).Where(c => c.OriginId.StartsWith("semantic:", StringComparison.Ordinal)).Select(c => c.OriginId).ToHashSet(StringComparer.Ordinal);
        return workspace.Criteria.Where(c => c.OriginId.StartsWith("semantic:", StringComparison.Ordinal) && !current.Contains(c.OriginId)).ToList();
    }
}
public partial class ComparisonView
{
    private void RefreshSemanticSources(AnalysisReport report)
    {
        int count = CandidateComparison.OutdatedSemanticCriteria(workspace, report).Count;
        SemanticSourceNotice.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SemanticSourceNotice.Text = count > 0 ? $"{count} 项条件的源需求或购机用途需要重新核对。请删除这些条件，或选中后保留为独立目标并重新核对。原始条件与测量保留。" : "";
    }
    private void DetachCriterion_Click(object sender, RoutedEventArgs e)
    {
        if (!Commit() || CriteriaGrid.SelectedItem is not PurchaseCriterion criterion) { ComparisonStatus.Text = "先选择要保留为独立目标的条件。"; return; }
        criterion.OriginId = ""; criterion.Reviewed = false;
        Invalidate(); Bind();
        ComparisonStatus.Text = "所选条件已保留为独立目标，原文与测量仍在。请重新核对目标内容及测量依据。";
    }
}
