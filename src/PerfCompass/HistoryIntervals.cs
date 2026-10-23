using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PerfCompass;

internal readonly record struct HistoryFingerprint(ulong A, ulong B, ulong C, ulong D)
{
    internal static HistoryFingerprint From(ReadOnlySpan<byte> bytes)
    {
        Span<byte> digest = stackalloc byte[32]; SHA256.HashData(bytes, digest);
        return new(BinaryPrimitives.ReadUInt64LittleEndian(digest), BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[16..]), BinaryPrimitives.ReadUInt64LittleEndian(digest[24..]));
    }
}
/// <summary>Disjoint UTC ranges compress continuous sessions; insertion order cannot inflate covered time.</summary>
internal sealed class HistoryIntervals
{
    private sealed record Interval(long Start, long End);
    private readonly SortedSet<Interval> ranges = new(Comparer<Interval>.Create((a, b) => a.Start.CompareTo(b.Start)));
    internal long Add(long start, long end)
    {
        if (end <= start) return 0;
        long unique = end - start, mergedStart = start, mergedEnd = end;
        while (true)
        {
            var previous = ranges.GetViewBetween(new(long.MinValue, long.MinValue), new(mergedEnd, mergedEnd)).Max;
            if (previous == null || previous.End < mergedStart) break;
            unique -= Math.Max(0, Math.Min(end, previous.End) - Math.Max(start, previous.Start));
            mergedStart = Math.Min(mergedStart, previous.Start); mergedEnd = Math.Max(mergedEnd, previous.End); ranges.Remove(previous);
        }
        ranges.Add(new(mergedStart, mergedEnd)); return unique;
    }
}
internal sealed class HistoryAudit
{
    private readonly HashSet<HistoryFingerprint> seen = [];
    private readonly Dictionary<string, HistoryIntervals> totals = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Hardware, string Scene), HistoryIntervals> scenes = [];
    internal bool IsDuplicate(HistoryFingerprint fingerprint) => !seen.Add(fingerprint);
    internal (double Total, double Scene, bool Overlap) Cover(Sample sample)
    {
        if (!sample.Active || sample.Seconds <= 0) return (0, 0, false);
        long duration = (long)Math.Round(sample.Seconds * TimeSpan.TicksPerSecond), end = sample.Time.UtcTicks, start = end - duration;
        if (!totals.TryGetValue(sample.HardwareId, out var total)) totals[sample.HardwareId] = total = new();
        var key = (sample.HardwareId, AnalysisEngine.NormalizeScene(sample.Scene));
        if (!scenes.TryGetValue(key, out var scene)) scenes[key] = scene = new();
        long unique = total.Add(start, end), uniqueScene = scene.Add(start, end);
        return (unique / (double)TimeSpan.TicksPerSecond, uniqueScene / (double)TimeSpan.TicksPerSecond, duration - unique > 1);
    }
}
