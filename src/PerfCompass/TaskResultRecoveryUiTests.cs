using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunTaskResultRecoveryUiTests(string output, Action<bool, string> require,
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
        var view = TaskMeasurementPage;
        async Task<bool> Capture(string name)
        {
            UpdateLayout();
            var anchor = view.TaskStatus;
            double bottom = view.TaskResultSummary.TransformToVisual(anchor)
                .TransformBounds(new Rect(view.TaskResultSummary.RenderSize)).Bottom;
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
            var labels = new[] { view.TaskStatus, view.TaskResultSummary, StatusLine };
            bool visible = labels.All(InViewport);
            await screenshot(visible ? name : name + "-visibility-failure");
            if (!visible)
            {
                var surface = (FrameworkElement)Content;
                File.WriteAllText(Path.Combine(output, name + "-visibility-failure.json"), JsonSerializer.Serialize(new
                {
                    Width, Height, view.HasUnsavedResult,
                    Labels = labels.Select(label => new
                    {
                        label.Name, label.Text, label.IsVisible, label.ActualWidth, label.ActualHeight,
                        FullyInViewport = InViewport(label),
                        Bounds = label.TransformToAncestor(surface).TransformBounds(new Rect(label.RenderSize)).ToString()
                    }).ToArray()
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            return visible;
        }

        if (!paused) Click(PauseButton);
        Click(TaskMeasurementNav);
        string markerPath = Path.GetFullPath(Path.Combine(store.Root, "measurements"));
        require(paused && collector != null && Environment.GetCommandLineArgs().Contains("--ui-test")
            && view.Visibility == Visibility.Visible && !view.Running && view.LastResult == null
            && !File.Exists(markerPath) && !Directory.Exists(markerPath),
            "Result recovery uses an isolated empty measurement location and never replaces an existing file or directory");
        var originalPlanImport = view.PlanImportPathProvider; var originalPlanExport = view.PlanExportPathProvider;
        var originalResultExport = view.MeasurementExportPathProvider;
        var originalDiscard = DiscardUnsavedMeasurementOnExit;
        string markerText = "PerfCompass synthetic autosave obstruction " + Guid.NewGuid().ToString("N");
        bool markerCreated = false, finalState = false, cleanupSucceeded = true;
        int discardQuestions = 0;
        string fixtureRoot = Path.Combine(store.Root, "task-result-recovery-ui");
        Directory.CreateDirectory(fixtureRoot);
        string shortInput = Path.Combine(fixtureRoot, "recovery-fast.cmd");
        string shortSentinel = Path.Combine(fixtureRoot, "recovery-fast-runs.txt");
        string cancelInput = Path.Combine(fixtureRoot, "recovery-cancel.cmd");
        string cancelSentinel = Path.Combine(fixtureRoot, "recovery-cancel-started.txt");
        string planPath = Path.Combine(output, "task-result-recovery-plan.json");
        string savedResultPath = Path.Combine(output, "task-result-recovered-completed.json");
        string cancelledResultPath = Path.Combine(output, "task-result-recovered-cancelled.json");
        File.WriteAllText(shortInput, "@echo off\r\necho synthetic-run>>recovery-fast-runs.txt\r\nexit /b 0\r\n", Encoding.ASCII);
        File.WriteAllText(cancelInput, "@echo off\r\necho synthetic-start>>recovery-cancel-started.txt\r\nping -n 5 127.0.0.1 >nul\r\nexit /b 0\r\n", Encoding.ASCII);
        int ShortRuns() => File.Exists(shortSentinel) ? File.ReadAllLines(shortSentinel).Length : 0;
        void Configure(string name, string input)
        {
            view.TaskNameBox.Text = name; view.ExecutableBox.Text = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            view.ArgumentsBox.Text = "/d /q /c " + Path.GetFileName(input); view.WorkingDirectoryBox.Text = fixtureRoot;
            view.SoftwareVersionBox.Text = "受控 cmd 保存恢复流程"; view.InputFileBox.Text = input;
            view.ProcedureBox.Text = "合成生命周期测试；不测量硬件性能"; view.PowerProfileBox.Text = "本机保存恢复流程测试";
            view.TimeoutBox.Text = "5"; view.WarmupCheck.IsChecked = false;
            view.RepeatsCombo.SelectedItem = view.RepeatsCombo.Items.OfType<ComboBoxItem>()
                .Single(item => item.Tag.ToString() == "3");
        }
        File.WriteAllText(Path.Combine(output, "task-result-recovery-NOTICE.txt"),
            "This flow creates a unique ordinary marker file at measurements in the guarded empty UI-test data directory, after proving no file or directory exists there. The marker causes real task autosave to fail. A short cmd script runs three times; another cmd/ping script is explicitly started and then cancelled through the real application Exit path. These are lifecycle and preservation checks, not performance or cross-hardware tests. The test refuses discard, exports the retained results using the actual export button, and removes only its own unchanged marker file. Existing user data and frozen soak files are untouched. Emergency exports preserve any unsaved failure-state result before the outer UI-test shutdown.");
        try
        {
            using (var marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                markerCreated = true;
                marker.Write(Encoding.UTF8.GetBytes(markerText));
            }
            DiscardUnsavedMeasurementOnExit = () => { discardQuestions++; return false; };
            Configure("保存失败后保留（合成任务）", shortInput);
            Click(view.RunTaskButton); await view.Completion.WaitAsync(TimeSpan.FromSeconds(20));
            var retained = view.LastResult;
            require(retained is { Outcome: "completed", Attempts.Count: 3 } && ShortRuns() == 3
                && view.HasUnsavedResult && view.ExportTaskButton.IsEnabled
                && view.TaskResultPanel.Visibility == Visibility.Visible && view.TaskStatus.Text.Contains("自动保存失败")
                && File.Exists(markerPath) && !Directory.Exists(markerPath),
                "Three real completed attempts survive autosave failure as an exportable unsaved result");
            var completed = retained ?? throw new InvalidOperationException("The unsaved completed result was lost.");
            string originalName = completed.Recipe.Name;
            view.TaskNameBox.Text = "已编辑但尚未运行的新设置";
            bool editPreserved = ReferenceEquals(view.LastResult, completed) && completed.Recipe.Name == originalName;
            view.PlanExportPathProvider = () => planPath;
            Click(view.ExportTaskPlanButton); await view.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            require(editPreserved && ReferenceEquals(view.LastResult, completed) && view.HasUnsavedResult
                && view.ExportTaskButton.IsEnabled && view.TaskResultPanel.Visibility == Visibility.Visible
                && File.Exists(planPath) && view.BoundPlan != null && ShortRuns() == 3,
                "Editing current settings and saving a plan preserve the separate unsaved measurement without replaying it");

            var bound = view.BoundPlan;
            Click(view.NewTaskPlanButton);
            bool newBlocked = view.BoundPlan == bound && ReferenceEquals(view.LastResult, completed);
            view.PlanImportPathProvider = () => planPath; Click(view.ImportTaskPlanButton);
            bool importBlocked = view.BoundPlan == bound && ReferenceEquals(view.LastResult, completed);
            Click(view.RunTaskButton); await view.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            bool runBlocked = !view.Running && ReferenceEquals(view.LastResult, completed) && ShortRuns() == 3;
            view.PlanExportPathProvider = () => null; Click(view.ExportTaskPlanButton);
            bool cancelledPlanPreserved = view.HasUnsavedResult && ReferenceEquals(view.LastResult, completed);
            string blockedExport = Path.Combine(output, "task-result-export-blocked"); Directory.CreateDirectory(blockedExport);
            view.PlanExportPathProvider = () => blockedExport; Click(view.ExportTaskPlanButton);
            await view.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            bool failedPlanPreserved = view.HasUnsavedResult && ReferenceEquals(view.LastResult, completed);
            view.MeasurementExportPathProvider = () => null; Click(view.ExportTaskButton);
            bool cancelPreserved = view.HasUnsavedResult && ReferenceEquals(view.LastResult, completed);
            view.MeasurementExportPathProvider = () => blockedExport; Click(view.ExportTaskButton);
            require(newBlocked && importBlocked && runBlocked && cancelledPlanPreserved && failedPlanPreserved
                && cancelPreserved && view.HasUnsavedResult
                && ReferenceEquals(view.LastResult, completed) && view.ExportTaskButton.IsEnabled
                && view.TaskStatus.Text.Contains("导出失败") && ShortRuns() == 3,
                "New import and run cannot discard the unsaved result and cancelled or failed plan or result exports preserve its guard");

            Exit_Click(this, new RoutedEventArgs());
            await wait(() => !exitPending);
            bool completedVisible = await Capture("65-task-unsaved-preserved");
            require(discardQuestions == 1 && !exiting && !exitPending && !stop.IsCancellationRequested
                && view.Visibility == Visibility.Visible && ReferenceEquals(view.LastResult, completed)
                && view.HasUnsavedResult && view.ExportTaskButton.IsEnabled
                && StatusLine.Text.Contains("结果已保留") && completedVisible,
                "The real exit action returns to the visibly preserved unsaved result when discard is declined");
            view.MeasurementExportPathProvider = () => savedResultPath; Click(view.ExportTaskButton);
            var savedCompleted = TaskMeasurements.Load(savedResultPath);
            bool exportReleased = !view.HasUnsavedResult && savedCompleted.Outcome == "completed"
                && savedCompleted.Recipe.Name == originalName && savedCompleted.Attempts.Count == 3;
            Click(view.NewTaskPlanButton);
            require(exportReleased && view.LastResult == null && view.BoundPlan == null
                && !view.HasUnsavedResult && ShortRuns() == 3,
                "A successful real export preserves the original result content and releases the guard for a new plan");

            Configure("退出时取消并保留（合成任务）", cancelInput);
            view.PlanExportPathProvider = () => Path.Combine(output, "task-result-cancel-plan.json");
            Click(view.ExportTaskPlanButton); await view.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            if (view.BoundPlan == null) throw new InvalidOperationException("The cancellation fixture plan was not saved.");
            Click(view.RunTaskButton);
            // Wait until the actual process has written its start marker. Exiting
            // during preflight hashing would not exercise preservation of a new
            // cancellation result produced after Exit began waiting.
            await wait(() => view.Running && File.Exists(cancelSentinel));
            Exit_Click(this, new RoutedEventArgs());
            await view.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            await wait(() => !exitPending && !view.Running);
            bool cancelledVisible = await Capture("66-task-cancelled-unsaved-preserved");
            require(discardQuestions == 2 && !exiting && !exitPending && !stop.IsCancellationRequested
                && view.LastResult is { Outcome: "cancelled" } cancelled && cancelled.Attempts.Count > 0
                && cancelled.Attempts.Any(attempt => attempt.Outcome == "cancelled")
                && view.HasUnsavedResult && view.ExportTaskButton.IsEnabled && cancelledVisible,
                "Exit waits for a started task to cancel then protects its newly produced unsaved result before shutdown");

            view.MeasurementExportPathProvider = () => cancelledResultPath; Click(view.ExportTaskButton);
            var savedCancelled = TaskMeasurements.Load(cancelledResultPath);
            bool cancellationExported = !view.HasUnsavedResult && savedCancelled.Outcome == "cancelled"
                && savedCancelled.Attempts.Any(attempt => attempt.Outcome == "cancelled");
            Click(view.NewTaskPlanButton);
            finalState = cancellationExported && view.LastResult == null && view.BoundPlan == null
                && !view.HasUnsavedResult && paused && !view.Running && !measurementActive;
        }
        finally
        {
            // Preserve an unsaved failure-state result through the production
            // export action before restoring the normal exit callback. Otherwise
            // the outer test's Exit could open an unattended discard dialog.
            try
            {
                if (view.Running) { view.Cancel(); await view.Completion.WaitAsync(TimeSpan.FromSeconds(15)); }
                for (int attempt = 0; exitPending && attempt < 80; attempt++) await Task.Delay(100);
                if (view.HasUnsavedResult)
                {
                    string emergency = Path.Combine(output, "task-result-emergency-" + Guid.NewGuid().ToString("N") + ".json");
                    view.MeasurementExportPathProvider = () => emergency;
                    Click(view.ExportTaskButton);
                    if (view.HasUnsavedResult)
                    {
                        // A failure involving the normal output directory must
                        // still leave a recovery copy in the isolated data root.
                        string fallback = Path.Combine(fixtureRoot, "emergency-" + Guid.NewGuid().ToString("N") + ".json");
                        view.MeasurementExportPathProvider = () => fallback;
                        Click(view.ExportTaskButton);
                    }
                    if (view.HasUnsavedResult) throw new IOException("The emergency UI export did not preserve the unsaved result.");
                }
            }
            catch (Exception error)
            {
                cleanupSucceeded = false;
                // Keep the original assertion exception as the failure. This
                // separate diagnostic records any preservation cleanup problem.
                try { File.WriteAllText(Path.Combine(output, "task-result-recovery-cleanup-error.txt"), error.ToString()); }
                catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException) { }
            }
            view.PlanImportPathProvider = originalPlanImport; view.PlanExportPathProvider = originalPlanExport;
            view.MeasurementExportPathProvider = originalResultExport;
            // If both recovery locations themselves fail, keep the result and
            // avoid an unattended modal dialog; never discard it in cleanup.
            DiscardUnsavedMeasurementOnExit = view.HasUnsavedResult ? () => false : originalDiscard;
            try
            {
                if (markerCreated && File.Exists(markerPath) && !Directory.Exists(markerPath)
                    && File.ReadAllText(markerPath) == markerText) File.Delete(markerPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                cleanupSucceeded = false;
                try { File.AppendAllText(Path.Combine(output, "task-result-recovery-cleanup-error.txt"), "\n" + error); }
                catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException) { }
            }
        }
        require(finalState && cleanupSucceeded && !File.Exists(markerPath) && !Directory.Exists(markerPath)
            && !view.Running && !view.HasUnsavedResult && !exitPending && !exiting && !stop.IsCancellationRequested,
            "Both retained outcomes are exported and only the owned obstruction is removed leaving a clean paused task-plan flow");
    }
}
