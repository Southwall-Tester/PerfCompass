using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PerfCompass;

public partial class MainWindow
{
    private static async Task EditComparisonCell(DataGrid grid, int row, int column, object value)
    {
        var item = grid.Items[row]; var col = grid.Columns[column];
        grid.SelectedItem = item; grid.CurrentCell = new(item, col); grid.ScrollIntoView(item, col); grid.UpdateLayout();
        if (!grid.BeginEdit()) throw new Exception("Could not edit comparison cell " + row + "/" + column);
        await Task.Delay(10); grid.UpdateLayout();
        var editor = col.GetCellContent(item);
        if (editor is TextBox text) { text.Text = value.ToString(); text.GetBindingExpression(TextBox.TextProperty)?.UpdateSource(); }
        else if (editor is CheckBox check) { check.IsChecked = (bool)value; check.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource(); }
        else if (editor is ComboBox combo) { combo.SelectedValue = value; combo.GetBindingExpression(Selector.SelectedValueProperty)?.UpdateSource(); }
        else throw new Exception("Unexpected comparison cell editor: " + editor?.GetType().Name);
        if (!grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true)) throw new Exception("Comparison cell did not commit.");
    }
    private async Task RunTaskMeasurementUiTests(string output, Action<bool, string> require, Func<string, Task> screenshot, Func<Func<bool>, Task> wait)
    {
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Click(TaskMeasurementNav); var view = TaskMeasurementPage;
        require(view.Visibility == Visibility.Visible && PageTitle.Text == "任务测量", "Task measurement opens through native navigation");
        bool beforeInvalid = paused; Click(view.RunTaskButton);
        require(!view.Running && paused == beforeInvalid && view.LastResult == null, "Invalid task setup starts no process and does not pause monitoring");
        string input = Path.Combine(store.Root, "ui-task.cmd"); File.WriteAllText(input, "@echo off\r\nping -n 2 127.0.0.1 >nul\r\nexit /b 0\r\n", System.Text.Encoding.ASCII);
        view.TaskNameBox.Text = "受控重复任务（流程测试）"; view.ExecutableBox.Text = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        view.ArgumentsBox.Text = "/d /q /c ui-task.cmd"; view.WorkingDirectoryBox.Text = store.Root; view.InputFileBox.Text = input;
        view.SoftwareVersionBox.Text = "Windows cmd controlled fixture"; view.ProcedureBox.Text = "验证重复启动与导入，不衡量真实工作负载性能";
        view.PowerProfileBox.Text = "受控界面流程测试"; view.TimeoutBox.Text = "5";
        int before = store.Read(7).Count(); Click(view.RunTaskButton);
        require(view.Running && paused && !view.TaskInputs.IsEnabled, "Starting repeated task pauses collection and locks its protocol fields");
        Click(PauseButton); require(paused, "Monitor cannot resume while a task measurement is active");
        await view.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var measured = view.LastResult!;
        require(measured.Outcome == "completed" && measured.Attempts.Count == 4 && measured.Attempts.Count(a => a.Warmup) == 1 && measured.MedianSeconds > 0, "Real native task run records three attempts plus a separate warmup");
        require(paused && store.Read(7).Count() == before, "Task test writes no long-term samples and leaves monitoring paused");
        require(view.TaskProgressLabel.Text.Contains("已结束") && view.TaskProgressBar.Visibility == Visibility.Collapsed && !StatusLine.Text.Contains("仍在运行"), "Task progress and global status reach a terminal state after completion");
        string path = Directory.GetFiles(Path.Combine(store.Root, "measurements"), "*.json").Single();
        require(TaskMeasurements.Load(path).ProtocolFingerprint == measured.ProtocolFingerprint && !File.ReadAllText(path).Contains(store.Root.Replace("\\", "\\\\")), "Saved measurement retains its protocol without selected absolute paths");
        view.TaskResultPanel.BringIntoView(); await screenshot("16-task-measurement");
        bool syntheticImport = !TaskMeasurements.Comparable(measured);
        if (syntheticImport) { path = Path.Combine(store.Root, "synthetic-import.json"); TaskMeasurements.Save(path, SelfTests.MeasurementFixture(.9, 1, 1.1)); }
        File.WriteAllText(Path.Combine(output, "measurement-notice.txt"), "Real controlled cmd/ping lifecycle test, not a CPU/GPU performance benchmark. Import fixture synthetic: " + syntheticImport + ". Quotes and candidate identities remain synthetic UI test data.");
        Click(ComparisonNav); await wait(() => ComparisonPage.Visibility == Visibility.Visible);
        var comparisonView = ComparisonPage; Click(comparisonView.AddCriterionButton); int goalRow = comparisonView.CriteriaGrid.Items.Count - 1;
        await EditComparisonCell(comparisonView.CriteriaGrid, goalRow, 1, "受控重复任务耗时"); await EditComparisonCell(comparisonView.CriteriaGrid, goalRow, 2, "task");
        await EditComparisonCell(comparisonView.CriteriaGrid, goalRow, 3, "max"); await EditComparisonCell(comparisonView.CriteriaGrid, goalRow, 4, 5); await EditComparisonCell(comparisonView.CriteriaGrid, goalRow, 5, "秒");
        await EditComparisonCell(comparisonView.CriteriaGrid, goalRow, 8, "核对同一受控任务，非实际购机建议");
        Click(comparisonView.PrepareMeasurementsButton);
        var goal = (PurchaseCriterion)comparisonView.CriteriaGrid.Items[goalRow];
        var observation = comparisonView.Workspace.Observations.First(o => o.CriterionId == goal.Id);
        comparisonView.ObservationsGrid.SelectedItem = observation;
        await RunMeasurementHandoffUiTests(output, require, screenshot, comparisonView, observation, goal, measured);
        var picker = comparisonView.MeasurementPathPicker;
        try
        {
            comparisonView.MeasurementPathPicker = () => path; Click(comparisonView.ImportTaskMeasurementButton);
            require(observation.Measurement != null && observation.Value > 0 && !observation.Comparable && !goal.Reviewed && comparisonView.CoverageConfirm.IsChecked == false, "Measurement imports into selected row without auto-confirming machine or protocol");
            await EditComparisonCell(comparisonView.CriteriaGrid, goalRow, 0, true);
            int observationRow = comparisonView.ObservationsGrid.Items.IndexOf(observation);
            await EditComparisonCell(comparisonView.ObservationsGrid, observationRow, 4, true);
            Click(comparisonView.CompareButton); var evaluated = comparisonView.Result!;
            require(evaluated.Candidates.First(c => c.Id == observation.CandidateId).Checks.Single(c => c.Name == goal.Name).State == "pass", "Reviewed imported repeat evidence reaches the real candidate evaluator");
            string html = comparisonView.ExportHtml(); File.WriteAllText(Path.Combine(output, "imported-measurement-report.html"), html);
            require(html.Contains("中位数") && html.Contains("范围") && html.Contains("协议 SHA256") && CandidateComparison.Load(store.Root).Observations.Any(o => o.Measurement?.ProtocolFingerprint == observation.Measurement!.ProtocolFingerprint), "Imported repeat evidence survives comparison save and HTML export");
            comparisonView.ComparisonResults.BringIntoView(); await screenshot("17-imported-measurement");
            var changed = TaskMeasurements.Load(path); changed = changed with { Recipe = changed.Recipe with { SoftwareVersion = "changed-version" } };
            string other = Path.Combine(store.Root, "other-protocol.json"); TaskMeasurements.Save(other, changed);
            comparisonView.MeasurementPathPicker = () => other; comparisonView.ObservationsGrid.SelectedItem = observation; Click(comparisonView.ImportTaskMeasurementButton);
            require(comparisonView.ComparisonStatus.Text.Contains("协议不同") && observation.Measurement!.Recipe.SoftwareVersion != "changed-version", "A different task protocol cannot silently replace a comparable result");
        }
        finally { comparisonView.MeasurementPathPicker = picker; }
        Click(TaskMeasurementNav); view.ArgumentsBox.Text = "/d /q /c ping -n 20 127.0.0.1";
        require(view.LastResult == null && !view.ExportTaskButton.IsEnabled, "Editing task arguments invalidates the displayed measurement");
        Click(view.RunTaskButton); await Task.Delay(500); Click(view.CancelTaskButton);
        await view.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        require(view.LastResult?.Outcome != "completed" && paused, "Cancel through the native control cannot produce a successful short measurement");
    }
}
