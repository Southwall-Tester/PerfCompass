namespace PerfCompass;

public record StorageCapacityEvidence(double OccupiedGiB, double ObservedTotalGiB, DateTimeOffset RecordedAt, string Scope);
public record StorageCapacityRow(string NominalLabel, long NominalBytes, double OccupiedGiB)
{
    public double NominalGiB => NominalBytes / 1073741824d;
    public double DifferenceGiB => NominalGiB - OccupiedGiB;
    public string ConvertedLabel => $"约 {NominalGiB:0.##} GiB";
    public string DifferenceLabel => DifferenceGiB == 0 ? "与已测占用相同" : $"{(DifferenceGiB < 0 ? "少" : "多")}约 {Math.Abs(DifferenceGiB).ToString(Math.Abs(DifferenceGiB)<.005?"G3":"0.##")} GiB";
    public string Meaning => DifferenceGiB < 0 ? "保持这部分占用需先释放或外迁，另留系统与增长空间" : "未扣格式化、系统预留和增长，不等于可用余量";
}

public record StorageCapacityPreview(StorageCapacityEvidence Evidence, StorageCapacityRow[] Rows)
{
    public const string Notice = "假设保留这些卷的合计占用。读数可能含系统和临时文件，未确认所有卷或重复挂载；先逐卷核对迁移清单。容量差额不判断 SSD 速度、寿命或候选是否达标。已确认的新数据需求优先。";
    public string Baseline => $"已取得固定卷合计占用 {Evidence.OccupiedGiB:0.0} / {Evidence.ObservedTotalGiB:0.0} GiB";
    public string Period => $"{Evidence.Scope} · 最近读数 {Evidence.RecordedAt.LocalDateTime:yyyy-MM-dd HH:mm}；不是增长量预测";
    internal static StorageCapacityEvidence? Capture(Sample? sample, string scope)
    {
        bool known=sample != null && (sample.DiskCapacityFieldsPresent == true
            || sample.DiskCapacityFieldsPresent == null && sample.DiskUsedGb > 0 && sample.DiskTotalGb > 0);
        if(sample == null || !known || SamplingClock.KnownIssue(sample.SamplingIssue)
            || !double.IsFinite(sample.DiskUsedGb) || !double.IsFinite(sample.DiskTotalGb) || sample.DiskTotalGb <= 0
            || sample.DiskUsedGb < 0 || sample.DiskUsedGb > sample.DiskTotalGb) return null;
        return new(sample.DiskUsedGb, sample.DiskTotalGb, sample.Time, scope);
    }
    public static StorageCapacityPreview? From(AnalysisReport report)
    {
        if(string.IsNullOrWhiteSpace(report.HardwareId) || report.HistoryQuality.HasIssues || report.HistoryQuality.HasIntervalConflicts
            || report.LatestStorageCapacity is not { } data || !double.IsFinite(data.OccupiedGiB) || !double.IsFinite(data.ObservedTotalGiB)
            || data.OccupiedGiB < 0 || data.ObservedTotalGiB <= 0 || data.OccupiedGiB > data.ObservedTotalGiB) return null;
        (string Label,long Bytes)[] common = [("256 GB",256_000_000_000L),("512 GB",512_000_000_000L),("1 TB",1_000_000_000_000L),
            ("2 TB",2_000_000_000_000L),("4 TB",4_000_000_000_000L),("8 TB",8_000_000_000_000L),("16 TB",16_000_000_000_000L),("32 TB",32_000_000_000_000L)];
        int center=Array.FindIndex(common,c => c.Bytes/1073741824d >= data.ObservedTotalGiB);
        int first=center < 0 ? common.Length-5 : Math.Clamp(center-2,0,common.Length-5);
        return new(data,common.Skip(first).Take(5).Select(c => Compare(c.Label,c.Bytes,data.OccupiedGiB)).ToArray());
    }
    public static StorageCapacityRow Compare(string label,long nominalBytes,double occupiedGiB)
    {
        if(string.IsNullOrWhiteSpace(label) || nominalBytes<=0 || !double.IsFinite(occupiedGiB) || occupiedGiB<0) throw new ArgumentException("需要有效标称字节数与 GiB 占用。");
        return new(label,nominalBytes,occupiedGiB);
    }
}
