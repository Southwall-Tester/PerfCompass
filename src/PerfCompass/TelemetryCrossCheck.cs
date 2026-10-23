using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace PerfCompass;

internal sealed class WindowsReferenceCounters : IDisposable
{
    internal static int MachineProcessors
    {
        get
        {
            uint count = GetActiveProcessorCount(0xffff);
            return count > 0 && count <= int.MaxValue ? (int)count : throw new InvalidOperationException("无法确认整机逻辑处理器数量。");
        }
    }
    private IntPtr query;
    private readonly Dictionary<string, IntPtr> counters = [];
    internal List<string> Issues { get; } = [];
    internal int ProcessorGroups { get; } = GetActiveProcessorGroupCount();
    internal int Processors { get; } = (int)GetActiveProcessorCount(0xffff);
    internal WindowsReferenceCounters()
    {
        uint status = PdhOpenQueryW(null, 0, out query);
        if (status != 0) { Issues.Add("PDH query: " + status); return; }
        if (ProcessorGroups == 1 && Processors is > 0 and <= 64)
        {
            Add("total", @"\Processor(_Total)\% Processor Time");
            for (int i = 0; i < Processors; i++) Add("core-" + i, $@"\Processor({i})\% Processor Time");
        }
        else Issues.Add("CPU comparison not supported across multiple processor groups.");
        Add("commit", @"\Memory\Committed Bytes");
        PdhCollectQueryData(query);
    }
    private void Add(string name, string path)
    {
        uint status = PdhAddEnglishCounterW(query, path, 0, out var counter);
        if (status == 0) counters[name] = counter; else Issues.Add(name + ": " + status);
    }
    internal ReferenceReading Read()
    {
        var at = DateTimeOffset.Now; var watch = Stopwatch.StartNew(); uint status = query == IntPtr.Zero ? 1 : PdhCollectQueryData(query);
        double? Get(string name)
        {
            if (status != 0 || !counters.TryGetValue(name, out var counter) || PdhGetFormattedCounterValue(counter, 0x200, out _, out var value) != 0
                || value.Status > 1 || !double.IsFinite(value.Value) || value.Value < 0) return null;
            return value.Value;
        }
        var cores = Enumerable.Range(0, ProcessorGroups == 1 && Processors <= 64 ? Processors : 0).Select(i => Get("core-" + i)).ToArray();
        var memory = new MemoryStatus { Size = (uint)Marshal.SizeOf<MemoryStatus>() }; bool available = GlobalMemoryStatusEx(ref memory);
        var total = Get("total"); var committed = Get("commit"); watch.Stop();
        return new(at, watch.Elapsed.TotalMilliseconds, total, cores.Length > 0 && cores.All(v => v.HasValue) ? cores.Max() : null,
            available ? memory.TotalPhysical / 1073741824d : null, available ? (memory.TotalPhysical - memory.AvailablePhysical) / 1073741824d : null,
            committed / 1073741824d, cores.Count(v => v.HasValue), Processors);
    }
    public void Dispose() { if (query != IntPtr.Zero) { PdhCloseQuery(query); query = IntPtr.Zero; } }
    [StructLayout(LayoutKind.Explicit, Size = 16)] private struct CounterValue { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Value; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Size, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint PdhOpenQueryW(string? source, nuint data, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, nuint data, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("kernel32.dll")] private static extern ushort GetActiveProcessorGroupCount();
    [DllImport("kernel32.dll")] private static extern uint GetActiveProcessorCount(ushort group);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
internal record ReferenceReading(DateTimeOffset Time, double ReadMilliseconds, double? Cpu, double? CoreMax, double? MemoryTotalGiB,
    double? MemoryUsedGiB, double? CommitGiB, int CoveredCores, int ExpectedCores);
internal record TelemetryPair(Sample Sample, ReferenceReading Reference, double CollectorMilliseconds);
internal static class TelemetryCrossCheck
{
    internal static async Task<int> RunAsync(LocalStore store, int seconds)
    {
        using var collector = new Collector(store); using var reference = new WindowsReferenceCounters();
        string output = Path.Combine(store.Root, "validation"); Directory.CreateDirectory(output);
        var rows = new List<TelemetryPair>(); var timer = Stopwatch.StartNew();
        // Prime both paths before comparing consecutive intervals. GPU update time can still skew the memory read.
        collector.ResetTiming(); reference.Read();
        while (timer.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(2000); var other = reference.Read(); var measure = Stopwatch.StartNew(); var sample = collector.Collect().Sample; measure.Stop();
            rows.Add(new(sample, other, measure.Elapsed.TotalMilliseconds));
        }
        object Differences(Func<TelemetryPair, double?> select)
        {
            var values = rows.Select(select).Where(v => v.HasValue && double.IsFinite(v.Value)).Select(v => Math.Abs(v!.Value)).ToList();
            return new { Pairs = values.Count, MeanAbsolute = values.Count > 0 ? (double?)values.Average() : null, MaximumAbsolute = values.Count > 0 ? (double?)values.Max() : null };
        }
        using var executable = File.OpenRead(Environment.ProcessPath!);
        SettingsFile.AtomicWrite(Path.Combine(output, "telemetry-cross-check.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            Source = "Real local Collector; PDH language-neutral Processor/Memory counters; GlobalMemoryStatusEx",
            Version = typeof(App).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion,
            ExecutableSha256 = Convert.ToHexString(SHA256.HashData(executable)), RequestedSeconds = seconds, ObservedSeconds = timer.Elapsed.TotalSeconds,
            ManagedAssemblySha256 = ValidationIdentity.ManagedAssemblyHash(),
            collector.CpuName, collector.GpuName, Os = Environment.OSVersion.VersionString,
            reference.ProcessorGroups, reference.Processors, reference.Issues,
            CpuPercentagePoints = Differences(p => p.Sample.Cpu - p.Reference.Cpu), CorePercentagePoints = Differences(p => p.Sample.CoreMax - p.Reference.CoreMax),
            PhysicalTotalGiB = Differences(p => p.Sample.MemoryTotalGb - p.Reference.MemoryTotalGiB), PhysicalUsedGiB = Differences(p => p.Sample.MemoryUsedGb - p.Reference.MemoryUsedGiB),
            CommitGiB = Differences(p => p.Sample.CommitGb - p.Reference.CommitGiB), Rows = rows,
            Scope = "Two Windows acquisition paths, not independent physical ground truth. No controlled load is generated. Each pair retains interval timestamps and read duration; sequential memory reads can differ. Missing references remain null. CPU means busy time, not frequency-normalized Processor Utility. This run does not calibrate purchase thresholds or prove other hardware and drivers."
        }));
        // Exit 0 denotes a complete recording with at least two comparable CPU/memory pairs, not an accuracy certificate.
        return rows.Count >= 2 && rows.Count(p => p.Sample.Cpu.HasValue && p.Reference.Cpu.HasValue && p.Reference.MemoryUsedGiB.HasValue) >= 2 ? 0 : 2;
    }
}
