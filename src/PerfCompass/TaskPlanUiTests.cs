using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunTaskPlanUiTests(string output, Action<bool, string> require,
        Func<string, Task> screenshot, Func<Func<bool>, Task> wait)
    {
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        bool InViewport(FrameworkElement element)
        {
            if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
            var surface = (FrameworkElement)Content;
            var bounds = element.TransformToAncestor(surface).TransformBounds(new Rect(element.RenderSize));
            var visible = bounds;
            visible.Intersect(new Rect(surface.RenderSize));
            for (var ancestor = VisualTreeHelper.GetParent(element); ancestor != null && ancestor != surface;
                ancestor = VisualTreeHelper.GetParent(ancestor))
                if (ancestor is FrameworkElement frame && (frame.ClipToBounds || frame is ScrollContentPresenter))
                    visible.Intersect(frame.TransformToAncestor(surface).TransformBounds(new Rect(frame.RenderSize)));
            return !visible.IsEmpty && visible.Width > 8 && visible.Height > 8
                && visible.Width >= bounds.Width - .5 && visible.Height >= bounds.Height - .5;
        }
        async Task<bool> Capture(string name, FrameworkElement anchor, params FrameworkElement[] labels)
        {
            UpdateLayout();
            double bottom = labels.Select(label => label.TransformToVisual(anchor)
                .TransformBounds(new Rect(label.RenderSize)).Bottom).DefaultIfEmpty(0).Max();
            anchor.BringIntoView(new Rect(0, 0, anchor.ActualWidth, bottom + 12));
            for (var ancestor = VisualTreeHelper.GetParent(anchor); ancestor != null;
                ancestor = VisualTreeHelper.GetParent(ancestor))
                if (ancestor is ScrollViewer scroll)
                {
                    if (!scroll.CanContentScroll && scroll.Content is Visual content)
                        scroll.ScrollToVerticalOffset(Math.Max(0,
                            anchor.TransformToAncestor(content).Transform(new Point()).Y - 12));
                    break;
                }
            await Task.Delay(100); UpdateLayout();
            bool visible = labels.All(InViewport);
            await screenshot(visible ? name : name + "-visibility-failure");
            if (!visible)
            {
                var surface = (FrameworkElement)Content;
                File.WriteAllText(Path.Combine(output, name + "-visibility-failure.json"), JsonSerializer.Serialize(new
                {
                    Width, Height,
                    Labels = labels.Select(label => new
                    {
                        label.Name, label.IsVisible, label.ActualWidth, label.ActualHeight,
                        Text = (label as TextBlock)?.Text, FullyInViewport = InViewport(label),
                        Bounds = label.TransformToAncestor(surface).TransformBounds(new Rect(label.RenderSize)).ToString()
                    }).ToArray()
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            return visible;
        }

        if (!paused) Click(PauseButton);
        Click(TaskMeasurementNav);
        var view = TaskMeasurementPage;
        require(paused && collector != null && Environment.GetCommandLineArgs().Contains("--ui-test")
            && view.Visibility == Visibility.Visible && !view.Running,
            "Task plan flow starts in the guarded independent UI-test directory with recording paused");
        var originalImport = view.PlanImportPathProvider; var originalExport = view.PlanExportPathProvider;
        var originalExecutable = view.ExecutablePathProvider; var originalInput = view.InputPathProvider;
        var originalWorkingDirectory = view.WorkingDirectoryPathProvider;
        string originalDirectory = Path.Combine(store.Root, "task-plan-ui", "original");
        string relocatedDirectory = Path.Combine(store.Root, "task-plan-ui", "relocated");
        Directory.CreateDirectory(originalDirectory); Directory.CreateDirectory(relocatedDirectory);
        string originalInputPath = Path.Combine(originalDirectory, "task-plan-input.cmd");
        string relocatedInputPath = Path.Combine(relocatedDirectory, "task-plan-input.cmd");
        string originalSentinel = Path.Combine(originalDirectory, "task-plan-executions.txt");
        string relocatedSentinel = Path.Combine(relocatedDirectory, "task-plan-executions.txt");
        const string script = "@echo off\r\necho synthetic-run>>task-plan-executions.txt\r\nexit /b 0\r\n";
        File.WriteAllText(originalInputPath, script, Encoding.ASCII);
        File.Copy(originalInputPath, relocatedInputPath, true);
        string planPath = Path.Combine(output, "task-plan-portable.json");
        string executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        string measurements = Path.Combine(store.Root, "measurements");
        string[] MeasurementFiles() => Directory.Exists(measurements) ? Directory.GetFiles(measurements, "*.json") : [];
        int RunCount() => File.Exists(relocatedSentinel) ? File.ReadAllLines(relocatedSentinel).Length : 0;
        string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        int beforeMeasurements = MeasurementFiles().Length;
        const string name = "方案搬迁校验（受控流程，非性能测试）";
        const string arguments = "/d /q /c task-plan-input.cmd";
        const string version = "手填的方案版本声明（必须保留）";
        const string procedure = "同一合成脚本和相对参数。\n仅核对方案复用与显式运行，不比较机器性能。";
        File.WriteAllText(Path.Combine(output, "task-plan-NOTICE.txt"),
            "The common startup checks use actual local CPU/memory collection. This flow then stays paused and uses two independent directories on the same machine to simulate task-plan relocation. A small explicitly run cmd script appends a sentinel once per attempt; its times are not performance measurements. Export, import, invalid inputs and mismatched protocol checks must not execute it. A successful explicit run is three formal attempts plus one warmup. No GPU workload or model is used, no physical cross-hardware comparison is claimed, and no production history or frozen soak files are touched. All fixture files are under the guarded UI-test data root; reports and screenshots are under the supplied test output.");
        try
        {
            if (view.BoundPlan != null) Click(view.NewTaskPlanButton);
            view.TaskNameBox.Text = name; view.ExecutableBox.Text = executable;
            view.ArgumentsBox.Text = arguments; view.WorkingDirectoryBox.Text = originalDirectory;
            view.SoftwareVersionBox.Text = version; view.InputFileBox.Text = originalInputPath;
            view.ProcedureBox.Text = procedure; view.PowerProfileBox.Text = "受控方案复用流程，非性能比较";
            string enteredProcedure = view.ProcedureBox.Text;
            view.TimeoutBox.Text = "5";
            view.RepeatsCombo.SelectedItem = view.RepeatsCombo.Items.OfType<ComboBoxItem>()
                .Single(item => item.Tag.ToString() == "3");
            view.WarmupCheck.IsChecked = true;
            view.PlanExportPathProvider = () => planPath;
            Click(view.ExportTaskPlanButton);
            await view.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            await wait(() => !view.Running && File.Exists(planPath) && view.BoundPlan != null);
            require(view.LastResult == null && !File.Exists(originalSentinel) && RunCount() == 0
                && MeasurementFiles().Length == beforeMeasurements && paused
                && view.PlanEnvironmentConfirmedCheck.IsChecked == true,
                "Saving a task plan hashes the selected files without running its parameters or creating a measurement");
            var plan = TaskPlans.Load(planPath);
            string originalPlanHash = HashFile(planPath);
            require(plan.Version == "task-plan-1.1" && plan.Recipe.Name == name
                && plan.Recipe.Arguments == arguments && plan.Recipe.SoftwareVersion == version
                && plan.Recipe.Procedure == enteredProcedure && plan.Recipe.Repeats == 3 && plan.Recipe.Warmup
                && plan.Recipe.TimeoutSeconds == 5 && plan.Recipe.InputName == "task-plan-input.cmd"
                && plan.Recipe.ExecutableSha256 == HashFile(executable)
                && plan.Recipe.InputSha256 == HashFile(originalInputPath)
                && !File.ReadAllText(planPath).Contains(originalDirectory.Replace("\\", "\\\\")),
                "The saved portable plan preserves its exact declared protocol and file hashes without local selected paths");

            view.PlanImportPathProvider = () => planPath;
            Click(view.ImportTaskPlanButton);
            await wait(() => view.BoundPlan != null && view.ExecutableBox.Text.Length == 0);
            require(view.WorkingDirectoryBox.Text.Length == 0 && view.InputFileBox.Text.Length == 0
                && view.TaskNameBox.Text == name && view.ArgumentsBox.Text == arguments
                && view.SoftwareVersionBox.Text == version && view.ProcedureBox.Text == enteredProcedure
                && view.LastResult == null && !view.Running && !File.Exists(originalSentinel) && RunCount() == 0,
                "Importing the portable plan restores its protocol but clears all three local paths and starts no task");
            bool planVisible = await Capture("63-task-plan-imported", view.TaskPlanPanel,
                view.TaskPlanStatus, view.TaskPlanEvidence);
            require(view.TaskNameBox.IsReadOnly && view.ArgumentsBox.IsReadOnly && view.SoftwareVersionBox.IsReadOnly
                && view.ProcedureBox.IsReadOnly && view.PowerProfileBox.IsReadOnly && view.TimeoutBox.IsReadOnly
                && !view.RepeatsCombo.IsEnabled && !view.WarmupCheck.IsEnabled
                && view.ChooseExecutableButton.IsEnabled && view.ChooseInputButton.IsEnabled
                && view.ChooseWorkingDirectoryButton.IsEnabled && view.NewTaskPlanButton.IsEnabled
                && planVisible && view.TaskPlanStatus.Text.Length > 0 && view.TaskPlanEvidence.Text.Length > 0,
                "The imported plan is visibly identified and locks protocol edits while allowing local file relocation");

            view.ExecutablePathProvider = () => executable;
            view.InputPathProvider = () => relocatedInputPath;
            view.WorkingDirectoryPathProvider = () => relocatedDirectory;
            void Relocate()
            {
                Click(view.ChooseExecutableButton); Click(view.ChooseInputButton); Click(view.ChooseWorkingDirectoryButton);
            }
            Relocate();
            bool environmentUnconfirmed = view.PlanEnvironmentConfirmedCheck.IsChecked != true;
            view.PlanEnvironmentConfirmedCheck.IsChecked = true;
            view.WorkingDirectoryBox.Text = originalDirectory;
            bool relocatedRequiresConfirmation = view.PlanEnvironmentConfirmedCheck.IsChecked != true;
            view.WorkingDirectoryBox.Text = relocatedDirectory;
            Click(view.RunTaskButton);
            await view.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            require(view.ExecutableBox.Text == executable && view.InputFileBox.Text == relocatedInputPath
                && view.WorkingDirectoryBox.Text == relocatedDirectory && view.SoftwareVersionBox.Text == version
                && view.BoundPlan?.Recipe == plan.Recipe && RunCount() == 0 && !File.Exists(originalSentinel)
                && environmentUnconfirmed && relocatedRequiresConfirmation && !view.Running && !measurementActive && view.LastResult == null
                && MeasurementFiles().Length == beforeMeasurements && view.TaskStatus.Text.Contains("环境核对"),
                "File pickers preserve the protocol but imported or relocated paths cannot run before local environment confirmation");

            view.PlanEnvironmentConfirmedCheck.IsChecked = true;
            Click(view.RunTaskButton);
            await view.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            var firstResult = view.LastResult;
            bool runVisible = await Capture("64-task-plan-relocated-run", view.TaskResultPanel, view.TaskResultSummary);
            require(firstResult is { Outcome: "completed" } && firstResult.Attempts.Count == 4
                && firstResult.Attempts.Count(attempt => attempt.Warmup) == 1 && RunCount() == 4
                && !File.Exists(originalSentinel) && MeasurementFiles().Length == beforeMeasurements + 1
                && paused && !view.Running && runVisible,
                "Only an explicit run executes the relocated script and produces one warmup plus three formal attempts");
            var completed = firstResult ?? throw new InvalidOperationException("The completed task result was not retained.");
            require(completed.Recipe == plan.Recipe && completed.ProtocolFingerprint == plan.ProtocolFingerprint
                && completed.Machine.PowerSource == plan.PowerSource && completed.Machine.PowerProfile == plan.PowerProfile
                && HashFile(planPath) == originalPlanHash,
                "Relocating local paths preserves the bound protocol while recording a new measurement on the current machine");
            string measurementPath = MeasurementFiles().Single(path =>
                TaskMeasurements.Load(path).Started == completed.Started);
            view.PlanImportPathProvider = () => measurementPath;
            Click(view.ImportTaskPlanButton);
            await wait(() => view.LastResult == null && view.BoundPlan != null);
            require(view.BoundPlan!.Recipe == completed.Recipe && view.LastResult == null
                && view.ExecutableBox.Text.Length == 0 && view.InputFileBox.Text.Length == 0
                && view.WorkingDirectoryBox.Text.Length == 0 && !view.ExportTaskButton.IsEnabled
                && view.PlanEnvironmentConfirmedCheck.IsChecked != true
                && RunCount() == 4 && MeasurementFiles().Length == beforeMeasurements + 1,
                "Loading an existing measurement clears a real previous result and requires fresh local paths and environment confirmation");

            Relocate();
            // Bypass the read-only editor deliberately: this verifies the backend
            // guard at the actual hashed recipe, not just the UI edit restriction.
            view.ArgumentsBox.Text = arguments + " unexpected-argument";
            view.PlanEnvironmentConfirmedCheck.IsChecked = true;
            Click(view.RunTaskButton);
            await view.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            require(view.LastResult == null && !view.Running && RunCount() == 4
                && MeasurementFiles().Length == beforeMeasurements + 1 && view.BoundPlan != null
                && view.TaskStatus.Text.Contains("不一致") && view.TaskStatus.Text.Contains("参数"),
                "A programmatically changed argument is rejected by the bound-plan run path before any new execution");
            view.ArgumentsBox.Text = arguments;
            File.AppendAllText(relocatedInputPath, "\r\nrem changed synthetic input\r\n", Encoding.ASCII);
            view.PlanEnvironmentConfirmedCheck.IsChecked = true;
            Click(view.RunTaskButton);
            await view.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            require(view.LastResult == null && !view.Running && RunCount() == 4
                && MeasurementFiles().Length == beforeMeasurements + 1 && view.BoundPlan != null
                && HashFile(relocatedInputPath) != plan.Recipe.InputSha256
                && view.TaskStatus.Text.Contains("输入文件内容"),
                "Changed input bytes are rejected before execution even when the selected filename and plan are unchanged");
            File.Copy(originalInputPath, relocatedInputPath, true);
            Click(view.RunTaskButton);
            await view.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            require(view.LastResult is { Outcome: "completed" } restored && restored.ProtocolFingerprint == completed.ProtocolFingerprint
                && restored.Attempts.Count == 4 && RunCount() == 8
                && MeasurementFiles().Length == beforeMeasurements + 2 && paused,
                "Restoring the exact input and arguments allows a new explicitly requested run under the original plan");

            var resultBeforePlanSave = view.LastResult;
            Click(view.ExportTaskPlanButton);
            await view.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            require(ReferenceEquals(view.LastResult, resultBeforePlanSave) && view.ExportTaskButton.IsEnabled
                && view.TaskResultPanel.Visibility == Visibility.Visible && view.BoundPlan?.Recipe == plan.Recipe
                && MeasurementFiles().Length == beforeMeasurements + 2 && RunCount() == 8 && !view.Running,
                "Saving a plan after a successful run preserves the existing result and its export action without replaying the task");

            var retainedPlan = view.BoundPlan; var retainedResult = view.LastResult;
            string invalidPath = Path.Combine(output, "task-plan-invalid-version.json");
            File.WriteAllText(invalidPath, "{\"Version\":\"task-plan-999\"}");
            view.PlanImportPathProvider = () => invalidPath;
            Click(view.ImportTaskPlanButton);
            bool invalidPreserved = view.BoundPlan == retainedPlan && ReferenceEquals(view.LastResult, retainedResult)
                && view.ExecutableBox.Text == executable && view.InputFileBox.Text == relocatedInputPath;
            view.PlanImportPathProvider = () => null;
            Click(view.ImportTaskPlanButton);
            require(invalidPreserved && view.BoundPlan == retainedPlan && ReferenceEquals(view.LastResult, retainedResult)
                && view.WorkingDirectoryBox.Text == relocatedDirectory && RunCount() == 8,
                "An invalid plan file or cancelled import preserves the current bound plan and completed measurement");

            await RunMeasurementHandoffScenario(output, require, screenshot, wait, retainedResult!);
            Click(view.NewTaskPlanButton);
            view.TaskNameBox.Text = "显式新建的合成方案";
            require(view.BoundPlan == null && !view.TaskNameBox.IsReadOnly && !view.ArgumentsBox.IsReadOnly
                && !view.SoftwareVersionBox.IsReadOnly && !view.ProcedureBox.IsReadOnly
                && !view.PowerProfileBox.IsReadOnly && !view.TimeoutBox.IsReadOnly
                && view.RepeatsCombo.IsEnabled && view.WarmupCheck.IsEnabled && view.LastResult == null
                && HashFile(planPath) == originalPlanHash && TaskPlans.Load(planPath).Recipe == plan.Recipe
                && RunCount() == 8 && paused && !view.Running,
                "Explicitly editing a new plan unlocks its fields without changing the saved original or leaving a task running");
        }
        finally
        {
            view.PlanImportPathProvider = originalImport; view.PlanExportPathProvider = originalExport;
            view.ExecutablePathProvider = originalExecutable; view.InputPathProvider = originalInput;
            view.WorkingDirectoryPathProvider = originalWorkingDirectory;
        }
    }
}
