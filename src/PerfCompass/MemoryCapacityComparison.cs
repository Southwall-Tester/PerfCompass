namespace PerfCompass;

public record MemoryCapacityEvidence(double PeakGiB, double? CommitPeakGiB, double MinimumObservedCapacityGiB,
    double MaximumObservedCapacityGiB, DateTimeOffset FirstRecord, DateTimeOffset LastRecord)
{
    public string Scope { get; init; } = "";
    public double ObservedHours { get; init; }
    public double Coverage { get; init; }
}

public record MemoryCapacityRow(double CapacityGiB, double DifferenceGiB)
{
    public string CapacityLabel => $"{CapacityGiB:0.##} GiB";
    public string DifferenceLabel => DifferenceGiB == 0 ? "与已测峰值相同" : $"{(DifferenceGiB < 0 ? "少" : "多")}约 {Math.Abs(DifferenceGiB).ToString(Math.Abs(DifferenceGiB) < .005 ? "G3" : "0.##")} GiB";
    public string Meaning => DifferenceGiB < 0 ? "需缩减当时占用，或验证换页影响" : "高于已测物理峰值，仍需验证任务";
}

public record MemoryCapacityPreview(MemoryCapacityEvidence Evidence, MemoryCapacityRow[] Rows)
{
    public const string Notice = "按候选可用物理容量比较，商品规格请先统一单位。差额不等于实际余量或任务达标；工作集可能随容量变化，提交量也不能直接当作必装内存。已确认的新需求优先。";
    public string Baseline => $"已测可用容量 {(Evidence.MinimumObservedCapacityGiB == Evidence.MaximumObservedCapacityGiB ? $"{Evidence.MaximumObservedCapacityGiB:0.0}" : $"{Evidence.MinimumObservedCapacityGiB:0.0}–{Evidence.MaximumObservedCapacityGiB:0.0}")} GiB · 物理峰值 {Evidence.PeakGiB:0.0} GiB · 提交量峰值 {(Evidence.CommitPeakGiB is double c && double.IsFinite(c) && c >= 0 ? $"{c:0.0} GiB" : "未取得")}";
    public string Period => $"{Evidence.Scope} · 首末记录 {Evidence.FirstRecord.LocalDateTime:MM/dd HH:mm}–{Evidence.LastRecord.LocalDateTime:MM/dd HH:mm}；只描述这些已测任务";

    public static MemoryCapacityPreview? From(AnalysisReport report)
    {
        var observation = report.Cards.FirstOrDefault(c => c.Key == "memory")?.Observation;
        var measured = report.SelectedMemoryCapacity ?? observation?.MemoryCapacity;
        if (string.IsNullOrWhiteSpace(report.HardwareId) || report.HistoryQuality.HasIssues || report.HistoryQuality.HasIntervalConflicts
            || measured is not { } data || observation == null || observation.ObservedHours < 300d / 3600 || observation.Coverage < .95
            || data.ObservedHours < 300d / 3600 || data.Coverage < .95
            || !double.IsFinite(data.PeakGiB) || data.PeakGiB < 0 || !double.IsFinite(data.MinimumObservedCapacityGiB)
            || !double.IsFinite(data.MaximumObservedCapacityGiB) || data.MinimumObservedCapacityGiB <= 0
            || data.MaximumObservedCapacityGiB < data.MinimumObservedCapacityGiB || data.PeakGiB > data.MaximumObservedCapacityGiB || data.LastRecord < data.FirstRecord) return null;
        double[] common = [1, 2, 4, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 2048, 4096, 8192, 16384];
        int center = Array.FindIndex(common, n => n >= data.MaximumObservedCapacityGiB);
        double[] capacities = center < 0 ? [data.MaximumObservedCapacityGiB / 2, data.MaximumObservedCapacityGiB]
            : common.Skip(Math.Clamp(center - 2, 0, common.Length - 5)).Take(5).ToArray();
        return new(data, capacities.Select(c => Compare(c, data.PeakGiB)).ToArray());
    }

    public static MemoryCapacityRow Compare(double availableGiB, double observedPeakGiB)
    {
        if (!double.IsFinite(availableGiB) || availableGiB <= 0 || !double.IsFinite(observedPeakGiB) || observedPeakGiB < 0)
            throw new ArgumentException("比较需要有效的 GiB 可用容量与已测物理峰值。");
        return new(availableGiB, availableGiB - observedPeakGiB);
    }
}
