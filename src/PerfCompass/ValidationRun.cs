using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PerfCompass;

internal record ValidationRunOptions(string Mode, int Seconds)
{
    internal static ValidationRunOptions? Parse(string[] args)
    {
        int modes = args.Count(a => a is "--soak-test" or "--telemetry-check");
        if (modes == 0) return null;
        if (modes != 1 || args.Count(a => a == "--validation-seconds") > 1 || args.Any(a => a is "--ui-test" or "--smoke-test" or "--diagnose" or "--check-instance" or "--recovery-ui-test" or "--self-test" or "--nlp-evaluate"))
            throw new ArgumentException("验证模式不能组合使用。");
        string mode = args.Contains("--soak-test") ? "soak" : "telemetry";
        int index = Array.IndexOf(args, "--validation-seconds"), seconds = mode == "soak" ? 28800 : 40;
        if (index >= 0 && (index + 1 >= args.Length || !int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out seconds))) throw new ArgumentException("验证时长必须为整数秒。");
        if (seconds < 10 || seconds > (mode == "soak" ? 604800 : 300)) throw new ArgumentException("验证时长超出范围。");
        return new(mode, seconds);
    }
    internal static void RequireEmptyRoot(string root, bool explicitRoot)
    {
        if (!explicitRoot || File.Exists(root) || Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new ArgumentException("验证必须显式指定空的独立数据目录。");
        DataBackup.NoLinks(root);
    }
}

internal sealed class RunningMetric
{
    private long count; private double mean, m2, min = double.PositiveInfinity, max = double.NegativeInfinity;
    internal void Add(double value)
    {
        if (!double.IsFinite(value)) return;
        count++; double delta = value - mean; mean += delta / count; m2 += delta * (value - mean);
        min = Math.Min(min, value); max = Math.Max(max, value);
    }
    internal object Snapshot() => new { Count = count, Mean = count > 0 ? (double?)mean : null, Minimum = count > 0 ? (double?)min : null,
        Maximum = count > 0 ? (double?)max : null, StandardDeviation = count > 1 ? (double?)Math.Sqrt(Math.Max(0, m2 / (count - 1))) : null };
}
internal record SoakHeartbeat(double ElapsedSeconds, double GapSeconds, double DispatcherMilliseconds, double? SampleAgeSeconds,
    bool Paused, bool HistoryWriteFailed, double CpuMachinePercent, double WorkingSetMiB, long ManagedBytes, int Handles,
    bool CpuAvailable, bool MemoryAvailable, bool GpuAvailable, bool RetentionProtected);

internal sealed class SoakEvidence
{
    private readonly RunningMetric cpu = new(), working = new(), managed = new(), handles = new(), dispatcher = new();
    internal int Heartbeats { get; private set; }
    internal int ObservationGaps { get; private set; }
    internal int StaleSamples { get; private set; }
    internal int PausedChecks { get; private set; }
    internal int WriteFailures { get; private set; }
    internal int MissingCpu { get; private set; }
    internal int MissingMemory { get; private set; }
    internal int MissingGpu { get; private set; }
    internal int ProtectionChecks { get; private set; }
    internal void Add(SoakHeartbeat h, double expectedGap)
    {
        Heartbeats++; if (h.GapSeconds > Math.Max(10, expectedGap * 2)) ObservationGaps++;
        if (h.SampleAgeSeconds is not double age || age < 0 || age > 15 || !double.IsFinite(age)) StaleSamples++;
        if (h.Paused) PausedChecks++; if (h.HistoryWriteFailed) WriteFailures++;
        if (!h.CpuAvailable) MissingCpu++; if (!h.MemoryAvailable) MissingMemory++; if (!h.GpuAvailable) MissingGpu++;
        if (h.RetentionProtected) ProtectionChecks++;
        cpu.Add(h.CpuMachinePercent); working.Add(h.WorkingSetMiB); managed.Add(h.ManagedBytes); handles.Add(h.Handles); dispatcher.Add(h.DispatcherMilliseconds);
    }
    internal bool Healthy => Heartbeats > 0 && ObservationGaps + StaleSamples + PausedChecks + WriteFailures + MissingCpu + MissingMemory + ProtectionChecks == 0;
    internal object Snapshot() => new { Heartbeats, ObservationGaps, StaleSamples, PausedChecks, WriteFailures, MissingCpu, MissingMemory, MissingGpu, ProtectionChecks,
        CpuMachinePercent = cpu.Snapshot(), WorkingSetMiB = working.Snapshot(), ManagedBytes = managed.Snapshot(), Handles = handles.Snapshot(), DispatcherMilliseconds = dispatcher.Snapshot() };
}

