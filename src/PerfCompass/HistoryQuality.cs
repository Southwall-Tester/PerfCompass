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
    public static HistoryQuality Unchecked { get; } = new(0, 0, 0, "", []);
    public bool HasIssues => IssueCount > 0;
    public string Message => HasIssues ? $"最近 {ReadDays} 天的历史读取发现 {IssueCount} 处异常，涉及 {AffectedFiles} 个文件。可用观测继续保留，暂不判断配置余量或时期变化。异常记录可能无法确认硬件与场景归属。" : "";
    public string Details => string.Join("\n", Issues.Select(i => i.Label)) + (IssueCount > Issues.Length ? $"\n仅列出前 {Issues.Length} 处，其余 {IssueCount - Issues.Length} 处未展开。" : "");
}
internal sealed class HistoryDiagnostics(int days) : IDisposable
{
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HistoryIssue> issues = [];
    private int count;
    internal void Add(string file, long line, string reason, byte[]? payload = null, string fingerprint = "")
    {
        count++; files.Add(file);
        var issue = new HistoryIssue(file, line, reason, payload != null ? Convert.ToHexString(SHA256.HashData(payload)) : fingerprint);
        if (issues.Count < 50) issues.Add(issue);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(issue));
    }
    internal HistoryQuality Build() => new(days, count, files.Count, count > 0 ? Convert.ToHexString(hash.GetCurrentHash()) : "", issues.ToArray());
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
            Cards = report.Cards.Select(c => c.Status is "surplus" or "review" or "changed" ? c with
                { Status = "unknown", Title = "历史异常，先核对记录", Suggestion = quality.Message } : c).ToList(),
            DemandTrends = report.DemandTrends.Select(t => t with
                { Status = "insufficient", Summary = "历史读取存在异常，时期对照暂不成立。", Suggestion = quality.Message }).ToList()
        };
    }
}
