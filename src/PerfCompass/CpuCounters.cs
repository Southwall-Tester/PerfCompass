namespace PerfCompass;

internal readonly record struct ProcessorTimes(long Idle, long Kernel, long User);
internal sealed record CpuCounterFrame(int ExpectedProcessors, ushort ProcessorGroups, ProcessorTimes[] Values);
internal readonly record struct CpuLoads(double? Total, double? Maximum)
{
    internal static bool Complete(CpuCounterFrame? frame) => frame is { ExpectedProcessors: > 0, ProcessorGroups: > 0 }
        && frame.Values.Length == frame.ExpectedProcessors
        && frame.Values.All(v => v.Idle >= 0 && v.Kernel >= 0 && v.User >= 0);

    internal static CpuLoads Calculate(CpuCounterFrame? previous, CpuCounterFrame? current)
    {
        if (!Complete(previous) || !Complete(current) || previous!.ExpectedProcessors != current!.ExpectedProcessors
            || previous.ProcessorGroups != current.ProcessorGroups) return new(null, null);
        double total = 0, busy = 0, maximum = 0;
        for (int i = 0; i < current.Values.Length; i++)
        {
            var before = previous.Values[i]; var after = current.Values[i];
            if (after.Idle < before.Idle || after.Kernel < before.Kernel || after.User < before.User) return new(null, null);
            // Subtract nonnegative monotonic counters before adding as double: adding
            // cumulative Int64 kernel/user counters first can overflow.
            double elapsed = (double)(after.Kernel - before.Kernel) + (after.User - before.User);
            double idle = after.Idle - before.Idle;
            if (elapsed <= 0 || idle > elapsed) return new(null, null);
            double used = elapsed - idle;
            total += elapsed; busy += used; maximum = Math.Max(maximum, used / elapsed * 100);
        }
        return new(busy / total * 100, maximum);
    }
}
