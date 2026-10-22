using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PerfCompass;

public partial class TaskMeasurementView : UserControl
{
    private LocalStore? store;
    private Func<TaskMachine>? machine;
    private Action<bool>? activity;
    private CancellationTokenSource? cancellation;
    private Task running = Task.CompletedTask;
    internal TaskMeasurementResult? LastResult { get; private set; }
    internal bool Running => cancellation != null;
    internal Task Completion => running;
    public TaskMeasurementView()
    {
        InitializeComponent();
        void Invalidate() { if (!Running && LastResult != null) { LastResult = null; TaskResultPanel.Visibility = Visibility.Collapsed; ExportTaskButton.IsEnabled = false; TaskStatus.Text = "运行设置已修改，上次测量已保存在 data/measurements。请重新运行当前设置。"; } }
        TaskInputs.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Invalidate()));
        TaskInputs.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => Invalidate()));
        TaskInputs.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => Invalidate()));
        TaskInputs.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => Invalidate()));
    }
    internal void Initialize(LocalStore localStore, Func<TaskMachine> machineProvider, Action<bool> activityChanged) { store = localStore; machine = machineProvider; activity = activityChanged; }
    internal void Cancel() => cancellation?.Cancel();
    private void ChooseExecutable_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Windows 程序|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        ExecutableBox.Text = dialog.FileName;
        if (WorkingDirectoryBox.Text.Length == 0) WorkingDirectoryBox.Text = Path.GetDirectoryName(dialog.FileName)!;
        SoftwareVersionBox.Text = FileVersionInfo.GetVersionInfo(dialog.FileName).ProductVersion ?? "";
    }
    private void ChooseWorkingDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog(); if (dialog.ShowDialog() == true) WorkingDirectoryBox.Text = dialog.FolderName;
    }
    private void ChooseInput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { CheckFileExists = true }; if (dialog.ShowDialog() == true) InputFileBox.Text = dialog.FileName;
    }
    private async void RunTask_Click(object sender, RoutedEventArgs e) { if (!Running) { running = RunTask(); await running; } }
    private async Task RunTask()
    {
        if (store == null || machine == null || activity == null) return;
        LastResult = null; TaskResultPanel.Visibility = Visibility.Collapsed; ExportTaskButton.IsEnabled = false;
        if (!int.TryParse(TimeoutBox.Text, out int timeout) || timeout is < 1 or > 600) { TaskStatus.Text = "单轮超时须为 1–600 秒。"; return; }
        var request = new TaskRunRequest(TaskNameBox.Text.Trim(), ExecutableBox.Text.Trim(), ArgumentsBox.Text, WorkingDirectoryBox.Text.Trim(), SoftwareVersionBox.Text.Trim(), InputFileBox.Text.Trim(), ProcedureBox.Text.Trim(), int.Parse(((ComboBoxItem)RepeatsCombo.SelectedItem).Tag.ToString()!), WarmupCheck.IsChecked == true, timeout);
        TaskMachine currentMachine;
        try { TaskMeasurements.ValidateRequest(request); currentMachine = machine(); if (string.IsNullOrWhiteSpace(currentMachine.PowerProfile)) throw new ArgumentException("请填写电源 / 性能模式。"); }
        catch (ArgumentException ex) { TaskStatus.Text = ex.Message; return; }
        catch (Exception ex) { store.Log(new InvalidOperationException("Task preflight failed: " + ex.GetType().Name)); TaskStatus.Text = "无法读取程序或机器信息，测量尚未开始。"; return; }
        using var source = new CancellationTokenSource(); cancellation = source;
        var elapsed = Stopwatch.StartNew(); string stage = "准备测量";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        void UpdateLabel() => TaskProgressLabel.Text = stage + $" · 已等待 {elapsed.Elapsed.TotalSeconds:0.0} 秒";
        timer.Tick += (_, _) => UpdateLabel();
        try
        {
            activity(true);
            TaskInputs.IsEnabled = false; RunTaskButton.IsEnabled = false; CancelTaskButton.IsEnabled = true;
            TaskProgressBar.Visibility = Visibility.Visible; TaskProgressBar.IsIndeterminate = true;
            TaskStatus.Text = "监测已暂停，本次任务测试不写入长期历史。结束后保持暂停，确认任务结束再继续记录。";
            timer.Start(); UpdateLabel();
            var progress = new Progress<TaskRunProgress>(p => { if (cancellation != source) return; stage = p.Stage; TaskProgressBar.Maximum = p.Total; TaskProgressBar.Value = Math.Max(0, p.Finished); TaskProgressBar.IsIndeterminate = p.Stage.Contains("校验") || p.Stage.Contains("复核"); UpdateLabel(); });
            var result = await TaskMeasurements.RunAsync(request, currentMachine, progress, source.Token);
            LastResult = result;
            TaskResultSummary.Text = result.Summary; TaskEvidenceLabel.Text = TaskMeasurements.Evidence(result);
            TaskAttemptsList.ItemsSource = result.Attempts.Select(a => $"第 {a.Number} 轮{(a.Warmup ? "（预热）" : "")}：{a.Seconds:0.###} 秒 · {TaskMeasurements.OutcomeLabel(a.Outcome)} · 退出码 {a.ExitCode?.ToString() ?? "未知"} · 根进程退出 {(a.RootExited ? "是" : "未确认")}").ToArray();
            TaskResultPanel.Visibility = Visibility.Visible; ExportTaskButton.IsEnabled = true;
            TaskStatus.Text = result.Detail + " 已保存到 data/measurements。导入候选比较后仍需核对机器、步骤与结果；确认任务结束后再继续监测。";
            if (result.Outcome == "completed" && !TaskMeasurements.Comparable(result)) TaskStatus.Text = "测量已记录，但电源状态未知或发生变化，暂不能作为同条件比较依据。请核对后重新测试；确认任务结束后再继续监测。";
            try
            {
                var root = Path.Combine(store.Root, "measurements"); Directory.CreateDirectory(root);
                string path = Path.Combine(root, DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json"); TaskMeasurements.Save(path, result);
            }
            catch (Exception ex) { store.Log(new InvalidOperationException("Task autosave failed: " + ex.GetType().Name)); TaskStatus.Text = "测量已结束，但自动保存失败。请使用导出测量文件，保存到可写目录；确认任务结束后再继续监测。"; }
            TaskResultPanel.BringIntoView();
        }
        catch (OperationCanceledException) { TaskStatus.Text = "测量已取消；未使用旧结果。确认任务结束后再继续监测。"; }
        catch (ArgumentException ex) { TaskStatus.Text = ex.Message; }
        catch (Exception ex) { store.Log(new InvalidOperationException("Task measurement failed: " + ex.GetType().Name)); TaskStatus.Text = "测量或保存失败，未产生可用于配置判断的新结果。确认任务结束后再继续监测。"; }
        finally { timer.Stop(); elapsed.Stop(); cancellation = null; activity(false); TaskInputs.IsEnabled = true; RunTaskButton.IsEnabled = true; CancelTaskButton.IsEnabled = false; TaskProgressBar.IsIndeterminate = false; TaskProgressBar.Visibility = Visibility.Collapsed; TaskProgressLabel.Text = $"本次运行已结束 · 用时 {elapsed.Elapsed.TotalSeconds:0.0} 秒"; }
    }
    private void CancelTask_Click(object sender, RoutedEventArgs e) => Cancel();
    private void ExportTask_Click(object sender, RoutedEventArgs e)
    {
        if (LastResult == null) return;
        var dialog = new SaveFileDialog { Filter = "任务测量 JSON|*.json", FileName = "PerfCompass-任务测量.json" };
        if (dialog.ShowDialog() != true) return;
        try { TaskMeasurements.Save(dialog.FileName, LastResult); TaskStatus.Text = "已导出测量文件，可在候选比较中选中对应结果行后导入。"; }
        catch (Exception ex) { store?.Log(new InvalidOperationException("Task export failed: " + ex.GetType().Name)); TaskStatus.Text = "导出失败，请检查目标目录。"; }
    }
}
