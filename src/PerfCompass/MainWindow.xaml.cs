using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace PerfCompass;

public partial class MainWindow : Window
{
    private readonly LocalStore store;
    private readonly CancellationTokenSource stop = new();
    private Collector? collector;
    private readonly Forms.NotifyIcon tray;
    private volatile bool paused;
    private bool exiting, initialized, analyzing, refreshRequested;
    private bool measurementActive;
    private int recordingGeneration;
    private readonly object recordingGate = new();
    private readonly List<HistoryPoint> livePoints = [];
    private readonly List<Sample> experienceContext = [];
    private AnalysisReport? report;
    private LiveSnapshot? live;
    private int windowDays = 7;
    private string? analysisScene;
    private bool updatingSceneChoices;
    private RequirementsPlan? requirementDraft;
    private Task? collectionTask;
    private DateTimeOffset lastAnalysis = DateTimeOffset.MinValue;
    private string activePage = "live";
    private volatile bool historyWriteFailed;
    public MainWindow(LocalStore store)
    {
        this.store = store;
        InitializeComponent();
        ComparisonPage.OpenTaskMeasurement = () => Navigate("measurement");
        TaskMeasurementPage.Initialize(store,
            () => new(live?.CpuName ?? "", live?.GpuName ?? "", live?.Sample.MemoryTotalGb, Environment.OSVersion.VersionString, TaskMeasurements.PowerSource(), TaskMeasurementPage.PowerProfileBox.Text.Trim()),
            active =>
            {
                if (active && semanticCancellation != null) throw new ArgumentException("模型解析尚未结束，请结束后再进行任务测量。");
                bool wasActive = measurementActive; measurementActive = active;
                if (active && !paused) TogglePause();
                if (!active && wasActive) StatusLine.Text = "任务测量已结束。核对任务进程后，可继续监测。";
            });
        SelectSavedScene();
        RepresentativeCheck.IsChecked = false;
        FutureBox.Text = store.Settings.FutureNeeds;
        InitializeSemanticSettings();
        if (store.Settings.Requirements is { } savedPlan && savedPlan.Source == store.Settings.FutureNeeds.Trim())
        { ShowRequirementDraft(savedPlan); RequirementsConfirm.IsChecked = true; }
        CloseToTrayCheck.IsChecked = store.Settings.CloseToTray;
        try { StartupCheck.IsChecked = StartupRegistration.Enabled(); }
        catch (Exception error) { StartupCheck.IsChecked = false; SettingsFailure("startup-read", "无法读取自启动状态，请检查启动设置权限。", error); }
        RetentionCombo.SelectedItem = RetentionCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag.ToString() == store.Settings.RetentionDays.ToString());
        RetentionLabel.Text = $"当前保存最近 {store.Settings.RetentionDays} 天 · 实时约 2 秒 · 历史记录约 10 秒";
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 PerfCompass", null, (_, _) => Dispatcher.Invoke(Restore));
        menu.Items.Add("暂停 / 继续记录", null, (_, _) => Dispatcher.Invoke(TogglePause));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Exit));
        tray = new Forms.NotifyIcon { Text = "PerfCompass · 正在记录", Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application, Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(Restore);
        RefreshDataProtection();
        if (store.SettingsRecoveryPending || store.RetentionProtected || Environment.GetCommandLineArgs().Contains("--paused"))
        {
            TogglePause();
            Navigate("settings");
            StatusLine.Text = "数据恢复保护中，请核对记录设置后再继续监测。";
        }
        Closing += OnClosing;
        initialized = true;
        Loaded += async (_, _) =>
        {
            try
            {
                if (Environment.GetCommandLineArgs().Contains("--recovery-ui-test")) { await RunRecoveryStartupUiTest(); return; }
                collector = await Task.Run(() => new Collector(store));
                InitializeWorkloadCoverage();
                collectionTask = Task.Run(CollectionLoop);
                await RefreshAnalysis();
                if (Environment.GetCommandLineArgs().Contains("--ui-test")) _ = RunUiTests();
                if (Environment.GetCommandLineArgs().Contains("--smoke-test")) _ = RunSmokeTest();
            }
            catch (Exception ex) { store.Log(ex); StatusLine.Text = "采集启动失败，请查看 data/errors.log 后重试。"; RecordState.Text = "● 启动失败"; }
        };
    }
    private async Task CollectionLoop()
    {
        var batch = new List<Sample>();
        int generation = recordingGeneration;
        var lastCleanup = DateTimeOffset.Now;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(2000, stop.Token);
                if (paused || generation != Volatile.Read(ref recordingGeneration))
                {
                    collector!.ResetTiming(); batch.Clear(); generation = Volatile.Read(ref recordingGeneration); continue;
                }
                try
                {
                    var snapshot = collector!.Collect();
                    lock (recordingGate)
                    {
                        if (paused || generation != recordingGeneration) { batch.Clear(); continue; }
                        if (SampleBatch.MustFlushBefore(batch, snapshot.Sample))
                        { store.Append(SampleBatch.Merge(batch)); batch.Clear(); }
                        batch.Add(snapshot.Sample);
                        if (batch.Count >= 5) { store.Append(SampleBatch.Merge(batch)); batch.Clear(); historyWriteFailed = false; }
                    }
                    if (DateTimeOffset.Now - lastCleanup > TimeSpan.FromHours(1)) { store.Cleanup(); lastCleanup = DateTimeOffset.Now; }
                    await Dispatcher.InvokeAsync(() => UpdateLive(snapshot));
                }
                catch (Exception ex)
                {
                    if (ex is IOException or UnauthorizedAccessException)
                    {
                        historyWriteFailed = true;
                        // Keep at most one pending bucket if the destination stays unwritable.
                        if (batch.Count > 5) batch.RemoveRange(0, batch.Count - 5);
                    }
                    store.Log(ex);
                    await Dispatcher.InvokeAsync(() => { RecordState.Text = "● 采集重试中"; StatusLine.Text = "本次采样失败，2 秒后重试。错误已写入 data/errors.log。"; });
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { if (batch.Count > 0 && generation == recordingGeneration) store.Append(SampleBatch.Merge(batch)); } catch (Exception ex) { store.Log(ex); }
            collector?.Dispose();
        }
    }
    private static string Percent(double? value) => value is double d ? $"{d:0}%" : "—";
    private void UpdateLive(LiveSnapshot snapshot)
    {
        if (paused || exiting) return;
        live = snapshot;
        var s = snapshot.Sample;
        experienceContext.Add(s);
        experienceContext.RemoveAll(p => p.Time < s.Time.AddMinutes(-2));
        CpuValue.Text = Percent(s.Cpu); CpuBar.Value = s.Cpu ?? 0;
        CpuDetail.Text = $"最忙核心 {Percent(s.CoreMax)}" + (s.CpuTemp.HasValue ? $" · {s.CpuTemp:0}°C" : "");
        double? memPct = s.MemoryTotalGb > 0 ? s.MemoryUsedGb / s.MemoryTotalGb * 100 : null;
        MemoryValue.Text = Percent(memPct); MemoryBar.Value = memPct ?? 0;
        MemoryDetail.Text = $"{s.MemoryUsedGb:0.0} / {s.MemoryTotalGb:0.0} GiB";
        GpuValue.Text = Percent(s.Gpu); GpuBar.Value = s.Gpu ?? 0;
        GpuDetail.Text = s.Gpu.HasValue ? (s.VramUsedGb.HasValue ? $"显存 {s.VramUsedGb:0.0} GiB" : "显存数据待采集") + (s.GpuTemp.HasValue ? $" · {s.GpuTemp:0}°C" : "") : "负载传感器不可用";
        DiskValue.Text = Percent(s.DiskBusy); DiskBar.Value = s.DiskBusy ?? 0;
        DiskDetail.Text = s.DiskReadMbps.HasValue ? $"读 {s.DiskReadMbps:0.0} / 写 {s.DiskWriteMbps:0.0} MiB/s" : "计数器不可用";
        CpuNameLabel.Text = snapshot.CpuName; GpuNameLabel.Text = snapshot.GpuName;
        DriveLabel.Text = string.Join("\n", snapshot.Disks.Select(d => $"{d.Name}  {d.UsedGb:0} / {d.TotalGb:0} GiB"));
        AppList.ItemsSource = snapshot.Apps;
        SensorLabel.Text = snapshot.SensorStatus;
        SensorHealthBanner.Text = snapshot.SensorFailures.Length == 0 ? "" : "本次传感器更新失败：" + string.Join("、", snapshot.SensorFailures) + "。对应读数留空，基础采集继续；下次自动重试。";
        SensorHealthBanner.Visibility = snapshot.SensorFailures.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RecordState.Text = "● " + (s.Active ? "正在记录 · 有效使用" : "正在记录 · 空闲");
        LastTime.Text = $"更新于 {s.Time.LocalDateTime:HH:mm:ss}";
        StatusLine.Text = historyWriteFailed ? "实时采集继续，历史保存失败。请检查数据目录权限与磁盘空间；程序会自动重试。" : $"采集继续 · 历史记录约 10 秒保存 · {snapshot.SensorStatus}";
        livePoints.Add(new(s.Time, s.Cpu, memPct, s.Gpu));
        livePoints.RemoveAll(p => p.Time < s.Time.AddMinutes(-4));
        LiveChart.SetPoints(livePoints);
        if ((DateTimeOffset.Now - lastAnalysis).TotalSeconds >= 60 && !analyzing) _ = RefreshAnalysis();
    }
    private async Task RefreshAnalysis()
    {
        if (analyzing) { refreshRequested = true; return; }
        analyzing = true; lastAnalysis = DateTimeOffset.Now;
        try
        {
            var settings = store.Settings; var id = collector?.HardwareId; int days = windowDays; string? scene = analysisScene;
            var result = await Task.Run(() => SemanticRequirements.Apply(RequirementParser.Apply(ExperienceAnalyzer.Attach(
                HistoryAnalysis.Analyze(store, settings, days, scene, hardwareId: id), store.ReadExperiences(days, id ?? "")), settings), settings));
            if (days != windowDays || scene != analysisScene || settings != store.Settings || id != collector?.HardwareId) { refreshRequested = true; return; }
            report = result;
            HistoryQualityNotice.Visibility = result.HistoryQuality.HasNotices ? Visibility.Visible : Visibility.Collapsed;
            HistoryQualityNotice.Header = result.HistoryQuality.HasIssues ? $"历史记录异常 · {result.HistoryQuality.IssueCount} 处（点击查看位置）" : $"已排除重复记录 · {result.HistoryQuality.DuplicateCount} 条（点击查看位置）";
            HistoryQualityDetails.Text = result.HistoryQuality.Message + "\n" + result.HistoryQuality.Details + "\n原文件保留。可在记录设置备份，并从已核对的备份恢复到新目录。";
            ComparisonPage.RefreshLoadedContext(store, result);
            ExperienceList.ItemsSource = result.ExperienceEvents.Take(20);
            ExperienceSummary.Text = $"最近 {days} 天 · {scene ?? "全部场景"} · {result.ExperienceEvents.Count} 条记录，显示最近 20 条";
            updatingSceneChoices = true;
            try
            {
                AnalysisSceneCombo.Items.Clear();
                var allScenes = new ComboBoxItem { Content = "全部场景", Tag = null };
                AnalysisSceneCombo.Items.Add(allScenes); ComboBoxItem selected = allScenes;
                foreach (string name in result.SceneProfiles.Select(p => p.Name).Concat(scene == null ? [] : new[] { scene }).Distinct())
                {
                    var choice = new ComboBoxItem { Content = name, Tag = name }; AnalysisSceneCombo.Items.Add(choice);
                    if (name == scene) selected = choice;
                }
                AnalysisSceneCombo.SelectedItem = selected;
            }
            finally { updatingSceneChoices = false; }
            SceneProfileList.ItemsSource = result.SceneProfiles;
            SceneProfileSummary.Text = result.HistoryQuality.HasIntervalConflicts ? "场景时段互有重叠，不能相加；负载值暂待核对" : "有效使用时段统计 · P95 按采样时长加权";
            SceneEmptyLabel.Visibility = result.SceneProfiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DemandTrendList.ItemsSource = result.DemandTrends;
            var changed = result.DemandTrends.Where(t => t.Status == "increased").OrderBy(t => t.Days).FirstOrDefault();
            DemandTrendHeader.Text = changed == null ? "近期需求变化 · " + (result.DemandTrends.Any(t => t.Status == "similar" || t.Status == "decreased") ? "查看时期对照" : "对照记录积累中") : $"近期需求变化 · {changed.Name}增加（{changed.Days} 天对照）";
            AnalysisSummary.Text = result.Summary;
            CoverageLabel.Text = $"{scene ?? "全部场景"} · {result.Level}  ·  有效使用 {result.DurationLabel}  ·  覆盖 {result.ActiveDays} 天  ·  {result.SampleCount:N0} 条记录";
            RepresentativeStatus.Text = result.CoverageMessage;
            CoverageNext.Text = result.HistoryQuality.HasIssues ? result.HistoryQuality.Message : !result.RepresentativeConfirmed ? result.CoverageMessage : result.Ready ? "已满足基础观察门槛；近期任务变化会优先约束配置建议。" : result.ActiveHours >= 10 && result.ActiveDays >= 3 && (result.FreshActiveHours < .5 || result.FreshActiveDays < 2) ? "还需补充最近 7 天至少 30 分钟、2 天的有效记录，再判断当前配置余量。" : "观察目标：10 小时 / 3 天。" + result.CoverageMessage;
            var expanded = AdviceList.Items.OfType<Border>().Select(b => b.Child).OfType<StackPanel>()
                .SelectMany(s => s.Children.OfType<Expander>()).Where(x => x.IsExpanded).Select(x => x.Tag?.ToString()).ToHashSet();
            AdviceList.Items.Clear();
            foreach (var advice in result.Cards) AdviceList.Items.Add(AdviceCard(advice, expanded.Contains(advice.Key)));
            HistoryChart.SetPoints(result.History);
            HistorySummary.Text = $"最近 {days} 天 · {result.SampleCount:N0} 条记录 · {scene ?? "全部场景"} · " + (result.HistoryQuality.HasIntervalConflicts ? "时段重叠，历史负载图暂不显示；场景时长可能互相重叠，不能相加。" : result.Scenes.Length > 0 ? string.Join("、", result.Scenes) : "等待有效使用");
        }
        catch (Exception ex) { store.Log(ex); StatusLine.Text = "分析读取失败，请稍后刷新。错误已记录。"; }
        finally
        {
            analyzing = false;
            if (refreshRequested && !exiting) { refreshRequested = false; _ = RefreshAnalysis(); }
        }
    }
    private static Brush Brush(string color) => (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
    private Border AdviceCard(Advice advice, bool expanded)
    {
        var stack = new StackPanel();
        string label = advice.Status switch { "surplus" => "疑似过剩", "pressure" => "压力线索", "fit" => "保持观察", "changed" => "近期需求增加", "review" => "待验证降档", "experience" => "体验待核对", "requirements" => "新需求待验证", "constraints" => "已确认条件", _ => "证据积累中" };
        string color = advice.Status switch { "surplus" => "#007F73", "pressure" or "changed" => "#BC7132", "review" => "#6378C8", _ => "#677A87" };
        var header = new DockPanel();
        header.Children.Add(new TextBlock { Text = advice.Name, FontSize = 13, Foreground = Brush("#677A87") });
        header.Children.Add(new TextBlock { Text = label, Foreground = Brush(color), HorizontalAlignment = System.Windows.HorizontalAlignment.Right, FontSize = 12 });
        stack.Children.Add(header);
        stack.Children.Add(new TextBlock { Text = advice.Title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new(0, 10, 0, 10) });
        stack.Children.Add(new TextBlock { Text = advice.Suggestion, Foreground = Brush("#45606C"), LineHeight = 23 });
        var detail = new StackPanel();
        detail.Children.Add(new TextBlock { Text = advice.Evidence, Margin = new(0, 12, 0, 8), LineHeight = 22 });
        detail.Children.Add(new TextBlock { Text = advice.Limits, Foreground = Brush("#677A87"), FontSize = 12, LineHeight = 21 });
        stack.Children.Add(new Expander { Tag = advice.Key, IsExpanded = expanded, Header = "查看依据与选型条件", Content = detail, Margin = new(0, 16, 0, 0), Foreground = Brush("#007F73") });
        return new Border { Style = (Style)FindResource("Card"), Child = stack, Margin = new(0, 0, 0, 14) };
    }
    private void Navigate(string page)
    {
        activePage = page;
        LivePage.Visibility = page == "live" ? Visibility.Visible : Visibility.Collapsed;
        AdvicePage.Visibility = page == "advice" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == "history" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        ComparisonPage.Visibility = page == "comparison" ? Visibility.Visible : Visibility.Collapsed;
        TaskMeasurementPage.Visibility = page == "measurement" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "comparison" && report != null) ComparisonPage.SetContext(store, report);
        foreach (var pair in new[] { (LiveNav, "live"), (AdviceNav, "advice"), (ComparisonNav, "comparison"), (TaskMeasurementNav, "measurement"), (HistoryNav, "history"), (SettingsNav, "settings") })
        { pair.Item1.Background = Brush(pair.Item2 == page ? "#254854" : "#132D38"); pair.Item1.Foreground = Brush(pair.Item2 == page ? "#FFFFFF" : "#BCCED4"); }
        (PageTitle.Text, PageSubtitle.Text) = page switch
        {
            "advice" => ("配置分析", "识别不足与过剩，整理下次购机的配置依据"),
            "history" => ("历史趋势", "查看使用分布，导出本机采样记录"),
            "settings" => ("记录设置", "确认典型任务与下一台电脑的新需求"),
            "comparison" => ("候选配置", "比较报价、已确认条件与实际任务表现"),
            "measurement" => ("任务测量", "在同一任务下重复记录运行耗时，核对候选电脑的实际表现"),
            _ => ("实时概览", "当前占用、硬件状态与正在运行的应用")
        };
        if (page is "advice" or "history") _ = RefreshAnalysis();
    }
    private void LiveNav_Click(object sender, RoutedEventArgs e) => Navigate("live");
    private void AdviceNav_Click(object sender, RoutedEventArgs e) => Navigate("advice");
    private async void ComparisonNav_Click(object sender, RoutedEventArgs e) { await RefreshAnalysis(); Navigate("comparison"); }
    private void TaskMeasurementNav_Click(object sender, RoutedEventArgs e) => Navigate("measurement");
    private void HistoryNav_Click(object sender, RoutedEventArgs e) => Navigate("history");
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => Navigate("settings");
    private void Pause_Click(object sender, RoutedEventArgs e) => TogglePause();
    private void TogglePause()
    {
        if (protectionWindow != null && paused) { StatusLine.Text = "备份与恢复窗口打开期间暂停监测，请先完成或关闭该窗口。"; return; }
        if (semanticLocalRequest && paused) { StatusLine.Text = "本机模型正在解析，结束并停止服务后再继续监测。"; return; }
        if (measurementActive && paused) { StatusLine.Text = "任务测量仍在运行，结束并核对任务进程后再继续监测。"; return; }
        lock (recordingGate) { paused = !paused; recordingGeneration++; }
        experienceContext.Clear();
        PauseButton.Content = paused ? "继续记录" : "暂停记录";
        RecordState.Text = paused ? "● 已暂停" : "● 正在恢复";
        tray.Text = paused ? "PerfCompass · 已暂停" : "PerfCompass · 正在记录";
        StatusLine.Text = paused ? "已暂停采集与历史保存。点击继续记录恢复。" : "正在恢复采集…";
    }
    private void Scene_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || restoringSceneSelection || SceneCombo.SelectedItem is not ComboBoxItem item) return;
        if (!TrySaveSettings("scene", store.Settings with { Scene = item.Content.ToString()! }, "场景未切换，仍按原场景记录。检查文件占用或目录权限后重新选择。")) SelectSavedScene();
    }
    private void Window_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (initialized && WindowCombo.SelectedItem is ComboBoxItem item) { windowDays = int.Parse(item.Tag.ToString()!); _ = RefreshAnalysis(); }
    }
    private void AnalysisScene_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || updatingSceneChoices || AnalysisSceneCombo.SelectedItem is not ComboBoxItem item) return;
        analysisScene = item.Tag as string; _ = RefreshAnalysis();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAnalysis();
    private async void RecordExperience_Click(object sender, RoutedEventArgs e)
    {
        if (collector == null || exiting) { ExperienceStatus.Text = "请等待采集器完成初始化。"; return; }
        RecordExperienceButton.IsEnabled = false;
        try
        {
            string symptom = ((ComboBoxItem)ExperienceKind.SelectedItem).Content.ToString()!;
            var value = ExperienceAnalyzer.Create(DateTimeOffset.Now, collector.HardwareId, store.Settings.Scene, symptom,
                ExperienceNote.Text, paused ? [] : experienceContext.ToArray());
            await Task.Run(() => store.AppendExperience(value));
            ExperienceNote.Clear();
            ExperienceStatus.Text = "已保存。" + value.Conclusion;
            await RefreshAnalysis();
        }
        catch (Exception ex) { store.Log(ex); ExperienceStatus.Text = "保存失败，请检查数据目录权限后重试。"; }
        finally { RecordExperienceButton.IsEnabled = true; }
    }
    private async void ResolveExperience_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ExperienceEvent value } button || value.Resolved) return;
        button.IsEnabled = false;
        try { await Task.Run(() => store.AppendExperience(value with { Resolved = true })); await RefreshAnalysis(); }
        catch (Exception ex) { store.Log(ex); StatusLine.Text = "体验状态保存失败，请重试。"; button.IsEnabled = true; }
    }
    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        RequirementsPlan? confirmed = null;
        if (RequirementsConfirm.IsChecked == true)
        {
            if (requirementDraft == null || requirementDraft.Source != FutureBox.Text.Trim())
            { RequirementsStatus.Text = "描述已改变，请先重新解析并核对。"; return; }
            try { confirmed = RequirementParser.Confirm(requirementDraft, ((ComboBoxItem)AiModeCombo.SelectedItem).Tag.ToString()!); }
            catch (ArgumentException ex) { RequirementsStatus.Text = ex.Message; return; }
        }
        Settings selected;
        try { selected = WorkloadCoverage.Select(store.Settings, RepresentativeCheck.IsChecked == true, collector?.HardwareId); }
        catch (ArgumentException ex) { RequirementsStatus.Text = ex.Message; return; }
        if (!TrySaveSettings("requirements", selected with { FutureNeeds = FutureBox.Text.Trim(), Requirements = confirmed,
            SemanticRequirements = confirmed != null || store.Settings.SemanticRequirements?.Source != FutureBox.Text.Trim() ? null : store.Settings.SemanticRequirements },
            "需求设置未保存，输入已保留。请检查文件占用、目录权限或磁盘空间后重试。"))
        { RequirementsStatus.Text = settingsFailures["requirements"]; return; }
        RequirementsStatus.Text = confirmed == null ? "描述已保存，尚未确认的解析结果不参与配置建议。" : "已保存确认的需求，将用于配置建议；修改描述后需要重新解析。";
        StatusLine.Text = "设置已保存。"; await RefreshAnalysis();
    }
    private void ParseNeeds_Click(object sender, RoutedEventArgs e)
    { ShowRequirementDraft(RequirementParser.Parse(FutureBox.Text)); RequirementsConfirm.IsChecked = false; }
    private void ShowRequirementDraft(RequirementsPlan plan)
    {
        requirementDraft = plan;
        RequirementsList.ItemsSource = plan.Facts.Select(f => $"{(f.Excluded ? "排除任务" : f.Future ? "未来需求" : "任务线索")}：{f.Label}\n依据：{f.Evidence}").Concat(plan.UnparsedClauses.Select(s => "未解析，不参与建议：" + s));
        AiModeCombo.SelectedItem = AiModeCombo.Items.OfType<ComboBoxItem>().First(i => i.Tag.ToString() == plan.AiMode);
        AiModePanel.Visibility = plan.Facts.Any(f => f.Key == "ai" && !f.Excluded) ? Visibility.Visible : Visibility.Collapsed;
        RequirementsStatus.Text = plan.Facts.Count == 0 ? "规则未识别到支持的任务，原文会保留。此入口尚非通用语义理解。" : "当前为有限规则预解析，可能遗漏条件、时间与数值。请逐项核对；只有列出的字段在确认保存后参与建议。";
    }
    private void FutureBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (!initialized) return;
        ResetSemanticDraft();
        requirementDraft = null; RequirementsList.ItemsSource = null; RequirementsConfirm.IsChecked = false;
        RequirementsStatus.Text = "描述已修改，请重新解析。";
    }
    private void OpenData_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(store.Root) { UseShellExecute = true });
    private void Help_Click(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "README.html");
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else System.Windows.MessageBox.Show("打开项目目录中的 README.md 查看使用说明与分析方法。", "PerfCompass");
    }
    private async void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        while (analyzing) await Task.Delay(50);
        await RefreshAnalysis(); if (report == null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "HTML 报告|*.html|JSON 数据|*.json", FileName = $"PerfCompass-配置报告-{DateTime.Now:yyyyMMdd}" };
        if (dialog.ShowDialog(this) != true) return;
        var current = report;
        await File.WriteAllTextAsync(dialog.FileName, dialog.FilterIndex == 2 ? JsonSerializer.Serialize(current, new JsonSerializerOptions { WriteIndented = true }) : ReportExporter.Html(current, live), new UTF8Encoding(false));
        StatusLine.Text = $"报告已导出：{dialog.FileName}";
    }
    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV 数据|*.csv", FileName = $"PerfCompass-采样-{DateTime.Now:yyyyMMdd}.csv" };
        if (dialog.ShowDialog(this) != true) return;
        string? id = collector?.HardwareId; string? scene = analysisScene; int days = windowDays;
        await Task.Run(() => ReportExporter.Csv(dialog.FileName, store.Read(days, id).Where(s => scene == null || AnalysisEngine.NormalizeScene(s.Scene) == scene)));
        StatusLine.Text = $"CSV 已导出：{dialog.FileName}";
    }
    private void SaveBackground_Click(object sender, RoutedEventArgs e)
    {
        int retention = RetentionCombo.SelectedItem is ComboBoxItem choice ? int.Parse(choice.Tag.ToString()!) : store.Settings.RetentionDays;
        try { BackgroundSettings.Save(store, store.Settings with { CloseToTray = CloseToTrayCheck.IsChecked == true, RetentionDays = retention }, StartupCheck.IsChecked == true, ReadStartup, WriteStartup); }
        catch (BackgroundSettingsException error)
        {
            string message = error.RollbackFailed ? "后台设置未保存，自启动状态未能确认，请核对 Windows 启动应用后重试。" : "后台设置未保存，自启动已恢复原状态。输入已保留，请检查数据目录后重试。";
            SettingsFailure("background", message, error); BackgroundSaveStatus.Text = message; return;
        }
        catch (Exception error) { const string message = "后台设置未保存。请检查启动设置权限和数据目录后重试。"; SettingsFailure("background", message, error); BackgroundSaveStatus.Text = message; return; }
        ClearSettingsFailure("background"); ClearSettingsFailure("startup-read");
        RefreshDataProtection();
        RetentionLabel.Text = $"当前保存最近 {retention} 天 · 实时约 2 秒 · 历史记录约 10 秒";
        BackgroundSaveStatus.Text = "后台设置已保存。"; StatusLine.Text = "后台设置已保存。";
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true;
        if (store.Settings.CloseToTray) Hide(); else Exit();
    }
    private void Restore() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void Exit_Click(object sender, RoutedEventArgs e) => Exit();
    private async void Exit()
    {
        if (exiting) return;
        if (protectionWindow is { } maintenance) { maintenance.Close(); if (maintenance.Busy) return; }
        exiting = true; semanticCancellation?.Cancel(); TaskMeasurementPage.Cancel(); ModelApiKey.Clear(); stop.Cancel(); tray.Visible = false; tray.Dispose();
        if (TaskMeasurementPage.Running) await Task.WhenAny(TaskMeasurementPage.Completion, Task.Delay(7000));
        if (collectionTask != null)
        {
            var finished = await Task.WhenAny(collectionTask, Task.Delay(5000));
            if (finished != collectionTask) store.Log(new TimeoutException("Collector shutdown exceeded five seconds."));
        }
        Close(); System.Windows.Application.Current.Shutdown(Environment.ExitCode);
    }
}
