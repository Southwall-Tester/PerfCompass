using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace PerfCompass;

public sealed class Collector : IDisposable
{
    private readonly Computer computer = new() { IsCpuEnabled = true, IsGpuEnabled = true };
    private readonly LocalStore store;
    private ProcPerf[]? previous;
    private readonly Dictionary<int, (TimeSpan Cpu, long Start)> processes = new();
    private readonly SamplingClock clock;
    private long? previousAppsTimestamp;
    private List<AppUsage> lastApps = [];
    private PerformanceCounter? read, write, idle;
    private string sensorStatus = "硬件传感器正在初始化";
    public string CpuName { get; private set; } = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "CPU";
    public string GpuName { get; private set; } = "等待显卡识别";
    public string HardwareId { get; private set; } = "";
    internal Action<IHardware>? HardwareUpdateOverride;
    internal volatile Func<SamplingInstant>? TimingReadOverride;
    private SamplingInstant ReadTime() => TimingReadOverride?.Invoke() ?? SamplingInstant.Now();
    public Collector(LocalStore store)
    {
        this.store = store;
        try
        {
            computer.Open();
            CpuName = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu)?.Name ?? CpuName;
            GpuName = Gpus().FirstOrDefault()?.Name ?? "未取得 GPU 传感器";
            sensorStatus = "LibreHardwareMonitor · Windows 性能计数器";
        }
        catch (Exception e) { sensorStatus = "部分传感器不可用，基础采集继续"; store.Log(e); }
        try
        {
            read = new("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
            write = new("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
            idle = new("PhysicalDisk", "% Idle Time", "_Total");
            read.NextValue(); write.NextValue(); idle.NextValue();
        }
        catch (Exception e) { store.Log(e); read?.Dispose(); write?.Dispose(); idle?.Dispose(); read = write = idle = null; }
        var mem = Memory();
        HardwareId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{CpuName}|{GpuName}|{mem.Total:0}|{Environment.ProcessorCount}")))[..16];
        previous = CpuTimes();
        clock = new(ReadTime());
    }
    private IEnumerable<IHardware> Gpus() => computer.Hardware.Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
        .OrderBy(h => h.HardwareType == HardwareType.GpuIntel ? 1 : 0);
    public LiveSnapshot Collect()
    {
        var instant = ReadTime(); var tick = clock.Next(instant); var now = tick.Time;
        double dt = tick.Seconds; bool discontinuity = tick.Issue.Length > 0;
        if (discontinuity) { processes.Clear(); lastApps = []; previousAppsTimestamp = null; }
        double? cpu = null, core = null;
        var current = CpuTimes();
        if (!discontinuity && current != null && previous != null && current.Length == previous.Length)
        {
            var loads = new List<double>();
            for (int i = 0; i < current.Length; i++)
            {
                long total = current[i].Kernel + current[i].User - previous[i].Kernel - previous[i].User;
                long idleDelta = current[i].Idle - previous[i].Idle;
                if (total > 0) loads.Add(Math.Clamp((1 - (double)idleDelta / total) * 100, 0, 100));
            }
            if (loads.Count > 0) { cpu = loads.Average(); core = loads.Max(); }
        }
        previous = current;
        var sensors = HardwareSnapshot.Capture(computer.Hardware, store.Log, HardwareUpdateOverride);
        var cpuHw = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        var gpuHw = Gpus().FirstOrDefault();
        var gpu = sensors.Value(gpuHw, SensorType.Load, "GPU Core", "GPU D3D 3D", "D3D 3D");
        var vramUsed = sensors.Value(gpuHw, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
        var vramTotal = sensors.Value(gpuHw, SensorType.SmallData, "GPU Memory Total");
        double? cpuTemp = sensors.Value(cpuHw, SensorType.Temperature, "CPU Package", "Core (Tctl/Tdie)", "Core (Tdie)", "Core Average");
        double? gpuTemp = sensors.Value(gpuHw, SensorType.Temperature, "GPU Core");
        var mem = Memory();
        var disks = new List<DiskInfo>();
        foreach (var drive in DriveInfo.GetDrives())
            try { if (drive.IsReady && drive.DriveType == DriveType.Fixed) disks.Add(new(drive.Name, drive.TotalSize / 1073741824d, (drive.TotalSize - drive.TotalFreeSpace) / 1073741824d)); } catch { }
        double? diskRead = null, diskWrite = null, diskBusy = null;
        try { diskRead = read?.NextValue() / 1048576d; diskWrite = write?.NextValue() / 1048576d; diskBusy = idle is null ? null : Math.Clamp(100 - idle.NextValue(), 0, 100); }
        catch { }
        if (previousAppsTimestamp == null || Stopwatch.GetElapsedTime(previousAppsTimestamp.Value, instant.Timestamp).TotalSeconds >= 6)
        {
        double appInterval = previousAppsTimestamp is long stamp ? Stopwatch.GetElapsedTime(stamp, instant.Timestamp).TotalSeconds : 0;
        previousAppsTimestamp = instant.Timestamp;
        var apps = new List<AppUsage>();
        var seen = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            try
            {
                int pid = process.Id; seen.Add(pid);
                var pcpu = process.TotalProcessorTime;
                long started = process.StartTime.Ticks;
                double usage = 0;
                if (dt > 0 && appInterval is > 0 and < 30 && processes.TryGetValue(pid, out var last) && last.Start == started)
                    usage = Math.Clamp((pcpu - last.Cpu).TotalSeconds / appInterval / Environment.ProcessorCount * 100, 0, 100);
                processes[pid] = (pcpu, started);
                apps.Add(new(process.ProcessName, usage, process.WorkingSet64 / 1048576d));
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
        }
        foreach (int old in processes.Keys.Where(k => !seen.Contains(k)).ToArray()) processes.Remove(old);
        lastApps = apps.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AppUsage(g.Count() > 1 ? $"{g.Key} · {g.Count()} 进程" : g.Key, g.Sum(a => a.Cpu), g.Sum(a => a.MemoryMb)))
            .OrderByDescending(a => a.Cpu).ThenByDescending(a => a.MemoryMb).Take(8).ToList();
        }
        // Count attended usage or substantial background work, not an idle desktop overnight.
        bool active = IdleSeconds() < 300 || cpu >= 20 || gpu >= 20 || diskRead + diskWrite >= 20;
        var sample = new Sample
        {
            Time = now, HardwareId = HardwareId, Seconds = dt, SamplingIssue = tick.Issue, Active = active, Scene = store.Settings.Scene,
            Cpu = cpu, CoreMax = core, MemoryTotalGb = mem.Total, MemoryUsedGb = mem.Used, CommitGb = mem.Commit,
            Gpu = gpu, VramUsedGb = vramUsed / 1024, VramTotalGb = vramTotal / 1024, CpuTemp = cpuTemp, GpuTemp = gpuTemp,
            DiskReadMbps = diskRead, DiskWriteMbps = diskWrite, DiskBusy = diskBusy,
            MaxDiskPercent = disks.Count > 0 ? disks.Max(d => d.Percent) : 0,
            DiskUsedGb = disks.Sum(d => d.UsedGb), DiskTotalGb = disks.Sum(d => d.TotalGb)
        };
        return new(sample, CpuName, GpuName, sensorStatus, disks, lastApps) { SensorFailures = sensors.Failures };
    }
    public void ResetTiming() { clock.Reset(ReadTime()); previous = CpuTimes(); processes.Clear(); previousAppsTimestamp = null; lastApps = []; }
    public void Dispose() { computer.Close(); read?.Dispose(); write?.Dispose(); idle?.Dispose(); }

    [StructLayout(LayoutKind.Sequential)] private struct ProcPerf { public long Idle, Kernel, User, Dpc, Interrupt; public uint InterruptCount; }
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returned);
    private static ProcPerf[]? CpuTimes()
    {
        int size = Marshal.SizeOf<ProcPerf>(); int length = size * Math.Max(256, Environment.ProcessorCount);
        var ptr = Marshal.AllocHGlobal(length);
        try
        {
            if (NtQuerySystemInformation(8, ptr, length, out int actual) != 0) return null;
            var result = new ProcPerf[actual / size];
            for (int i = 0; i < result.Length; i++) result[i] = Marshal.PtrToStructure<ProcPerf>(ptr + i * size);
            return result;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct PerformanceInfo
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint Handles, Processes, Threads;
    }
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool GetPerformanceInfo(ref PerformanceInfo info, uint size);
    private static (double? Total, double? Used, double? Commit) Memory()
    {
        var p = new PerformanceInfo { Size = (uint)Marshal.SizeOf<PerformanceInfo>() };
        if (!GetPerformanceInfo(ref p, p.Size)) return (null, null, null);
        double scale = (double)p.PageSize / 1073741824d;
        return ((double)p.PhysicalTotal * scale, (double)(p.PhysicalTotal - p.PhysicalAvailable) * scale, (double)p.CommitTotal * scale);
    }
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Time; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput info);
    private static double IdleSeconds()
    {
        var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        return GetLastInputInfo(ref input) ? unchecked((uint)Environment.TickCount - input.Time) / 1000d : double.PositiveInfinity;
    }
}
