using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PerfCompass;

public record HistoryIssue(string File, long Line, string Reason, string Fingerprint = "")
{
    public string Label => Line > 0 ? $"{File} · 第 {Line} 行：{Reason}" : $"{File}：{Reason}";
}
public record HistoryQuality(int ReadDays, int IssueCount, int AffectedFiles, string Signature, HistoryIssue[] Issues)
{
    public int DuplicateCount { get; init; }
    public HistoryIssue[] Duplicates { get; init; } = [];
    public bool HasIntervalConflicts { get; init; }
    public static HistoryQuality Unchecked { get; } = new(0, 0, 0, "", []);
    public bool HasIssues => IssueCount > 0;
    public bool HasNotices => HasIssues || DuplicateCount > 0;
    public string Message => HasIssues ? $"最近 {ReadDays} 天的历史读取发现 {IssueCount} 处异常，涉及 {AffectedFiles} 个文件。可用观测继续保留，暂不判断配置余量或时期变化。异常记录可能无法确认硬件与场景归属。"
        + (HasIntervalConflicts ? " 重叠时段的负载份额无法确认，历史压力和负载统计也暂不解释。" : "") + (DuplicateCount > 0 ? $" 另排除 {DuplicateCount} 条相同记录。" : "")
        : DuplicateCount > 0 ? $"最近 {ReadDays} 天读取中排除了 {DuplicateCount} 条相同记录，观测时长按一次计算，原文件保留。" : "";
    public string Details => string.Join("\n", Issues.Concat(Duplicates.Take(Math.Max(0, 50 - Issues.Length))).Select(i => i.Label))
        + (IssueCount + DuplicateCount > 50 ? "\n位置明细最多展开 50 处；总数见提示。" : "");
}
internal sealed class HistoryDiagnostics(int days) : IDisposable
{
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HistoryIssue> issues = [];
    private int count;
    private int duplicateCount;
    private readonly List<HistoryIssue> duplicates = [];
    internal bool HasIntervalConflicts { get; set; }
    internal void Duplicate(string file, long line)
    { duplicateCount++; if (duplicates.Count < 50) duplicates.Add(new(file, line, "相同记录已排除，不重复累计时长")); }
    internal void Add(string file, long line, string reason, byte[]? payload = null, string fingerprint = "")
    {
        count++; files.Add(file);
        var issue = new HistoryIssue(file, line, reason, payload != null ? Convert.ToHexString(SHA256.HashData(payload)) : fingerprint);
        if (issues.Count < 50) issues.Add(issue);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(issue));
    }
    internal HistoryQuality Build() => new(days, count, files.Count, count > 0 ? Convert.ToHexString(hash.GetCurrentHash()) : "", issues.ToArray())
        { DuplicateCount = duplicateCount, Duplicates = duplicates.ToArray(), HasIntervalConflicts = HasIntervalConflicts };
    public void Dispose() => hash.Dispose();
}
public static class HistoryAnalysis
{
    public static AnalysisReport Analyze(LocalStore store, Settings settings, int days, string? scene = null, DateTimeOffset? asOf = null, string? hardwareId = null)
    {
        var now = asOf ?? DateTimeOffset.Now;
        using var diagnostics = new HistoryDiagnostics(Math.Max(days, 60));
        return AnalysisEngine.Analyze(store.ReadChecked(Math.Max(days, 60), hardwareId, now, diagnostics), settings, days, scene, now, hardwareId, diagnostics.Build);
    }
    public static AnalysisReport Apply(AnalysisReport report, HistoryQuality quality)
    {
        if (!quality.HasIssues) return report with { HistoryQuality = quality };
        return report with
        {
            Ready = false, HistoryQuality = quality,
            Cards = report.Cards.Select(c => quality.HasIntervalConflicts ? c with
                { Status = "unknown", Title = "时段重叠，先核对记录来源", Evidence = "同一配置的记录时间片重叠，无法确定负载份额。", Suggestion = quality.Message }
                : c.Status is "surplus" or "review" or "changed" ? c with
                { Status = "unknown", Title = "历史异常，先核对记录", Suggestion = quality.Message } : c).ToList(),
            DemandTrends = report.DemandTrends.Select(t => t with
                { Status = "insufficient", Summary = "历史读取存在异常，时期对照暂不成立。", Suggestion = quality.Message,
                    LoadStatisticsAvailable = !quality.HasIntervalConflicts,
                    RecentBusyMinutes = quality.HasIntervalConflicts ? 0 : t.RecentBusyMinutes,
                    PreviousBusyMinutes = quality.HasIntervalConflicts ? 0 : t.PreviousBusyMinutes,
                    RecentHeavyScenes = quality.HasIntervalConflicts ? [] : t.RecentHeavyScenes,
                    RecentP95 = quality.HasIntervalConflicts ? null : t.RecentP95, PreviousP95 = quality.HasIntervalConflicts ? null : t.PreviousP95 }).ToList(),
            History = quality.HasIntervalConflicts ? [] : report.History,
            SceneProfiles = quality.HasIntervalConflicts ? report.SceneProfiles.Select(p => p with { CpuP95 = null, CoreP95 = null, GpuP95 = null, MemoryPeakGb = null, GpuPeak = null }).ToList() : report.SceneProfiles
        };
    }
}
