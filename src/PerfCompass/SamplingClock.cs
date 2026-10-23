using System.Diagnostics;

namespace PerfCompass;

internal readonly record struct SamplingInstant(DateTimeOffset Time, long Timestamp)
{
    internal static SamplingInstant Now() => new(DateTimeOffset.Now, Stopwatch.GetTimestamp());
}
internal readonly record struct SamplingTick(DateTimeOffset Time, double Seconds, string Issue);

/// <summary>Calendar time locates observations; a separate elapsed counter checks their duration.</summary>
internal sealed class SamplingClock(SamplingInstant initial)
{
    internal const double ClockStepToleranceSeconds = .25;
    private SamplingInstant previous = initial;
    internal static bool KnownIssue(string? issue) => issue is "clock-change" or "poll-gap" or "invalid-elapsed";
    internal static string Describe(string? issue) => issue switch
    {
        "clock-change" => "日历时间与经过时间不一致，本段未计入有效使用，采样基线已重建。",
        "poll-gap" => "采样间隔超过 15 秒，本段未计入有效使用，采样基线已重建。",
        "invalid-elapsed" => "无法确认本段经过时间，本段未计入有效使用，采样基线已重建。",
        _ => ""
    };
    internal void Reset(SamplingInstant instant) => previous = instant;
    internal SamplingTick Next(SamplingInstant instant)
    {
        double wall = (instant.Time - previous.Time).TotalSeconds;
        double elapsed = Stopwatch.GetElapsedTime(previous.Timestamp, instant.Timestamp).TotalSeconds;
        previous = instant;
        string issue = !double.IsFinite(elapsed) || elapsed <= 0 ? "invalid-elapsed"
            : wall <= 0 || Math.Abs(wall - elapsed) > ClockStepToleranceSeconds ? "clock-change"
            : wall > 15 || elapsed > 15 ? "poll-gap" : "";
        // Never create more covered time than either elapsed time or the UTC range.
        // Keeping duration within the UTC span also prevents rounding into adjacent records.
        return new(instant.Time, issue.Length == 0 ? Math.Min(wall, elapsed) : 0, issue);
    }
}
internal sealed class ElapsedSchedule(TimeSpan interval)
{
    private long? last;
    internal bool Due(long timestamp) => last == null || timestamp < last.Value || Stopwatch.GetElapsedTime(last.Value, timestamp) >= interval;
    internal void Mark(long timestamp) => last = timestamp;
}
