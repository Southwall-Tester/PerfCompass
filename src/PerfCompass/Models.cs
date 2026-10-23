namespace PerfCompass;

public record Settings(string Scene = "日常办公", bool Representative = false, string FutureNeeds = "", int RetentionDays = 90, bool CloseToTray = true, RequirementsPlan? Requirements = null, SemanticPlan? SemanticRequirements = null, string ModelEndpoint = "", string ModelName = "")
{
    public string RepresentativeHardwareId { get; init; } = "";
    public string RepresentativeObservationVersion { get; init; } = "";
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalFields { get; init; }
}
// Legacy persisted names: *Gb capacity fields use GiB; *Mbps throughput fields use MiB/s.
// Keep these names to read old histories without changing or rescaling their values.
public record DiskInfo(string Name, double TotalGb, double UsedGb) { public double Percent => TotalGb > 0 ? UsedGb / TotalGb * 100 : 0; }
public record AppUsage(string Name, double Cpu, double MemoryMb);
public record Sample
{
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;
    public string HardwareId { get; init; } = "";
    public string Scene { get; init; } = "日常办公";
    public double Seconds { get; init; }
    public string SamplingIssue { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public double? UniqueObservationSeconds { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public double? UniqueSceneSeconds { get; init; }
    public double? CpuCoverage { get; init; }
    public double? CoreCoverage { get; init; }
    public double? MemoryCoverage { get; init; }
    public double? CommitCoverage { get; init; }
    public double? GpuCoverage { get; init; }
    public bool Active { get; init; }
    public string ObservationVersion { get; init; } = "";
    public double? Cpu { get; init; }
    public double? CoreMax { get; init; }
    public double? MemoryUsedGb { get; init; }
    public double? MemoryPeakGb { get; init; }
    public double? MemoryTotalGb { get; init; }
    public double? CommitGb { get; init; }
    public double? Gpu { get; init; }
    public string GpuEngineVersion { get; init; } = "";
    public List<GpuEngineSample>? GpuEngines { get; init; }
    public string GpuMetricSource { get; init; } = "";
    public double? GpuPeak { get; init; }
    public double? CpuHighSeconds { get; init; }
    public double? GpuHeavySeconds { get; init; }
    public double? GpuHighSeconds { get; init; }
    public double? MemoryHighSeconds { get; init; }
    public double? VramUsedGb { get; init; }
    public double? VramTotalGb { get; init; }
    public double? CpuTemp { get; init; }
    public double? GpuTemp { get; init; }
    public double? DiskBusy { get; init; }
    public double? DiskReadMbps { get; init; }
    public double? DiskWriteMbps { get; init; }
    public double MaxDiskPercent { get; init; }
    public double DiskUsedGb { get; init; }
    public double DiskTotalGb { get; init; }
}
public record LiveSnapshot(Sample Sample, string CpuName, string GpuName, string SensorStatus, List<DiskInfo> Disks, List<AppUsage> Apps)
{
    public string[] SensorFailures { get; init; } = [];
    public string CpuObservationIssue { get; init; } = "";
}
public record GpuEngineSample(string Kind, string[] Sources, double? Load)
{
    public bool Complete { get; init; }
    public double? Peak { get; init; }
    public double? ObservedSeconds { get; init; }
    public double? CoverageSeconds { get; init; }
    public double? HeavySeconds { get; init; }
    public double? HighSeconds { get; init; }
}
public record GpuEngineProfile(string Kind, string Name, double ObservedHours, double CompleteHours,
    double? P95, double? Peak, double HeavyMinutes, string[] Sources)
{
    public string[] SourceScopes { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasMixedSources => SourceScopes.Distinct(StringComparer.Ordinal).Skip(1).Any();
    [System.Text.Json.Serialization.JsonIgnore]
    public string ScopeNotice => HasMixedSources ? "来源或规则混合，未合并 P95；峰值与重载时长仅保留为已观察线索。" : "";
}
public record ResourceObservation(string State, string Title, string Evidence, string Action, string Scope, double ObservedHours, double Coverage);

public record Advice(string Key, string Name, string Status, string Title, string Evidence, string Suggestion, string Limits)
{
    public ResourceObservation? Observation { get; init; }
    public ResourceObservation? RetainedWorkload { get; init; }
    public bool UsesRetainedWorkload => RetainedWorkload != null && Status is ("unknown" or "fit") && Observation?.State != "busy";
    public bool UsesObservation => !UsesRetainedWorkload && Observation != null && Status is "unknown" or "fit";
    public string DisplayTitle => UsesRetainedWorkload ? RetainedWorkload!.Title : UsesObservation ? Observation!.Title : Title;
    public string DisplaySuggestion => UsesRetainedWorkload ? RetainedWorkload!.Action : UsesObservation ? Observation!.Action : Suggestion;
    public string StatusLabel => UsesRetainedWorkload || UsesObservation ? "初步判断" : Status switch
    {
        "surplus" => "疑似过剩", "pressure" => "压力线索", "fit" => "保持观察", "changed" => "近期需求增加",
        "review" => "可比较降档", "experience" => "体验待核对", "requirements" => "新需求待验证",
        "constraints" => "已确认条件", _ => "等待观测"
    };
}
public record HistoryPoint(DateTimeOffset Time, double? Cpu, double? Memory, double? Gpu)
{
    public string GpuScope { get; init; } = "";
    public double? CpuObservedSeconds { get; init; }
    public double? MemoryObservedSeconds { get; init; }
    public double? GpuObservedSeconds { get; init; }
    public bool CpuCoverageEstimated { get; init; }
    public bool MemoryCoverageEstimated { get; init; }
    public bool GpuCoverageEstimated { get; init; }
    public string PeriodLabel => $"{Time.LocalDateTime:yyyy-MM-dd HH:mm}–{Time.AddHours(1).LocalDateTime:HH:mm}";
    public string CpuReadingLabel => Reading("CPU", Cpu, CpuObservedSeconds, CpuCoverageEstimated);
    public string MemoryReadingLabel => Reading("内存", Memory, MemoryObservedSeconds, MemoryCoverageEstimated);
    public string GpuReadingLabel => Reading("GPU 核心/3D", Gpu, GpuObservedSeconds, GpuCoverageEstimated);
    public string DetailsLabel => string.Join("\n", CpuReadingLabel, MemoryReadingLabel, GpuReadingLabel);
    private static string Reading(string name, double? value, double? seconds, bool estimated)
    {
        string duration = !seconds.HasValue ? "时长未记录" : seconds < 60 ? $"{seconds:0.###} 秒" : $"{seconds / 60:0.0} 分钟";
        return value.HasValue ? $"{name} {value:0.0}% · 读数 {duration}" + (estimated ? "（部分读数按区间比例分摊）" : "")
            : $"{name} — · 无可用均值";
    }
}
public record SceneProfile(string Name, int SampleCount, double ActiveHours, double? CpuP95, double? CoreP95,
    double? MemoryPeakGb, double? GpuP95, double? GpuPeak, double CpuCoverage, double GpuCoverage)
{
    public string[] GpuMetricScopes { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public bool GpuHasMixedSources => GpuMetricScopes.Length > 1;
    [System.Text.Json.Serialization.JsonIgnore]
    public string DurationLabel => DurationText.FromHours(ActiveHours);
}
public static class DurationText
{
    public static string FromHours(double hours) => hours <= 0 ? "0 分钟" : hours < 1d / 60 ? $"{hours * 3600:0} 秒" : hours < 1 ? $"{hours * 60:0.0} 分钟" : $"{hours:0.0} 小时";
}
public record DemandTrend(string Key, string Name, string Unit, int Days, string Status,
    double? RecentP95, double? PreviousP95, double RecentHours, double PreviousHours,
    double RecentBusyMinutes, double PreviousBusyMinutes, int RecentActiveDays, int PreviousActiveDays,
    string[] RecentHeavyScenes, string Summary, string Suggestion)
{
    private bool loadStatisticsAvailable = true;
    public bool LoadStatisticsAvailable { get => loadStatisticsAvailable && StatisticsUnavailableReason.Length == 0; init => loadStatisticsAvailable = value; }
    public string StatisticsUnavailableReason { get; init; } = "";
    public string StatusLabel => Status switch { "increased" => "近期增加", "decreased" => "近期减少", "similar" => "相近", _ => "对照积累中" };
    public string ComparisonLabel => $"最近 {Days} 天 / 此前 {Days} 天";
    public string ReadingsLabel => $"P95：{Format(RecentP95)} / {Format(PreviousP95)} · 有效观测 {DurationText.FromHours(RecentHours)} / {DurationText.FromHours(PreviousHours)}";
    public string BusyLabel => !LoadStatisticsAvailable ? (StatisticsUnavailableReason.Length > 0 ? StatisticsUnavailableReason : "重叠时段的负载比例无法确认") : Key == "memory" ? "" : $"{(Key == "cpu" ? "批次平均负载" : "负载")} ≥80% 累计 {RecentBusyMinutes:0.0} / {PreviousBusyMinutes:0.0} 分钟（有效观测占比 {Share(RecentBusyMinutes, RecentHours)} / {Share(PreviousBusyMinutes, PreviousHours)}）";
    public string SceneLabel => LoadStatisticsAvailable && RecentHeavyScenes.Length > 0 ? "近期重负载场景：" + string.Join("、", RecentHeavyScenes) : "";
    private string Format(double? value) => value.HasValue ? $"{value:0.0}{Unit}" : "缺失";
    private static string Share(double minutes, double hours) => hours > 0 ? $"{minutes / (hours * 60) * 100:0.0}%" : "缺失";
}
public record AnalysisReport(DateTimeOffset Generated, int WindowDays, int SampleCount, double ActiveHours, int ActiveDays,
    bool Ready, string[] Scenes, List<Advice> Cards, List<HistoryPoint> History, string FutureNeeds)
{
    public List<SceneProfile> SceneProfiles { get; init; } = [];
    public List<GpuEngineProfile> GpuEngineProfiles { get; init; } = [];
    public string[] GpuMetricScopes { get; init; } = [];
    public bool GpuHistoryHasMixedSources { get; init; }
    public string? SelectedScene { get; init; }
    public List<DemandTrend> DemandTrends { get; init; } = [];
    public List<ExperienceEvent> ExperienceEvents { get; init; } = [];
    public RequirementsPlan? ConfirmedRequirements { get; init; }
    public SemanticPlan? SemanticRequirements { get; init; }
    public double FreshActiveHours { get; init; }
    public int FreshActiveDays { get; init; }
    public string HardwareId { get; init; } = "";
    public bool RepresentativeConfirmed { get; init; }
    public string CoverageMessage { get; init; } = "";
    public string MethodVersion { get; init; } = "1.15";
    public HistoryQuality HistoryQuality { get; init; } = HistoryQuality.Unchecked;
    public int SamplingGapCount { get; init; }
    public string SamplingGapSummary => SamplingGapCount > 0 ? $"本范围记录到 {SamplingGapCount} 次采样中断；中断段未计入有效使用，也未作为空闲负载。" : "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string DurationLabel => DurationText.FromHours(ActiveHours);
    public string Level => Ready ? "较充分" : ActiveHours >= .5 ? "初步观察" : "积累中";
    public string Summary => HistoryQuality.HasIssues ? "历史记录存在异常，先核对数据再判断配置余量" : Cards.Any(c => c.Status == "experience") ? "近期有体验问题，先核对原因再评估余量" : Cards.Any(c => c.Status == "requirements") ? "已确认配置目标，先核对候选机器的表现" : DemandTrends.Any(t => t.Status == "increased") ? "近期任务负载增加，优先核对新的配置需求" : Ready ? $"{Cards.Count(c => c.Status == "surplus")} 项疑似过剩 · {Cards.Count(c => c.Status == "review")} 项可比较降档 · {Cards.Count(c => c.Status == "pressure")} 项压力线索" : Cards.Any(c => c.UsesObservation || c.UsesRetainedWorkload)
        ? Cards.Any(c => c.UsesObservation && c.Observation?.State is ("light" or "busy") || c.UsesRetainedWorkload)
            ? $"初步判断：{Cards.Count(c => c.UsesObservation && c.Observation?.State == "light")} 项有余量线索 · {Cards.Count(c => c.UsesObservation && c.Observation?.State == "busy")} 项曾出现繁忙"
                + (Cards.Any(c => c.UsesRetainedWorkload) ? $" · {Cards.Count(c => c.UsesRetainedWorkload)} 项保留重任务" : "")
            : $"已形成 {Cards.Count(c => c.UsesObservation)} 项资源使用判断，查看各项实际负载"
        : "正在记录任务，采到负载后逐项给出判断";
}
