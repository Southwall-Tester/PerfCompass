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
    private bool resultSaved = true;
    internal TaskPlan? BoundPlan { get; private set; }
    internal string DeclaredPowerProfile => BoundPlan == null ? PowerProfileBox.Text.Trim() : PowerProfileBox.Text;
    internal Func<string?>? PlanImportPathProvider;
    internal Func<string?>? PlanExportPathProvider;
    internal Func<string?>? ExecutablePathProvider;
    internal Func<string?>? InputPathProvider;
    internal Func<string?>? WorkingDirectoryPathProvider;
    internal Func<string?>? MeasurementExportPathProvider;
    internal Action? ResultPersisted;
    internal Action? OpenCandidateComparison;
    internal TaskMeasurementResult? LastResult { get; private set; }
    internal bool HasUnsavedResult => LastResult != null && !resultSaved;
    internal bool Running => cancellation != null;
    internal Task Completion => running;
    public TaskMeasurementView()
    {
        InitializeComponent();
        void Invalidate()
        {
            if (Running || LastResult == null) return;
            if (HasUnsavedResult) { TaskStatus.Text = "上次测量尚未保存，仍保留在下方；请先导出测量文件。当前设置的修改不会改写这份结果。"; return; }
            ClearResult(); TaskStatus.Text = "运行设置已修改，上次测量已保存。请重新运行当前设置。";
        }
        TaskInputs.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, e) =>
        {
            if (!Running && BoundPlan != null && (ReferenceEquals(e.OriginalSource, ExecutableBox)
                || ReferenceEquals(e.OriginalSource, WorkingDirectoryBox) || ReferenceEquals(e.OriginalSource, InputFileBox)))
                PlanEnvironmentConfirmedCheck.IsChecked = false;
            Invalidate();
        }));
        TaskInputs.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => Invalidate()));
        TaskInputs.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => Invalidate()));
        TaskInputs.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => Invalidate()));
    }
    internal void Initialize(LocalStore localStore, Func<TaskMachine> machineProvider, Action<bool> activityChanged) { store = localStore; machine = machineProvider; activity = activityChanged; }
    internal void Cancel() => cancellation?.Cancel();
    private bool ClearResult()
    {
        if (HasUnsavedResult) { TaskStatus.Text = "请先导出上次测量文件，避免丢失尚未保存的结果；本次操作尚未开始。"; return false; }
        LastResult = null; TaskResultPanel.Visibility = Visibility.Collapsed; ExportTaskButton.IsEnabled = UseMeasurementForCandidateButton.IsEnabled = false;
        return true;
    }
    private void SetPlan(TaskPlan? plan, bool environmentConfirmed = false)
    {
        BoundPlan = plan;
        foreach (var box in new[] { TaskNameBox, ArgumentsBox, SoftwareVersionBox, ProcedureBox, PowerProfileBox, TimeoutBox }) box.IsReadOnly = plan != null;
        RepeatsCombo.IsEnabled = WarmupCheck.IsEnabled = plan == null;
        NewTaskPlanButton.IsEnabled = plan != null;
        PlanEnvironmentConfirmedCheck.Visibility = plan == null ? Visibility.Collapsed : Visibility.Visible;
        PlanEnvironmentConfirmedCheck.IsChecked = plan != null && environmentConfirmed;
        TaskPlanEvidence.Text = plan == null ? "" : $"程序：{plan.Recipe.ExecutableName} · 输入：{plan.Recipe.InputName}\n软件：{plan.Recipe.SoftwareVersion} · 正式 {plan.Recipe.Repeats} 次 · {(plan.Recipe.Warmup ? "含一次预热" : "不预热")}\n电源：{(plan.PowerSource == "Online" ? "接通电源" : "电池供电")} · {plan.PowerProfile}";
    }
    private TaskRunRequest ReadRequest()
    {
        if (!int.TryParse(TimeoutBox.Text, out int timeout) || timeout is < 1 or > 600) throw new ArgumentException("单轮超时须为 1–600 秒。");
        string ProtocolText(TextBox box) => BoundPlan == null ? box.Text.Trim() : box.Text;
        return new(ProtocolText(TaskNameBox), ExecutableBox.Text.Trim(), ArgumentsBox.Text, WorkingDirectoryBox.Text.Trim(), ProtocolText(SoftwareVersionBox), InputFileBox.Text.Trim(), ProtocolText(ProcedureBox), int.Parse(((ComboBoxItem)RepeatsCombo.SelectedItem).Tag.ToString()!), WarmupCheck.IsChecked == true, timeout);
    }
    private void ImportTaskPlan_Click(object sender, RoutedEventArgs e)
    {
        if (Running) return;
        var dialog = new OpenFileDialog { Filter = "测量方案或测量记录 JSON|*.json", CheckFileExists = true };
        string? path = PlanImportPathProvider != null ? PlanImportPathProvider() : dialog.ShowDialog() == true ? dialog.FileName : null;
        if (path == null) return;
        try
        {
            var plan = TaskPlans.Load(path);
            if (!ClearResult()) return;
            SetPlan(null);
            TaskNameBox.Text = plan.Recipe.Name; ArgumentsBox.Text = plan.Recipe.Arguments;
            SoftwareVersionBox.Text = plan.Recipe.SoftwareVersion; ProcedureBox.Text = plan.Recipe.Procedure;
            PowerProfileBox.Text = plan.PowerProfile; TimeoutBox.Text = plan.Recipe.TimeoutSeconds.ToString(); WarmupCheck.IsChecked = plan.Recipe.Warmup;
            var item = RepeatsCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag.ToString() == plan.Recipe.Repeats.ToString());
            if (item == null) { item = new ComboBoxItem { Content = $"{plan.Recipe.Repeats} 次", Tag = plan.Recipe.Repeats.ToString() }; RepeatsCombo.Items.Add(item); }
            RepeatsCombo.SelectedItem = item;
            ExecutableBox.Clear(); WorkingDirectoryBox.Clear(); InputFileBox.Clear();
            SetPlan(plan);
            TaskPlanStatus.Text = $"已导入「{plan.Recipe.Name}」。请选择本机程序、工作目录和输入文件；运行前会核对是否与原方案一致。需要修改条件时，点击“作为新方案编辑”。";
            if (plan.UpgradedFromLegacy) TaskPlanStatus.Text += " 旧版任务安排已按当前只读保护方法复用；需重新核对环境并测量，不沿用旧耗时。";
            TaskStatus.Text = "方案已载入，尚未运行。请在下方核对本机环境；既有测量中的耗时和机器信息不会作为本机结果。";
        }
        catch (ArgumentException ex) { TaskPlanStatus.Text = ex.Message; }
        catch (Exception ex) { store?.Log(new InvalidOperationException("Task plan import failed: " + ex.GetType().Name)); TaskPlanStatus.Text = "未能读取有效测量方案，原设置保留。请检查文件格式与访问权限。"; }
    }
    private async void ExportTaskPlan_Click(object sender, RoutedEventArgs e)
    {
        if (Running) return;
        var dialog = new SaveFileDialog { Filter = "测量方案 JSON|*.json", FileName = "PerfCompass-测量方案.json" };
        string? path = PlanExportPathProvider != null ? PlanExportPathProvider() : dialog.ShowDialog() == true ? dialog.FileName : null;
        if (path != null) { running = ExportPlan(path); await running; }
    }
    private async Task ExportPlan(string path)
    {
        if (machine == null || activity == null) return;
        TaskRunRequest request; TaskMachine currentMachine;
        try { request = ReadRequest(); TaskMeasurements.ValidateRequest(request); currentMachine = machine(); }
        catch (ArgumentException ex) { TaskPlanStatus.Text = ex.Message; return; }
        catch (Exception ex) { store?.Log(new InvalidOperationException("Task plan preflight failed: " + ex.GetType().Name)); TaskPlanStatus.Text = "无法读取当前运行设置，方案未保存。"; return; }
        using var source = new CancellationTokenSource(); cancellation = source;
        bool environmentConfirmed = BoundPlan == null || PlanEnvironmentConfirmedCheck.IsChecked == true;
        var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => TaskProgressLabel.Text = $"正在校验方案文件 · 已等待 {elapsed.Elapsed.TotalSeconds:0.0} 秒";
        try
        {
            activity(true); TaskInputs.IsEnabled = TaskPlanButtons.IsEnabled = RunTaskButton.IsEnabled = ExportTaskButton.IsEnabled = false; CancelTaskButton.IsEnabled = true;
            TaskProgressBar.Visibility = Visibility.Visible; TaskProgressBar.IsIndeterminate = true;
            TaskPlanStatus.Text = "正在读取程序和输入文件，保存同条件测量方案；不会运行任务。";
            TaskStatus.Text = "文件校验期间暂停长期记录，完成后可继续记录。方案保留原样参数与步骤，其中手填的路径也会保留。";
            TaskProgressLabel.Text = "正在校验方案文件"; TaskProgressLabel.BringIntoView(); timer.Start();
            var plan = await TaskPlans.CreateAsync(request, currentMachine, source.Token);
            if (BoundPlan != null) TaskPlans.Verify(BoundPlan, plan.Recipe, currentMachine);
            source.Token.ThrowIfCancellationRequested(); TaskPlans.Save(path, plan);
            SetPlan(plan, environmentConfirmed);
            TaskPlanStatus.Text = "测量方案已保存，当前条件已固定。可直接运行，也可在候选电脑导入后选择本机文件；修改条件请作为新方案编辑。";
            TaskStatus.Text = "已保存方案，没有运行任务或生成新测量。既有测量结果保留，长期记录保持暂停，可按需继续。";
            TaskPlanPanel.BringIntoView();
        }
        catch (OperationCanceledException) { TaskPlanStatus.Text = "方案保存已取消，未替换已有文件。"; }
        catch (TaskFileProtectionException ex) { TaskPlanStatus.Text = ex.Message; }
        catch (ArgumentException ex) { TaskPlanStatus.Text = ex.Message; }
        catch (Exception ex) { store?.Log(new InvalidOperationException("Task plan export failed: " + ex.GetType().Name)); TaskPlanStatus.Text = "方案未保存，请检查文件与目标目录。"; }
        finally
        {
            timer.Stop(); elapsed.Stop(); cancellation = null; activity(false);
            TaskInputs.IsEnabled = TaskPlanButtons.IsEnabled = RunTaskButton.IsEnabled = true; CancelTaskButton.IsEnabled = false;
            ExportTaskButton.IsEnabled = LastResult != null;
            TaskProgressBar.Visibility = Visibility.Collapsed; TaskProgressBar.IsIndeterminate = false;
            TaskProgressLabel.Text = $"方案操作已结束 · 用时 {elapsed.Elapsed.TotalSeconds:0.0} 秒";
        }
    }
    private void NewTaskPlan_Click(object sender, RoutedEventArgs e)
    {
        if (Running) return;
        if (!ClearResult()) return;
        SetPlan(null);
        TaskPlanStatus.Text = "现在可编辑新方案。原方案文件保留，修改后的条件不会沿用原方案的对照。";
        TaskStatus.Text = "重新保存方案或显式运行后，将使用当前设置。";
    }
    private void ChooseExecutable_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Windows 程序|*.exe", CheckFileExists = true };
        string? path = ExecutablePathProvider != null ? ExecutablePathProvider() : dialog.ShowDialog() == true ? dialog.FileName : null;
        if (path == null) return;
        ExecutableBox.Text = path;
        if (WorkingDirectoryBox.Text.Length == 0) WorkingDirectoryBox.Text = Path.GetDirectoryName(path)!;
        if (BoundPlan == null) SoftwareVersionBox.Text = FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "";
    }
    private void ChooseWorkingDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();
        string? path = WorkingDirectoryPathProvider != null ? WorkingDirectoryPathProvider() : dialog.ShowDialog() == true ? dialog.FolderName : null;
        if (path != null) WorkingDirectoryBox.Text = path;
    }
    private void ChooseInput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { CheckFileExists = true };
        string? path = InputPathProvider != null ? InputPathProvider() : dialog.ShowDialog() == true ? dialog.FileName : null;
        if (path != null) InputFileBox.Text = path;
    }
    private async void RunTask_Click(object sender, RoutedEventArgs e) { if (!Running) { running = RunTask(); await running; } }
    private async Task RunTask()
    {
        if (store == null || machine == null || activity == null) return;
        if (BoundPlan != null && PlanEnvironmentConfirmedCheck.IsChecked != true) { TaskStatus.Text = "请先核对本机电源模式、软件设置和输入安排，并勾选环境核对；尚未运行任务。"; return; }
        if (!ClearResult()) return;
        TaskRunRequest request;
        TaskMachine currentMachine;
        try { request = ReadRequest(); TaskMeasurements.ValidateRequest(request); currentMachine = machine(); if (string.IsNullOrWhiteSpace(currentMachine.PowerProfile)) throw new ArgumentException("请填写电源 / 性能模式。"); }
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
            TaskInputs.IsEnabled = TaskPlanButtons.IsEnabled = false; RunTaskButton.IsEnabled = false; CancelTaskButton.IsEnabled = true;
            TaskProgressBar.Visibility = Visibility.Visible; TaskProgressBar.IsIndeterminate = true;
            TaskStatus.Text = "监测已暂停，本次任务测试不写入长期历史。结束后保持暂停，确认任务结束再继续记录。";
            timer.Start(); UpdateLabel();
            var progress = new Progress<TaskRunProgress>(p => { if (cancellation != source) return; stage = p.Stage; TaskProgressBar.Maximum = p.Total; TaskProgressBar.Value = Math.Max(0, p.Finished); TaskProgressBar.IsIndeterminate = p.Stage.Contains("校验") || p.Stage.Contains("复核"); UpdateLabel(); });
            var result = await TaskMeasurements.RunAsync(request, currentMachine, progress, source.Token, BoundPlan);
            LastResult = result; resultSaved = false;
            UseMeasurementForCandidateButton.IsEnabled = TaskMeasurements.HasCompleteRoundsAndStablePower(result);
            TaskResultSummary.Text = result.Summary; TaskEvidenceLabel.Text = TaskMeasurements.Evidence(result);
            TaskAttemptsList.ItemsSource = result.Attempts.Select(a => $"第 {a.Number} 轮{(a.Warmup ? "（预热）" : "")}：{a.Seconds:0.###} 秒 · {TaskMeasurements.OutcomeLabel(a.Outcome)} · 退出码 {a.ExitCode?.ToString() ?? "未知"} · 根进程退出 {(a.RootExited ? "是" : "未确认")}").ToArray();
            TaskResultPanel.Visibility = Visibility.Visible; ExportTaskButton.IsEnabled = true;
            TaskStatus.Text = result.Detail + " 已保存到 data/measurements。导入候选比较后仍需核对机器、步骤与结果；确认任务结束后再继续监测。";
            if (result.Outcome == "completed" && !TaskMeasurements.Comparable(result)) TaskStatus.Text = "测量已记录，但电源状态未知或发生变化，暂不能作为同条件比较依据。请核对后重新测试；确认任务结束后再继续监测。";
            try
            {
                var root = Path.Combine(store.Root, "measurements"); Directory.CreateDirectory(root);
                string path = Path.Combine(root, DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json"); TaskMeasurements.Save(path, result); resultSaved = true;
            }
            catch (Exception ex) { store.Log(new InvalidOperationException("Task autosave failed: " + ex.GetType().Name)); TaskStatus.Text = "测量已结束，但自动保存失败。请使用导出测量文件，保存到可写目录；确认任务结束后再继续监测。"; }
            TaskResultPanel.BringIntoView();
        }
        catch (OperationCanceledException) { TaskStatus.Text = "测量已取消；未使用旧结果。确认任务结束后再继续监测。"; }
        catch (TaskFileProtectionException ex) { TaskStatus.Text = ex.Message; }
        catch (ArgumentException ex) { TaskStatus.Text = ex.Message; }
        catch (Exception ex) { store.Log(new InvalidOperationException("Task measurement failed: " + ex.GetType().Name)); TaskStatus.Text = "测量或保存失败，未产生可用于配置判断的新结果。确认任务结束后再继续监测。"; }
        finally { timer.Stop(); elapsed.Stop(); cancellation = null; activity(false); TaskInputs.IsEnabled = TaskPlanButtons.IsEnabled = true; RunTaskButton.IsEnabled = true; CancelTaskButton.IsEnabled = false; TaskProgressBar.IsIndeterminate = false; TaskProgressBar.Visibility = Visibility.Collapsed; TaskProgressLabel.Text = $"本次运行已结束 · 用时 {elapsed.Elapsed.TotalSeconds:0.0} 秒"; }
    }
    private void CancelTask_Click(object sender, RoutedEventArgs e) => Cancel();
    private void UseMeasurementForCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (!Running && LastResult != null && UseMeasurementForCandidateButton.IsEnabled) OpenCandidateComparison?.Invoke();
    }
    private void ExportTask_Click(object sender, RoutedEventArgs e)
    {
        if (LastResult == null) return;
        var dialog = new SaveFileDialog { Filter = "任务测量 JSON|*.json", FileName = "PerfCompass-任务测量.json" };
        string? path = MeasurementExportPathProvider != null ? MeasurementExportPathProvider() : dialog.ShowDialog() == true ? dialog.FileName : null;
        if (path == null) return;
        try { TaskMeasurements.Save(path, LastResult); resultSaved = true; TaskStatus.Text = "已导出测量文件，可在候选比较中选中对应结果行后导入。"; ResultPersisted?.Invoke(); }
        catch (Exception ex) { store?.Log(new InvalidOperationException("Task export failed: " + ex.GetType().Name)); TaskStatus.Text = "导出失败，请检查目标目录。"; }
    }
}
