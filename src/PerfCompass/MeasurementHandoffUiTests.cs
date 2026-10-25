using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunMeasurementHandoffScenario(string output, Action<bool, string> require,
        Func<string, Task> screenshot, Func<Func<bool>, Task> wait, TaskMeasurementResult measured)
    {
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        require(TaskMeasurementPage.UseMeasurementForCandidateButton.IsEnabled == TaskMeasurements.Comparable(measured),
            "Only complete stable-power current results offer the candidate handoff action");
        if (TaskMeasurementPage.UseMeasurementForCandidateButton.IsEnabled) Click(TaskMeasurementPage.UseMeasurementForCandidateButton);
        else Click(ComparisonNav);
        await wait(() => ComparisonPage.Visibility == Visibility.Visible);
        require(PageTitle.Text == "候选配置", "A completed measurement can reach candidate review through native navigation");
        var view = ComparisonPage;
        Click(view.AddCandidateButton);
        await EditComparisonCell(view.CandidatesGrid, view.CandidatesGrid.Items.Count - 1, 0, "合成本次测量候选");
        var candidate = (PurchaseCandidate)view.CandidatesGrid.Items[^1];
        Click(view.AddCriterionButton);
        int row = view.CriteriaGrid.Items.Count - 1;
        await EditComparisonCell(view.CriteriaGrid, row, 1, "受控任务交接（非硬件性能验证）");
        await EditComparisonCell(view.CriteriaGrid, row, 2, "task");
        await EditComparisonCell(view.CriteriaGrid, row, 3, "max");
        await EditComparisonCell(view.CriteriaGrid, row, 4, 5);
        await EditComparisonCell(view.CriteriaGrid, row, 5, "秒");
        var goal = (PurchaseCriterion)view.CriteriaGrid.Items[row];
        Click(view.PrepareMeasurementsButton);
        var observation = view.Workspace.Observations.Single(o => o.CandidateId == candidate.Id && o.CriterionId == goal.Id);
        view.ObservationsGrid.SelectedItem = observation;
        await RunMeasurementHandoffUiTests(output, require, screenshot, view, observation, goal, measured);
        Click(TaskMeasurementNav);
    }

    private async Task RunMeasurementHandoffUiTests(string output, Action<bool, string> require,
        Func<string, Task> screenshot, ComparisonView view, PurchaseObservation observation,
        PurchaseCriterion goal, TaskMeasurementResult measured)
    {
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var provider = view.CurrentTaskMeasurement;
        bool synthetic = !TaskMeasurements.Comparable(measured);
        var input = synthetic ? SelfTests.MeasurementFixture(.9, 1, 1.1) : measured;
        File.WriteAllText(Path.Combine(output, "measurement-handoff-notice.txt"),
            "Source is a controlled cmd/ping lifecycle measurement, not candidate hardware performance. Stable-power fallback fixture used: " + synthetic);
        try
        {
            view.CurrentTaskMeasurement = () => null;
            Click(view.UseCurrentMeasurementButton);
            require(view.MeasurementHandoffPanel.Visibility == Visibility.Collapsed && observation.Measurement == null
                && view.ComparisonStatus.Text.Contains("没有已结束"), "No current measurement leaves candidate evidence untouched");
            view.CurrentTaskMeasurement = () => input;
            Click(view.UseCurrentMeasurementButton);
            require(view.MeasurementHandoffPanel.Visibility == Visibility.Visible && observation.Measurement == null
                && view.MeasurementHandoffTarget.Text.Contains(observation.CandidateName)
                && view.MeasurementHandoffSource.Text.Contains(input.Machine.Cpu),
                "Current-measurement review shows the selected candidate and actual source without importing early");
            view.MeasurementHandoffEvidence.Text += "\n（本页为受控流程核对，非候选硬件性能实测）";
            await screenshot("18-current-measurement-review");
            Click(view.CancelMeasurementHandoffButton);
            require(view.MeasurementHandoffPanel.Visibility == Visibility.Collapsed && observation.Measurement == null,
                "Cancelling the measurement handoff preserves existing candidate evidence");
            Click(view.UseCurrentMeasurementButton);
            view.SetAnalysisPending("受控范围切换");
            Click(view.ConfirmMeasurementHandoffButton);
            require(view.MeasurementHandoffPanel.Visibility == Visibility.Collapsed && observation.Measurement == null,
                "An analysis scope transition retires the earlier measurement review");
            view.SetContext(store, report!);
            Click(view.UseCurrentMeasurementButton);
            view.ObservationsGrid.SelectedItem = null;
            Click(view.ConfirmMeasurementHandoffButton);
            require(view.MeasurementHandoffPanel.Visibility == Visibility.Collapsed && observation.Measurement == null,
                "Changing the selected result row retires its pending handoff");
            view.ObservationsGrid.SelectedItem = observation;
            Click(view.UseCurrentMeasurementButton);
            goal.Procedure += " changed while reviewing";
            Click(view.ConfirmMeasurementHandoffButton);
            require(view.MeasurementHandoffPanel.Visibility == Visibility.Collapsed && observation.Measurement == null
                && view.ComparisonStatus.Text.Contains("已变化"), "Changed task steps cannot receive an earlier measurement review");
            goal.Procedure = goal.Procedure.Replace(" changed while reviewing", "");
            view.CurrentTaskMeasurement = () => input with { Machine = input.Machine with { PowerSource = "Unknown" } };
            Click(view.UseCurrentMeasurementButton);
            require(view.MeasurementHandoffPanel.Visibility == Visibility.Collapsed && observation.Measurement == null,
                "Unknown power cannot enter the current-measurement handoff");
            view.CurrentTaskMeasurement = () => input;
            goal.Unit = "ms";
            goal.Reviewed = true; observation.Comparable = true;
            view.Workspace.CoverageConfirmed = true; view.CoverageConfirm.IsChecked = true;
            Click(view.UseCurrentMeasurementButton);
            Click(view.ConfirmMeasurementHandoffButton);
            require(observation.Measurement != null && observation.Value == input.MedianSeconds * 1000
                && !observation.Comparable && !goal.Reviewed && !view.Workspace.CoverageConfirmed
                && observation.ProofStamp.Length == 0 && view.MeasurementHandoffPanel.Visibility == Visibility.Collapsed,
                "Explicit handoff converts milliseconds and clears target, comparability and coverage confirmations");
            require(!ReferenceEquals(observation.Measurement!.Attempts, input.Attempts)
                && TaskMeasurementPage.LastResult == measured,
                "Candidate evidence owns its round list and preserves the original measurement-page result");
            view.ObservationsGrid.SelectedItem = observation;
            var before = observation.Measurement;
            Click(view.UseCurrentMeasurementButton);
            Click(view.CancelMeasurementHandoffButton);
            require(ReferenceEquals(observation.Measurement, before),
                "Cancelling a replacement review preserves the existing original round evidence");
            view.CurrentTaskMeasurement = () => input with { Recipe = input.Recipe with { SoftwareVersion = "different-handoff-version" } };
            Click(view.UseCurrentMeasurementButton);
            require(view.MeasurementHandoffPanel.Visibility == Visibility.Collapsed && observation.Measurement == before
                && view.ComparisonStatus.Text.Contains("协议不同"), "A different current-measurement protocol cannot replace existing evidence");
            // Restore the original seconds target for the established file import flow.
            goal.Unit = "秒";
            view.ObservationsGrid.SelectedItem = observation;
        }
        finally { view.CurrentTaskMeasurement = provider; }
    }
}
