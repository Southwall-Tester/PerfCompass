using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunTaskFileProtectionUiTests(string output, Action<bool, string> require, Func<string, Task> screenshot, Func<Func<bool>, Task> wait)
    {
        void Click(Button b) { b.Focus(); b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        string History() => string.Join("|", Directory.GetFiles(store.Root, "samples-*.jsonl").Order().Select(path => Path.GetFileName(path) + ":" + Hash(path)));
        if (!paused) Click(PauseButton);
        string history = History();
        Click(TaskMeasurementNav); var view = TaskMeasurementPage;
        require(view.IsVisible && paused && !view.Running && view.LastResult == null, "File protection flow uses the real task navigation with empty guarded data");
        File.WriteAllText(Path.Combine(output, "NOTICE.txt"), "Synthetic command and input files in the isolated test directory. Real Windows sharing denials and owned command processes; not hardware performance or artifact correctness. The embedded old measurement was written by 0.8.20; the later legacy plan is a version-1.0 structural fixture built from current-machine task settings, not a historic physical run. Native controls and cell editors are used with deterministic picker delegates, not OS file-dialog or mouse automation. File permissions and attributes are not changed.");
        var request = TaskInputFixtures.Create(Path.Combine(store.Root, "task-file-fixture"), TaskInputFixtures.RestoreScript);
        void SelectFiles()
        {
            view.ExecutablePathProvider = () => request.Executable; view.WorkingDirectoryPathProvider = () => request.WorkingDirectory; view.InputPathProvider = () => request.InputFile;
            Click(view.ChooseExecutableButton); Click(view.ChooseWorkingDirectoryButton); Click(view.ChooseInputButton);
        }
        var oldExe = view.ExecutablePathProvider; var oldWorking = view.WorkingDirectoryPathProvider; var oldInput = view.InputPathProvider;
        var oldImport = view.PlanImportPathProvider; var oldExport = view.MeasurementExportPathProvider; var oldPlanExport = view.PlanExportPathProvider;
        async Task Capture(FrameworkElement anchor, string name)
        {
            await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.ContextIdle);
            anchor.BringIntoView(); await Task.Delay(150); UpdateLayout();
            var surface = (FrameworkElement)Content; var bounds = anchor.TransformToAncestor(surface).TransformBounds(new Rect(anchor.RenderSize));
            var visible = bounds; visible.Intersect(new Rect(surface.RenderSize));
            for (var p = VisualTreeHelper.GetParent(anchor); p != null && p != surface; p = VisualTreeHelper.GetParent(p))
                if (p is FrameworkElement f && (f.ClipToBounds || f is ScrollContentPresenter)) visible.Intersect(f.TransformToAncestor(surface).TransformBounds(new Rect(f.RenderSize)));
            await screenshot(name);
            require(anchor.IsVisible && !visible.IsEmpty && visible.Width > 8 && visible.Height > 8 && visible.Width >= bounds.Width - .5 && visible.Height >= bounds.Height - .5, "Complete file-protection state is visible before capture: " + name);
        }
        try
        {
            SelectFiles(); view.TaskNameBox.Text = request.Name; view.ArgumentsBox.Text = request.Arguments; view.SoftwareVersionBox.Text = request.SoftwareVersion;
            view.ProcedureBox.Text = request.Procedure; view.PowerProfileBox.Text = "受控文件完整性验证，非性能比较"; view.WarmupCheck.IsChecked = false; view.TimeoutBox.Text = "10";
            require(view.InputProtectionHint.Text.Contains("输出请写到其他文件") && view.InputProtectionHint.Text.Contains("不会自动保护"), "Native input instructions explain output separation and manifest scope");
            await Capture(view.InputProtectionHint, "input-file-scope");
            using (var writer = new FileStream(request.InputFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                string blockedPlan = Path.Combine(output, "blocked-plan.json"); view.PlanExportPathProvider = () => blockedPlan;
                Click(view.ExportTaskPlanButton); await view.Completion.WaitAsync(TimeSpan.FromSeconds(20));
                require(!File.Exists(blockedPlan) && view.TaskPlanStatus.Text.Contains("只读") && !File.Exists(Path.Combine(request.WorkingDirectory, "first.flag")), "Plan-save preflight explains a conflicting writer without saving or executing a task");
                Click(view.RunTaskButton); await view.Completion.WaitAsync(TimeSpan.FromSeconds(20));
                require(view.LastResult == null && view.TaskStatus.Text.Contains("尚未启动任务") && view.TaskStatus.Text.Contains("只读") && !File.Exists(Path.Combine(request.WorkingDirectory, "first.flag")), "An existing writer blocks native execution before any task sentinel or result");
                require(!view.Running && view.RunTaskButton.IsEnabled && view.TaskInputs.IsEnabled && paused, "Blocked preflight restores native controls and leaves monitoring paused");
                await Capture(view.TaskStatus, "input-writer-blocked");
            }
            Click(view.RunTaskButton); await view.Completion.WaitAsync(TimeSpan.FromSeconds(20));
            require(view.LastResult is { Outcome: "failed", Attempts.Count: 1 } && view.LastResult.Attempts[0].ExitCode == 7
                && File.ReadAllText(request.InputFile) == "alpha\r\n" && !File.Exists(Path.Combine(request.WorkingDirectory, "second-saw.txt")), "The actual restore-at-end task fails its first denied write through native execution");
            require(!view.HasUnsavedResult && view.TaskResultPanel.IsVisible && !TaskMeasurements.Comparable(view.LastResult!), "Failed guarded run preserves its saved raw attempt without a comparable result");
            using (var access = new FileStream(request.InputFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
            require(true, "The failed native run releases the selected input for subsequent editing");
            await Capture(view.TaskResultPanel, "mutating-input-failed");
            File.WriteAllText(Path.Combine(request.WorkingDirectory, "readonly.cmd"), "@echo off\r\necho done>read-output.txt\r\nexit /b 0\r\n");
            view.ArgumentsBox.Text = "/d /q /c readonly.cmd"; view.SoftwareVersionBox.Text = "controlled read-only task"; view.ProcedureBox.Text = "固定输入，只读运行；输出写入read-output.txt";
            Click(view.RunTaskButton); await view.Completion.WaitAsync(TimeSpan.FromSeconds(20));
            var current = view.LastResult!;
            require(current is { Version: TaskMeasurements.Version, Outcome: "completed", Attempts.Count: 3 } && File.Exists(Path.Combine(request.WorkingDirectory, "read-output.txt")), "A separate read-only task completes all native repeats using measurement 1.1");
            require(view.TaskEvidenceLabel.Text.Contains("方法 1.1") && !view.HasUnsavedResult && History() == history, "Current method evidence is visible and task activity does not enter monitoring history");
            ((Expander)view.TaskEvidenceLabel.Parent).IsExpanded = true;
            await Capture(view.TaskResultPanel, "protected-task-completed");
            string currentPath = Path.Combine(output, "current-measurement.json"); view.MeasurementExportPathProvider = () => currentPath; Click(view.ExportTaskButton);
            require(TaskMeasurements.Load(currentPath).Version == TaskMeasurements.Version && TaskMeasurements.Load(currentPath).ProtocolFingerprint == current.ProtocolFingerprint, "Native export retains the guarded method and protocol fingerprint");

            string oldPath = Path.Combine(output, "old-measurement-original.json"); File.WriteAllText(oldPath, TaskInputFixtures.Resource("PerfCompass.LegacyInputMeasurement.json")); string oldHash = Hash(oldPath);
            Click(ComparisonNav); await wait(() => ComparisonPage.IsVisible); var compare = ComparisonPage;
            Click(compare.AddCandidateButton); await EditComparisonCell(compare.CandidatesGrid, 0, 0, "合成核对候选"); await EditComparisonCell(compare.CandidatesGrid, 0, 2, 32); await EditComparisonCell(compare.CandidatesGrid, 0, 7, "合成规格");
            async Task<int> AddGoal(string name, string metric, string unit, double target)
            {
                Click(compare.AddCriterionButton); int row = compare.Workspace.Criteria.Count - 1;
                await EditComparisonCell(compare.CriteriaGrid, row, 1, name); await EditComparisonCell(compare.CriteriaGrid, row, 2, metric);
                await EditComparisonCell(compare.CriteriaGrid, row, 4, target); await EditComparisonCell(compare.CriteriaGrid, row, 5, unit);
                if (metric == "task") { await EditComparisonCell(compare.CriteriaGrid, row, 3, "max"); await EditComparisonCell(compare.CriteriaGrid, row, 8, "核对合成任务协议与实际产物"); }
                return row;
            }
            int memory = await AddGoal("独立内存条件", "memory", "GB", 16), legacy = await AddGoal("旧测量任务", "task", "秒", 999);
            Click(compare.PrepareMeasurementsButton);
            var oldPicker = compare.MeasurementPathPicker;
            try
            {
                compare.MeasurementPathPicker = () => oldPath; compare.ObservationsGrid.SelectedItem = compare.Workspace.Observations.Single(); Click(compare.ImportTaskMeasurementButton);
                require(compare.Workspace.Observations.Single().Measurement?.Version == TaskMeasurements.LegacyVersion && compare.ComparisonStatus.Text.Contains("仅作记录"), "Actual old-writer measurement imports with a native record-only warning");
                await EditComparisonCell(compare.CriteriaGrid, memory, 0, true); await EditComparisonCell(compare.CriteriaGrid, legacy, 0, true); await EditComparisonCell(compare.ObservationsGrid, 0, 4, true);
                compare.CoverageConfirm.IsChecked = true; Click(compare.CompareButton);
                var checks = compare.Result!.Candidates.Single().Checks;
                require(checks.Single(c => c.Name == "独立内存条件").State == "pass" && checks.Single(c => c.Name == "旧测量任务").State == "unknown"
                    && checks.Single(c => c.Name == "旧测量任务").Reading.Contains("旧测量"), "Rechecking legacy evidence cannot create a protected pass or erase a separate specification pass");
                require(Hash(oldPath) == oldHash && compare.ExportHtml().Contains("旧测量仅校验"), "Original legacy bytes and explicit report limits survive native comparison");
                compare.MeasurementPathPicker = () => currentPath; Click(compare.ImportTaskMeasurementButton);
                require(compare.ComparisonStatus.Text.Contains("协议不同") && compare.Workspace.Observations.Single().Measurement?.Version == TaskMeasurements.LegacyVersion, "A different current protocol cannot silently replace an old bound goal");
            }
            finally { compare.MeasurementPathPicker = oldPicker; }
            Click(TaskMeasurementNav); view.PlanImportPathProvider = () => oldPath; Click(view.ImportTaskPlanButton);
            require(view.BoundPlan is { Version: TaskPlans.Version, UpgradedFromLegacy: true } && view.LastResult == null && view.ExecutableBox.Text == ""
                && view.InputFileBox.Text == "" && view.PlanEnvironmentConfirmedCheck.IsChecked != true && view.TaskPlanStatus.Text.Contains("旧版"), "Actual old measurement reuses a current-method plan without identity elapsed results or environment confirmation");
            await Capture(view.TaskPlanPanel, "legacy-plan-review-required");
            Click(view.RunTaskButton);
            require(view.LastResult == null && !view.Running && view.TaskStatus.Text.Contains("请先核对本机"), "Legacy plan import cannot start another task without environment review");
            var legacyPlan = new TaskPlan(TaskPlans.LegacyVersion, current.Recipe, current.Machine.PowerSource, current.Machine.PowerProfile);
            string planPath = Path.Combine(output, "structural-legacy-plan.json"); TaskPlans.Save(planPath, legacyPlan); string planHash = Hash(planPath);
            view.PlanImportPathProvider = () => planPath; Click(view.ImportTaskPlanButton); SelectFiles();
            require(view.BoundPlan!.ProtocolFingerprint != legacyPlan.ProtocolFingerprint && view.PlanEnvironmentConfirmedCheck.IsChecked != true
                && view.SoftwareVersionBox.Text == current.Recipe.SoftwareVersion, "Legacy plan conversion preserves declared settings but separates the measurement method and clears confirmation");
            view.PlanEnvironmentConfirmedCheck.IsChecked = true; Click(view.RunTaskButton); await view.Completion.WaitAsync(TimeSpan.FromSeconds(20));
            require(view.LastResult is { Outcome: "completed", Version: TaskMeasurements.Version } && view.LastResult.ProtocolFingerprint == view.BoundPlan.ProtocolFingerprint
                && Hash(planPath) == planHash && Hash(oldPath) == oldHash, "A reviewed structural legacy plan reruns under the new method without rewriting either source file");
            require(History() == history && paused && !view.HasUnsavedResult, "All native integrity operations finish paused with saved results and unchanged monitoring history");
        }
        finally
        {
            view.ExecutablePathProvider = oldExe; view.WorkingDirectoryPathProvider = oldWorking; view.InputPathProvider = oldInput;
            view.PlanImportPathProvider = oldImport; view.MeasurementExportPathProvider = oldExport; view.PlanExportPathProvider = oldPlanExport;
        }
    }
}