public partial class MainWindow
{
    private Task? soakTask;
    private bool soakFinished;
    private async Task RunSoakTest(ValidationRunOptions options)
    {
        string folder = Path.Combine(store.Root, "validation"); Directory.CreateDirectory(folder);
        string journal = Path.Combine(folder, "heartbeats.jsonl"), resultPath = Path.Combine(folder, "result.json"), stopPath = Path.Combine(folder, "stop.request");
        var timer = Stopwatch.StartNew(); var evidence = new SoakEvidence();
        var version = typeof(App).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
        string executableHash;
        using (var source = File.OpenRead(Environment.ProcessPath!)) executableHash = Convert.ToHexString(SHA256.HashData(source));
        var started = DateTimeOffset.UtcNow;
        string? managedHash = ValidationIdentity.ManagedAssemblyHash();
        SettingsFile.AtomicWrite(Path.Combine(folder, "run.json"), JsonSerializer.SerializeToUtf8Bytes(new
        { Mode = "soak", options.Seconds, Started = started, Version = version, ExecutableSha256 = executableHash, ManagedAssemblySha256 = managedHash,
            ProcessId = Environment.ProcessId, ProcessorsAvailableToProcess = Environment.ProcessorCount, MachineProcessors = WindowsReferenceCounters.MachineProcessors, Cpu = collector?.CpuName, Gpu = collector?.GpuName,
            Os = Environment.OSVersion.VersionString, DataRoot = store.Root, StopFile = stopPath,
            WarmupSeconds = Math.Min(15, Math.Max(3, options.Seconds / 10d)),
            Scope = "Real app plus validation instrumentation, real local telemetry. Independent validation directory; no production history. No controlled workload or sleep/resume is implied." }));
        double interval = Math.Min(30, Math.Max(2, options.Seconds / 12d)); string status = "interrupted", error = "";
        int timeouts = 0; double previousElapsed = 0;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(15, Math.Max(3, options.Seconds / 10d))), stop.Token);
            timer.Restart();
            using var process = Process.GetCurrentProcess(); process.Refresh(); var previousCpu = process.TotalProcessorTime;
            while (timer.Elapsed.TotalSeconds < options.Seconds)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(.001, Math.Min(interval, options.Seconds - timer.Elapsed.TotalSeconds))), stop.Token);
                if (File.Exists(stopPath)) { status = "stopped"; break; }
                var response = Stopwatch.StartNew();
                try
                {
                    var observed = await Dispatcher.InvokeAsync(() => new
                    { Sample = live?.Sample, Paused = paused, HistoryWriteFailed = historyWriteFailed, Protected = store.RetentionProtected }).Task.WaitAsync(TimeSpan.FromSeconds(5), stop.Token);
                    response.Stop(); process.Refresh(); double elapsed = timer.Elapsed.TotalSeconds, gap = elapsed - previousElapsed;
                    var currentCpu = process.TotalProcessorTime; var sample = observed.Sample;
                    var heartbeat = new SoakHeartbeat(elapsed, gap, response.Elapsed.TotalMilliseconds,
                        sample == null ? null : (DateTimeOffset.Now - sample.Time).TotalSeconds,
                        observed.Paused, observed.HistoryWriteFailed, (currentCpu - previousCpu).TotalSeconds / Math.Max(.001, gap) / WindowsReferenceCounters.MachineProcessors * 100,
                        process.WorkingSet64 / 1048576d, GC.GetTotalMemory(false), process.HandleCount,
                        sample?.Cpu.HasValue == true, sample?.MemoryTotalGb > 0, sample?.Gpu.HasValue == true, observed.Protected);
                    evidence.Add(heartbeat, interval); previousElapsed = elapsed; previousCpu = currentCpu;
                    File.AppendAllText(journal, JsonSerializer.Serialize(heartbeat) + "\n");
                    SettingsFile.AtomicWrite(Path.Combine(folder, "progress.json"), JsonSerializer.SerializeToUtf8Bytes(new
                    { Status = "running", Updated = DateTimeOffset.UtcNow, ElapsedSeconds = elapsed, RequestedSeconds = options.Seconds, Evidence = evidence.Snapshot() }));
                }
                catch (TimeoutException) { timeouts++; }
            }
            if (status != "stopped") status = "completed";
        }
        catch (OperationCanceledException) { status = "interrupted"; }
        catch (Exception failure) { status = "failed"; error = failure.GetType().Name + ": " + failure.Message; store.Log(failure); }
        finally
        {
            bool saved = false; int samples = 0, historyIssues = 0, timingGaps = 0;
            bool collectorStopped = true;
            stop.Cancel();
            if (collectionTask != null) collectorStopped = await Task.WhenAny(collectionTask, Task.Delay(5000)) == collectionTask;
            try
            {
                var report = await Task.Run(() => HistoryAnalysis.Analyze(store, store.Settings, 90, hardwareId: collector?.HardwareId)).WaitAsync(TimeSpan.FromSeconds(10));
                samples = report.SampleCount; historyIssues = report.HistoryQuality.IssueCount; timingGaps = report.SamplingGapCount;
                saved = samples > 0;
            }
            catch (Exception failure) { error += " History verification: " + failure.GetType().Name; }
            bool errors = File.Exists(Path.Combine(store.Root, "errors.log"));
            bool passed = status == "completed" && timer.Elapsed.TotalSeconds >= options.Seconds && evidence.Healthy && timeouts == 0 && saved && historyIssues == 0 && timingGaps == 0 && collectorStopped && !errors;
            try { SettingsFile.AtomicWrite(resultPath, JsonSerializer.SerializeToUtf8Bytes(new
            { Status = status, ChecksPassed = passed, Started = started, Ended = DateTimeOffset.UtcNow, RequestedSeconds = options.Seconds,
                ObservedSeconds = timer.Elapsed.TotalSeconds, Version = version, ExecutableSha256 = executableHash, ManagedAssemblySha256 = managedHash, DispatcherTimeouts = timeouts,
                SavedSamples = samples, HistoryIssues = historyIssues, TimingGaps = timingGaps, CollectorStopped = collectorStopped, ErrorLogExists = errors, Evidence = evidence.Snapshot(), Error = error,
                Scope = "This proves only the recorded local run. GPU absence is reported, not treated as zero load. Resource statistics do not establish a universal memory/CPU budget, lifetime stability, sensor accuracy, or sleep/resume compatibility." })); }
            finally { soakFinished = true; Environment.ExitCode = passed ? 0 : status == "stopped" ? 2 : 1; await Dispatcher.InvokeAsync(Exit); }
        }
    }
}
