using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunComparisonUiTests(string output, Action<bool, string> require, Func<string, Task> screenshot, Func<Func<bool>, Task> wait)
    {
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Task Cell(DataGrid grid, int row, int column, object value) => EditComparisonCell(grid, row, column, value);
        Click(ComparisonNav); await wait(() => ComparisonPage.Visibility == Visibility.Visible);
        var view = ComparisonPage;
        require(PageTitle.Text == "候选配置" && view.Workspace.Candidates.Count == 0, "Candidate comparison opens through native navigation with empty user input");
        for (int i = 0; i < 3; i++) Click(view.AddCandidateButton);
        for (int i = 0; i < 3; i++)
        {
            await Cell(view.CandidatesGrid, i, 0, new[] { "轻量方案", "均衡方案", "高配方案" }[i]);
            await Cell(view.CandidatesGrid, i, 1, new[] { 6000, 8500, 10000 }[i]);
            await Cell(view.CandidatesGrid, i, 2, i == 0 ? 32 : 64);
            await Cell(view.CandidatesGrid, i, 5, "合成 CPU " + i);
            await Cell(view.CandidatesGrid, i, 6, "合成 GPU " + i);
            await Cell(view.CandidatesGrid, i, 7, "合成报价单 2026-10-22（非实际商品）");
        }
        Click(view.ImportCriteriaButton);
        require(view.CriteriaGrid.Items.Count > 0 && view.CriteriaGrid.Items.Cast<PurchaseCriterion>().All(c => !c.Reviewed), "Confirmed needs and observed tasks import as unreviewed comparison conditions");
        require(view.CriteriaGrid.Items.Cast<PurchaseCriterion>().Any(c => c.OriginId.StartsWith("semantic:") && c.Metric == "memory" && c.Target == 64)
            && view.CriteriaGrid.Items.Cast<PurchaseCriterion>().Any(c => c.Condition == "选上地质课"), "Reviewed natural language reaches numeric candidate constraints while retaining conditional workload");
        Click(view.AddCriterionButton); int memory = view.CriteriaGrid.Items.Count - 1;
        await Cell(view.CriteriaGrid, memory, 1, "内存至少64GB"); await Cell(view.CriteriaGrid, memory, 2, "memory");
        await Cell(view.CriteriaGrid, memory, 4, 64); await Cell(view.CriteriaGrid, memory, 5, "GB");
        Click(view.AddCriterionButton); int task = view.CriteriaGrid.Items.Count - 1;
        await Cell(view.CriteriaGrid, task, 1, "统一项目编译"); await Cell(view.CriteriaGrid, task, 2, "task"); await Cell(view.CriteriaGrid, task, 3, "max");
        await Cell(view.CriteriaGrid, task, 4, 120); await Cell(view.CriteriaGrid, task, 5, "秒");
        await Cell(view.CriteriaGrid, task, 8, "同项目、工具链、清空缓存、电源模式；三次编译取中位数");
        for (int i = 0; i < view.CriteriaGrid.Items.Count; i++) await Cell(view.CriteriaGrid, i, 0, true);
        Click(view.PrepareMeasurementsButton);
        for (int i = 0; i < view.ObservationsGrid.Items.Count; i++)
        {
            var observation = (PurchaseObservation)view.ObservationsGrid.Items[i];
            if (observation.CriterionId == ((PurchaseCriterion)view.CriteriaGrid.Items[task]).Id) await Cell(view.ObservationsGrid, i, 2, 100);
            else await Cell(view.ObservationsGrid, i, 3, "pass");
            await Cell(view.ObservationsGrid, i, 5, "合成测试记录；同一任务及版本与设置");
            await Cell(view.ObservationsGrid, i, 4, true);
        }
        view.CoverageConfirm.IsChecked = true; Click(view.CompareButton);
        require(view.Result?.Candidates.Count == 3 && view.Result.Candidates[0].State == "fail" && view.Result.Candidates.Skip(1).All(c => c.State == "pass"), "Real comparison grids evaluate insufficient capacity and tested candidates");
        require(view.Result!.Summary.Contains("均衡方案") && view.Result.Summary.Contains("1500"), "Comparison shows savings only between passing candidates");
        require(view.Result.ScenarioSummaries.Single().Contains("选上地质课") && view.Result.ScenarioSummaries.Single().Contains("8500"), "Conditional workload receives its own scenario comparison in the native result");
        var reopened = CandidateComparison.Load(store.Root);
        require(reopened.Candidates.Count == 3 && CandidateComparison.Evaluate(reopened, report!).Candidates[1].State == "pass", "Candidate quotes and reviewed test evidence survive reopening");
        File.WriteAllText(Path.Combine(output, "comparison-report.html"), view.ExportHtml());
        view.ComparisonSummary.BringIntoView(); await screenshot("13-candidate-comparison");
        view.ComparisonResults.BringIntoView(); await screenshot("15-comparison-decisions");
        view.CandidatesGrid.ScrollIntoView(view.CandidatesGrid.Items[0], view.CandidatesGrid.Columns[0]); view.CandidatesGrid.BringIntoView(); await screenshot("14-candidate-entry");
        view.CoverageConfirm.IsChecked = false;
        require(view.Result == null && !view.ExportComparisonButton.IsEnabled, "Removing coverage confirmation invalidates previous purchase result and export");
        view.CoverageConfirm.IsChecked = true; Click(view.CompareButton);
        await Cell(view.CandidatesGrid, 1, 2, 16);
        require(view.Result == null && view.CoverageConfirm.IsChecked == false, "Editing a candidate invalidates the displayed comparison");
        view.CoverageConfirm.IsChecked = true; Click(view.CompareButton);
        require(view.Result!.Candidates[1].State == "fail" && view.Result.Candidates[1].Checks.Any(c => c.State == "unknown"), "Changed candidate cannot inherit old task verification");
        Click(SettingsNav); FutureBox.Text = "新增三维扫描任务"; Click(SaveRequirementsButton); await wait(() => report!.FutureNeeds == "新增三维扫描任务");
        Click(ComparisonNav); await wait(() => ComparisonPage.Visibility == Visibility.Visible);
        require(view.Result == null, "Changing confirmed demand invalidates an open comparison workspace");
    }
}
