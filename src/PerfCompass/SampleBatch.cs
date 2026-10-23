namespace PerfCompass;

/// <summary>Compact two-second readings into ten-second history while preserving memory peaks.</summary>
public static class SampleBatch
{
    public static bool MustFlushBefore(IReadOnlyList<Sample> source, Sample next) => source.Count > 0 &&
        (source[^1].Scene != next.Scene || next.Seconds == 0 || source.Sum(s => s.Seconds) + next.Seconds > 15);
    public static Sample Merge(IReadOnlyList<Sample> source)
    {
        if (source.Count == 0) throw new ArgumentException("Empty sample batch");
        if (source.Where(s => s.Seconds is > 0 and <= 15).Sum(s => s.Seconds) > 15) throw new ArgumentException("Sample batch exceeds 15 seconds; flush before adding the next sample.");
        var active = source.Where(s => s.Active && s.Seconds is > 0 and <= 15).ToList();
        var selected = active.Count > 0 ? active : source.ToList();
        double? Average(Func<Sample, double?> selector)
        {
            var valid = selected.Where(s => selector(s).HasValue && s.Seconds is > 0 and <= 15).ToList();
            double time = valid.Sum(s => s.Seconds);
            return time > 0 ? valid.Sum(s => selector(s)!.Value * s.Seconds) / time : null;
        }
        double? Peak(Func<Sample, double?> selector)
        {
            var values = selected.Select(selector).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            return values.Count > 0 ? values.Max() : null;
        }
        var last = source[^1];
        double Covered(Func<Sample, double?> selector) => active.Where(s => selector(s).HasValue).Sum(s => s.Seconds);
        double Exposure(Func<Sample, bool> condition) => active.Where(condition).Sum(s => s.Seconds);
        return last with
        {
            Active = active.Count > 0, Seconds = active.Sum(s => s.Seconds),
            CpuCoverage = Covered(s => s.Cpu), CoreCoverage = Covered(s => s.CoreMax), MemoryCoverage = Covered(s => s.MemoryUsedGb),
            CommitCoverage = Covered(s => s.CommitGb), GpuCoverage = Covered(s => s.Gpu),
            Cpu = Average(s => s.Cpu), CoreMax = Peak(s => s.CoreMax),
            MemoryUsedGb = Average(s => s.MemoryUsedGb), MemoryPeakGb = Peak(s => s.MemoryUsedGb), CommitGb = Peak(s => s.CommitGb),
            Gpu = Average(s => s.Gpu), GpuPeak = Peak(s => s.GpuPeak ?? s.Gpu), VramUsedGb = Peak(s => s.VramUsedGb),
            CpuHighSeconds = Exposure(s => s.Cpu >= 90), GpuHeavySeconds = Exposure(s => s.Gpu >= 80), GpuHighSeconds = Exposure(s => s.Gpu >= 95),
            MemoryHighSeconds = Exposure(s => s.MemoryTotalGb is > 0 && s.MemoryUsedGb / s.MemoryTotalGb >= .9),
            DiskReadMbps = Average(s => s.DiskReadMbps), DiskWriteMbps = Average(s => s.DiskWriteMbps),
            DiskBusy = Average(s => s.DiskBusy), MaxDiskPercent = source.Max(s => s.MaxDiskPercent)
        };
    }
}
