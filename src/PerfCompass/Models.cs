namespace PerfCompass;

public record Settings(string Scene = "日常办公", bool Representative = false, string FutureNeeds = "", int RetentionDays = 90, bool CloseToTray = true, RequirementsPlan? Requirements = null, SemanticPlan? SemanticRequirements = null, string ModelEndpoint = "", string ModelName = "")
{
    public string RepresentativeHardwareId { get; init; } = "";
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
    public double? CpuCoverage { get; init; }
    public double? CoreCoverage { get; init; }
    public double? MemoryCoverage { get; init; }
    public double? CommitCoverage { get; init; }
    public double? GpuCoverage { get; init; }
    public bool Active { get; init; }
    public double? Cpu { get; init; }
    public double? CoreMax { get; init; }
    public double? MemoryUsedGb { get; init; }
    public double? MemoryPeakGb { get; init; }
    public double? MemoryTotalGb { get; init; }
    public double? CommitGb { get; init; }
    public double? Gpu { get; init; }
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
}
public record Advice(string Key, string Name, string Status, string Title, string Evidence, string Suggestion, string Limits);
public record HistoryPoint(DateTimeOffset Time, double? Cpu, double? Memory, double? Gpu);
public record SceneProfile(string Name, int SampleCount, double ActiveHours, double? CpuP95, double? CoreP95,
    double? MemoryPeakGb, double? GpuP95, double? GpuPeak, double CpuCoverage, double GpuCoverage)
{
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
    public string StatusLabel => Status switch { "increased" => "近期增加", "decreased" => "近期减少", "similar" => "相近", _ => "对照积累中" };
    public string ComparisonLabel => $"最近 {Days} 天 / 此前 {Days} 天";
    public string ReadingsLabel => $"P95：{Format(RecentP95)} / {Format(PreviousP95)} · 有效观测 {DurationText.FromHours(RecentHours)} / {DurationText.FromHours(PreviousHours)}";
    public string BusyLabel => Key == "memory" ? "" : $"负载 ≥80% 累计 {RecentBusyMinutes:0.0} / {PreviousBusyMinutes:0.0} 分钟（有效观测占比 {Share(RecentBusyMinutes, RecentHours)} / {Share(PreviousBusyMinutes, PreviousHours)}）";
    public string SceneLabel => RecentHeavyScenes.Length > 0 ? "近期重负载场景：" + string.Join("、", RecentHeavyScenes) : "";
    private string Format(double? value) => value.HasValue ? $"{value:0.0}{Unit}" : "缺失";
    private static string Share(double minutes, double hours) => hours > 0 ? $"{minutes / (hours * 60) * 100:0.0}%" : "缺失";
}
public record AnalysisReport(DateTimeOffset Generated, int WindowDays, int SampleCount, double ActiveHours, int ActiveDays,
    bool Ready, string[] Scenes, List<Advice> Cards, List<HistoryPoint> History, string FutureNeeds)
{
    public List<SceneProfile> SceneProfiles { get; init; } = [];
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
    public string MethodVersion { get; init; } = "1.5";
    [System.Text.Json.Serialization.JsonIgnore]
    public string DurationLabel => DurationText.FromHours(ActiveHours);
    public string Level => Ready ? "较充分" : ActiveHours >= .5 ? "初步观察" : "积累中";
    public string Summary => Cards.Any(c => c.Status == "experience") ? "近期有体验问题，先核对原因再评估余量" : Cards.Any(c => c.Status == "requirements") ? "已确认配置目标，先核对候选机器的表现" : DemandTrends.Any(t => t.Status == "increased") ? "近期任务负载增加，优先核对新的配置需求" : Ready ? $"{Cards.Count(c => c.Status == "surplus")} 项疑似过剩 · {Cards.Count(c => c.Status == "pressure")} 项压力线索" : "先记录真实使用，再决定配置预算";
}
