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
    private volatile bool exiting;
    private bool initialized, analyzing, refreshRequested;
    private bool exitPending;
    internal Func<bool>? DiscardUnsavedMeasurementOnExit;
    internal Func<bool>? DiscardUnsavedComparisonOnExit;
    private const string UnsavedComparisonNotice = "候选草稿已保留。请保存或导出后再退出。";
    private const string UnsavedMeasurementNotice = "测量结果已保留。请先导出，再退出程序。";
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
    private readonly ElapsedSchedule analysisSchedule = new(TimeSpan.FromSeconds(60));
    private string activePage = "live";
    private volatile bool historyWriteFailed;
    public MainWindow(LocalStore store)
    {
        this.store = store;
        InitializeComponent();
        liveUpdates = new(PostLiveUpdate, ApplyLiveUpdates);
        ApplicationVersionLabel.Text = "本地记录 · v" + typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        ComparisonPage.OpenTaskMeasurement = () => Navigate("measurement");
        ComparisonPage.CurrentTaskMeasurement = () => TaskMeasurementPage.Running ? null : TaskMeasurementPage.LastResult;
        TaskMeasurementPage.OpenCandidateComparison = () => Navigate("comparison");
        ComparisonPage.DraftPersisted = () =>
        {
            if (StatusLine.Text == UnsavedComparisonNotice) StatusLine.Text = "候选草稿已保存或导出。";
        };
        TaskMeasurementPage.ResultPersisted = () =>
        {
            if (StatusLine.Text == UnsavedMeasurementNotice) StatusLine.Text = "测量结果已导出。";
        };
        TaskMeasurementPage.Initialize(store,
            () => new(live?.CpuName ?? "", live?.GpuName ?? "", live?.Sample.MemoryTotalGb, Environment.OSVersion.VersionString, TaskMeasurements.PowerSource(), TaskMeasurementPage.DeclaredPowerProfile),
            active =>
            {
                if (active && semanticCancellation != null) throw new ArgumentException("模型解析尚未结束，请结束后再进行测量操作。");
                bool wasActive = measurementActive; measurementActive = active;
                if (active && !paused) TogglePause();
                if (!active && wasActive) StatusLine.Text = "测量操作已结束。若启动过任务，请核对其进程后再继续监测。";
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
        if (store.RequiresPausedStart || Environment.GetCommandLineArgs().Contains("--paused"))
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
                var validation = ValidationRunOptions.Parse(Environment.GetCommandLineArgs());
                if (validation?.Mode == "soak") soakTask = Task.Run(async () =>
                {
                    try { await RunSoakTest(validation); }
                    catch (Exception error) { store.Log(error); soakFinished = true; Environment.ExitCode = 1; await Dispatcher.InvokeAsync(Exit); }
                });
            }
            catch (Exception ex) { store.Log(ex); StatusLine.Text = "采集启动失败，请查看 data/errors.log 后重试。"; RecordState.Text = "● 启动失败"; }
        };
    }
    private async Task CollectionLoop()
    {
        var batch = new List<Sample>();
        int generation = recordingGeneration;
        var cleanupSchedule = new ElapsedSchedule(TimeSpan.FromHours(1)); cleanupSchedule.Mark(Stopwatch.GetTimestamp());
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(2000, stop.Token);
                if (store.ObserveRetentionTime()) RequestProtectionRefresh();
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
                        if (snapshot.Sample.Seconds == 0 || batch.Count >= 5) { store.Append(SampleBatch.Merge(batch)); batch.Clear(); historyWriteFailed = false; }
                    }
                    if (cleanupSchedule.Due(Stopwatch.GetTimestamp())) { store.Cleanup(); cleanupSchedule.Mark(Stopwatch.GetTimestamp()); }
                    PublishLive(snapshot, generation);
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
                    PublishCollectionFailure(generation);
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
    private void RenderLiveSnapshot(LiveSnapshot snapshot)
    {
        if (paused || exiting) return;
        live = snapshot;
        var s = snapshot.Sample;
        CpuValue.Text = Percent(s.Cpu); CpuBar.Value = s.Cpu ?? 0;
        CpuDetail.Text = $"最忙核心 {Percent(s.CoreMax)}" + (s.CpuTemp.HasValue ? $" · {s.CpuTemp:0}°C" : "");
        double? memPct = s.MemoryTotalGb > 0 ? s.MemoryUsedGb / s.MemoryTotalGb * 100 : null;
        MemoryValue.Text = Percent(memPct); MemoryBar.Value = memPct ?? 0;
        MemoryDetail.Text = $"{s.MemoryUsedGb:0.0} / {s.MemoryTotalGb:0.0} GiB";
        GpuValue.Text = Percent(s.Gpu); GpuBar.Value = s.Gpu ?? 0;
        GpuDetail.Text = s.Gpu.HasValue ? (s.VramUsedGb.HasValue ? $"显存 {s.VramUsedGb:0.0} GiB" : "显存数据待采集") + (s.GpuTemp.HasValue ? $" · {s.GpuTemp:0}°C" : "") : "核心/3D传感器不可用";
        GpuEngineLiveDetails.Text = GpuEngineEvidence.Describe(s);
        GpuEngineLiveVersion.Text = $"核心/3D来源：{(string.IsNullOrWhiteSpace(s.GpuMetricSource) ? "未取得或旧版未标注" : s.GpuMetricSource)} · 分引擎规则：{(string.IsNullOrWhiteSpace(s.GpuEngineVersion) ? "未记录" : s.GpuEngineVersion)}。";
        DiskValue.Text = Percent(s.DiskBusy); DiskBar.Value = s.DiskBusy ?? 0;
        DiskDetail.Text = s.DiskReadMbps.HasValue ? $"读 {s.DiskReadMbps:0.0} / 写 {s.DiskWriteMbps:0.0} MiB/s" : "计数器不可用";
        CpuNameLabel.Text = snapshot.CpuName; GpuNameLabel.Text = snapshot.GpuName;
        DriveLabel.Text = string.Join("\n", snapshot.Disks.Select(d => $"{d.Name}  {d.UsedGb:0} / {d.TotalGb:0} GiB"));
        AppList.ItemsSource = snapshot.Apps;
        SensorLabel.Text = snapshot.SensorStatus;
        SensorHealthBanner.Text = snapshot.SensorFailures.Length == 0 ? "" : "本次传感器更新失败：" + string.Join("、", snapshot.SensorFailures) + "。对应读数留空，基础采集继续；下次自动重试。";
        SensorHealthBanner.Visibility = snapshot.SensorFailures.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CpuObservationBanner.Text = snapshot.CpuObservationIssue;
        CpuObservationBanner.Visibility = snapshot.CpuObservationIssue.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        SamplingTimingBanner.Text = SamplingClock.Describe(s.SamplingIssue);
        SamplingTimingBanner.Visibility = s.SamplingIssue.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecordState.Text = "● " + (s.SamplingIssue.Length > 0 ? "采样时间中断" : s.Active ? "正在记录 · 有效使用" : "正在记录 · 空闲");
        LastTime.Text = $"更新于 {s.Time.LocalDateTime:HH:mm:ss}";
        StatusLine.Text = historyWriteFailed ? "实时采集继续，历史保存失败。请检查数据目录权限与磁盘空间；程序会自动重试。" : $"采集继续 · 历史记录约 10 秒保存 · {snapshot.SensorStatus}";
        livePoints.Add(new(s.Time, s.Cpu, memPct, s.Gpu) { GpuScope = GpuEngineEvidence.MetricScope(s) });
        livePoints.RemoveAll(p => p.Time < s.Time.AddMinutes(-4));
        LiveChart.SetPoints(livePoints);
        if (s.SamplingIssue.Length > 0 || !analyzing && analysisSchedule.Due(Stopwatch.GetTimestamp())) _ = RefreshAnalysis();
    }
    private void PublishAnalysis(AnalysisReport result, int days, string? scene)
    {
        RefreshDataProtection();
        HistoryQualityNotice.Visibility = result.HistoryQuality.HasNotices ? Visibility.Visible : Visibility.Collapsed;
        HistoryQualityNotice.Header = result.HistoryQuality.HasIssues ? $"历史记录异常 · {result.HistoryQuality.IssueCount} 处（点击查看位置）" : $"已排除重复记录 · {result.HistoryQuality.DuplicateCount} 条（点击查看位置）";
        HistoryQualityDetails.Text = result.HistoryQuality.Message + "\n" + result.HistoryQuality.Details + "\n原文件保留。可在记录设置备份，并从已核对的备份恢复到新目录。";
        ComparisonPage.RefreshLoadedContext(store, result);
        ExperienceQualityNotice.Visibility = ExperienceHistoryQualityNotice.Visibility = result.ExperienceQuality.HasIssues ? Visibility.Visible : Visibility.Collapsed;
        ExperienceQualityDetails.Text = ExperienceHistoryQualityNotice.Text = result.ExperienceQuality.Message + "\n" + result.ExperienceQuality.Details;
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
        if (!result.HistoryQuality.HasIntervalConflicts && result.SceneProfiles.Any(p => p.GpuHasMixedSources))
            SceneProfileSummary.Text += "。来源或规则混合的场景不合并 GPU P95；峰值仅保留为已观察线索：" + string.Join("、", result.SceneProfiles.Where(p => p.GpuHasMixedSources).Select(p => p.Name));
        SceneEmptyLabel.Visibility = result.SceneProfiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GpuEngineHistoryNotice.Text = result.HistoryQuality.HasIntervalConflicts ? "时段重叠，分引擎负载与时长暂不解释；请先核对历史来源。"
            : string.IsNullOrWhiteSpace(result.HardwareId) ? "请先隔离同一硬件配置的记录，再查看分引擎历史。"
            : "仅统计当前范围已暴露的引擎节点。P95 按同来源读数的时长加权；来源或规则混合时不合并 P95。部分读数可能低估同类最大负载；类别完整时长不等于全GPU覆盖。时期对照仅用同来源、读数完整的观测，可能与这里的 P95 不同。缺失保留未知，不同引擎占用不能相加。"
                + (result.GpuMetricScopes.Length > 0 ? " 核心/3D观测来源：" + string.Join("；", result.GpuMetricScopes) : "");
        GpuEngineProfileList.ItemsSource = result.GpuEngineProfiles
            .OrderByDescending(p => p.ObservedHours > 0 || p.Sources.Length > 0)
            .ThenByDescending(p => p.HeavyMinutes > 0 || p.Peak >= 80)
            .ThenByDescending(p => p.HeavyMinutes).ThenByDescending(p => p.Peak).ThenByDescending(p => p.ObservedHours)
            .Select(p => new
        {
            p.Name,
            Readings = $"已观察 P95 {(p.P95.HasValue ? $"{p.P95:0.0}%" : p.HasMixedSources ? "来源混合，未合并" : "缺失")} · 峰值 {(p.Peak.HasValue ? $"{p.Peak:0.0}%" : "缺失")} · ≥80% 累计 {p.HeavyMinutes:0.0} 分钟" + (p.HasMixedSources ? "；" + p.ScopeNotice : ""),
            Durations = $"已观察 {DurationText.FromHours(p.ObservedHours)} · 类别完整 {DurationText.FromHours(p.CompleteHours)}",
            Sources = p.Sources.Length == 0 ? "来源：未取得该类别记录；不能解释为零负载或不支持该功能。" : "来源：" + string.Join("、", p.Sources)
        }).ToList();
        DemandTrendList.ItemsSource = result.DemandTrends;
        var changed = result.DemandTrends.Where(t => t.Status == "increased").OrderBy(t => t.Days).FirstOrDefault();
        DemandTrendHeader.Text = changed == null ? "近期需求变化 · " + (result.DemandTrends.Any(t => t.Status == "similar" || t.Status == "decreased") ? "查看时期对照" : "对照记录积累中") : $"近期需求变化 · {changed.Name}增加（{changed.Days} 天对照）";
        AnalysisSummary.Text = result.Summary;
        PurchaseGuidanceList.ItemsSource = PurchaseGuidance.Build(result);
        ConfigurationDraftPanel.DataContext = ConfigurationDraft.Build(result);
        ConfigurationDraftPanel.Visibility = Visibility.Visible;
        CoverageLabel.Text = $"{scene ?? "全部场景"} · {result.Level}  ·  有效使用 {result.DurationLabel}  ·  覆盖 {result.ActiveDays} 天  ·  {result.SampleCount:N0} 条记录";
        RepresentativeStatus.Text = result.CoverageMessage;
        CoverageNext.Text = result.ExperienceQuality.HasIssues ? result.ExperienceQuality.Message : result.HistoryQuality.HasIssues ? result.HistoryQuality.Message : result.SemanticUseNotice.Length > 0 ? result.SemanticUseNotice : result.Ready ? "已覆盖典型任务，可比较降档方向；近期新增任务会更新判断。" : result.Cards.Any(c => c.Observation != null)
            ? "下方已给出当前任务的判断。完成常用与偶发任务后，在记录设置确认覆盖范围，进一步评估配置预算。"
            : "正常使用电脑即可积累观测；发现繁忙任务会先显示，轻负载判断从有效记录中逐项形成。";
        var expanded = AdviceList.Items.OfType<Border>().Select(b => b.Child).OfType<StackPanel>()
            .SelectMany(s => s.Children.OfType<Expander>()).Where(x => x.IsExpanded).Select(x => x.Tag?.ToString()).ToHashSet();
        AdviceList.Items.Clear();
        var memoryCapacity = MemoryCapacityPreview.From(result);
        var storageCapacity = StorageCapacityPreview.From(result);
        var gpuPurchase = GpuPurchasePlan.Build(result);
        foreach (var advice in result.Cards) AdviceList.Items.Add(AdviceCard(advice, expanded.Contains(advice.Key), memoryCapacity, expanded.Contains("memory-capacity"), storageCapacity, expanded.Contains("storage-capacity"), gpuPurchase, expanded.Contains("gpu-purchase")));
        HistoryChart.SetPoints(result.History);
        var selectedPeriod = (HistoryPeriodCombo.SelectedItem as HistoryPoint)?.Time;
        HistoryPeriodCombo.ItemsSource = result.History.AsEnumerable().Reverse().ToArray();
        HistoryPeriodCombo.IsEnabled = result.History.Count > 0;
        HistoryPeriodCombo.SelectedItem = result.History.FirstOrDefault(p => p.Time == selectedPeriod) ?? result.History.LastOrDefault();
        HistoryPeriodDetails.Text = (HistoryPeriodCombo.SelectedItem as HistoryPoint)?.DetailsLabel ?? "当前范围没有可显示的小时均值";
        HistorySummary.Text = $"最近 {days} 天 · {result.SampleCount:N0} 条记录 · {scene ?? "全部场景"} · " + (result.HistoryQuality.HasIntervalConflicts ? "时段重叠，历史负载图暂不显示；场景时长可能互相重叠，不能相加。" : result.Scenes.Length > 0 ? string.Join("、", result.Scenes) : "等待有效使用");
        if (!result.HistoryQuality.HasIntervalConflicts && result.GpuHistoryHasMixedSources)
            HistorySummary.Text += "。核心/3D来源或规则混合，GPU 历史曲线暂不显示；CPU、内存曲线独立保留。";
        SamplingGapLabel.Text = result.SamplingGapSummary;
        SamplingGapLabel.Visibility = result.SamplingGapCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private static Brush Brush(string color) => (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
    private void HistoryPeriod_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryPeriodDetails != null)
            HistoryPeriodDetails.Text = (HistoryPeriodCombo.SelectedItem as HistoryPoint)?.DetailsLabel ?? "当前范围没有可显示的小时均值";
    }
    private Border AdviceCard(Advice advice, bool expanded, MemoryCapacityPreview? memoryCapacity = null, bool capacityExpanded = false, StorageCapacityPreview? storageCapacity = null, bool storageExpanded = false, GpuPurchasePlan? gpuPurchase = null, bool gpuPurchaseExpanded = false)
    {
        var stack = new StackPanel();
        string label = advice.StatusLabel;
        string color = advice.Status switch { "surplus" => "#007F73", "pressure" or "changed" => "#BC7132", "review" => "#6378C8", _ => advice.Observation?.State == "light" ? "#007F73" : advice.Observation?.State == "busy" ? "#BC7132" : "#677A87" };
        var header = new DockPanel();
        header.Children.Add(new TextBlock { Text = advice.Name, FontSize = 13, Foreground = Brush("#677A87") });
        header.Children.Add(new TextBlock { Text = label, Foreground = Brush(color), HorizontalAlignment = System.Windows.HorizontalAlignment.Right, FontSize = 12 });
        stack.Children.Add(header);
        stack.Children.Add(new TextBlock { Text = advice.DisplayTitle, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new(0, 10, 0, 10), TextWrapping = TextWrapping.Wrap });
        if ((advice.UsesRetainedWorkload ? advice.RetainedWorkload : advice.Observation) is { } observation)
        {
            stack.Children.Add(new TextBlock { Text = observation.Evidence, Foreground = Brush("#45606C"), LineHeight = 23, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 6) });
            stack.Children.Add(new TextBlock { Text = $"{observation.Scope} · 有效观测 {DurationText.FromHours(observation.ObservedHours)} · 读数覆盖 {observation.Coverage:P0}", Foreground = Brush("#677A87"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) });
        }
        stack.Children.Add(new TextBlock { Text = advice.DisplaySuggestion, Foreground = Brush("#45606C"), LineHeight = 23, TextWrapping = TextWrapping.Wrap });
        if (advice.Key is "cpu" or "memory" or "gpu" or "disk" && (advice.Observation != null || advice.Status is "surplus" or "review" or "pressure" or "changed"))
        {
            var compare = new Button { Tag = "compare:" + advice.Key, Content = "比较候选配置", HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new(0, 12, 0, 0) };
            compare.Click += (_, _) => Navigate("comparison");
            stack.Children.Add(compare);
        }
        if (advice.Key == "gpu" && gpuPurchase != null)
            stack.Children.Add(new Expander { Tag = "gpu-purchase", Header = gpuPurchase.Title, IsExpanded = gpuPurchaseExpanded,
                Content = GpuPurchaseView(gpuPurchase), Margin = new(0, 12, 0, 0), Foreground = Brush("#007F73") });
        if (advice.Key == "memory" && memoryCapacity != null)
            stack.Children.Add(new Expander { Tag = "memory-capacity", Header = "查看各容量与已测峰值的差额", IsExpanded = capacityExpanded,
                Content = MemoryCapacityView(memoryCapacity), Margin = new(0, 12, 0, 0), Foreground = Brush("#007F73") });
        if (advice.Key == "disk" && storageCapacity != null)
            stack.Children.Add(new Expander { Tag="storage-capacity",Header="比较标称容量与已测占用",IsExpanded=storageExpanded,
                Content=StorageCapacityView(storageCapacity),Margin=new(0,12,0,0),Foreground=Brush("#007F73") });
        var detail = new StackPanel();
        if (advice.RetainedWorkload is { } retained)
            detail.Children.Add(new TextBlock { Text = retained.Evidence + "\n" + retained.Scope, Margin = new(0, 12, 0, 8), LineHeight = 22, TextWrapping = TextWrapping.Wrap });
        if (advice.UsesRetainedWorkload && advice.Observation is { } recent)
            detail.Children.Add(new TextBlock { Text = recent.Evidence + "\n" + recent.Scope, Margin = new(0, 0, 0, 8), LineHeight = 22, TextWrapping = TextWrapping.Wrap });
        detail.Children.Add(new TextBlock { Text = advice.Evidence, Margin = new(0, 12, 0, 8), LineHeight = 22 });
        detail.Children.Add(new TextBlock { Text = advice.Limits, Foreground = Brush("#677A87"), FontSize = 12, LineHeight = 21 });
        stack.Children.Add(new Expander { Tag = advice.Key, IsExpanded = expanded, Header = "查看依据与选型条件", Content = detail, Margin = new(0, 16, 0, 0), Foreground = Brush("#007F73") });
        return new Border { Tag = advice.Key, Style = (Style)FindResource("Card"), Child = stack, Margin = new(0, 0, 0, 14) };
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
        if (page == "comparison") { ComparisonPage.InitializeStore(store); if (report != null) ComparisonPage.SetContext(store, report); }
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
    private async void ComparisonNav_Click(object sender, RoutedEventArgs e)
    {
        Navigate("comparison");
        await RefreshAnalysis();
        if (activePage == "comparison" && report != null) ComparisonPage.SetContext(store, report);
    }
    private void TaskMeasurementNav_Click(object sender, RoutedEventArgs e) => Navigate("measurement");
    private void HistoryNav_Click(object sender, RoutedEventArgs e) => Navigate("history");
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => Navigate("settings");
    private void Pause_Click(object sender, RoutedEventArgs e) => TogglePause();
    private void TogglePause()
    {
        if (protectionWindow != null && paused) { StatusLine.Text = "备份与恢复窗口打开期间暂停监测，请先完成或关闭该窗口。"; return; }
        if (semanticLocalRequest && paused) { StatusLine.Text = "本机模型正在解析，结束并停止服务后再继续监测。"; return; }
        if (measurementActive && paused) { StatusLine.Text = "测量操作仍在进行，结束后再继续监测。"; return; }
        lock (recordingGate)
        { paused = !paused; recordingGeneration++; experienceContext.Clear(); pendingTimingInterruption = false; }
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
                ExperienceNote.Text, paused ? [] : RecentExperienceSamples());
            await Task.Run(() => store.AppendExperience(value));
            experienceRevision++;
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
        try { await Task.Run(() => store.AppendExperience(value with { Resolved = true })); experienceRevision++; await RefreshAnalysis(); }
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
        if (exportingReport) return;
        exportingReport = true; reportExportTask = ExportCurrentReport(); await reportExportTask;
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
        if (exiting || exitPending) return;
        if (protectionWindow is { } maintenance) { maintenance.Close(); if (maintenance.Busy) return; }
        exitPending = true;
        semanticCancellation?.Cancel(); TaskMeasurementPage.Cancel();
        if (TaskMeasurementPage.Running) await Task.WhenAny(TaskMeasurementPage.Completion, Task.Delay(7000));
        if (TaskMeasurementPage.Running)
        {
            exitPending = false; Restore(); Navigate("measurement");
            StatusLine.Text = "正在结束测量操作，请等待结果保存后再退出。"; return;
        }
        if (TaskMeasurementPage.HasUnsavedResult)
        {
            Restore(); Navigate("measurement");
            bool discard = DiscardUnsavedMeasurementOnExit?.Invoke() ?? System.Windows.MessageBox.Show(this,
                "上次测量尚未保存。是否放弃这份结果并退出？\n选择“否”返回测量页，可导出到其他目录。", "保留未保存的测量", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            if (!discard)
            {
                exitPending = false; TaskMeasurementPage.TaskResultPanel.BringIntoView();
                StatusLine.Text = UnsavedMeasurementNotice; return;
            }
        }
        if (!ComparisonPage.SaveDraftForExit())
        {
            Restore(); Navigate("comparison");
            bool discard = DiscardUnsavedComparisonOnExit?.Invoke() ?? System.Windows.MessageBox.Show(this,
                "候选比较草稿尚未保存。是否放弃本次修改并退出？\n选择“否”返回候选页，可修正内容或导出到其他目录。", "保留未保存的候选草稿", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            if (!discard)
            {
                exitPending = false; ComparisonPage.DraftStatus.BringIntoView();
                StatusLine.Text = UnsavedComparisonNotice; return;
            }
        }
        exiting = true; liveUpdates.Dispose(); ModelApiKey.Clear(); stop.Cancel(); tray.Visible = false; tray.Dispose();
        if (collectionTask != null)
        {
            var finished = await Task.WhenAny(collectionTask, Task.Delay(5000));
            if (finished != collectionTask) store.Log(new TimeoutException("Collector shutdown exceeded five seconds."));
        }
        if (soakTask != null && !soakFinished)
        {
            var finished = await Task.WhenAny(soakTask, Task.Delay(5000));
            if (finished != soakTask) store.Log(new TimeoutException("Validation result shutdown exceeded five seconds."));
        }
        Close(); System.Windows.Application.Current.Shutdown(Environment.ExitCode);
    }
}
