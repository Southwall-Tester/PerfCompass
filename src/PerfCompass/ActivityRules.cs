namespace PerfCompass;

internal static class ActivityRules
{
    internal const string Version = "activity-1.3";
    internal static bool KnownVersion(string? value) => value is "activity-1.1" or "activity-1.2" or Version;
    internal static bool IsActive(double idleSeconds, double? cpu, double? core, double? gpu, double? diskBusy, double? readMiB, double? writeMiB, double? knownGpuEngineLoad = null)
    {
        bool PercentAtLeast(double? value, double threshold) => value is double v && double.IsFinite(v) && v >= threshold && v <= 100;
        double KnownRate(double? value) => value is double v && double.IsFinite(v) && v >= 0 ? v : 0;
        return double.IsFinite(idleSeconds) && idleSeconds is >= 0 and < 300
            || PercentAtLeast(cpu, 20) || PercentAtLeast(core, 80) || PercentAtLeast(gpu, 20) || PercentAtLeast(diskBusy, 80)
            || PercentAtLeast(knownGpuEngineLoad, 20)
            || KnownRate(readMiB) + KnownRate(writeMiB) >= 20;
    }
}
