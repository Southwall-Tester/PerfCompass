using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows;
using LibreHardwareMonitor.Hardware;
using static LibreHardwareMonitor.Hardware.D3DDisplayDevice;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunGpuFreshnessUiTests(string output, Action<bool, string> require, Func<string, Task> screenshot)
    {
        var original = D3DDisplayDevice.QueryBackendOverride;
        var failure = new FailedD3DQueryUiBackend();
        var json = new JsonSerializerOptions { WriteIndented = true };
        var frames = new List<object>();
        File.WriteAllText(Path.Combine(output, "gpu-freshness-NOTICE.txt"),
            "This focused test starts with an explicitly empty isolated data directory. Actual GPU.Update, Collector and WPF binding are exercised. The managed D3D query backend returns a synthetic successful Open followed by a failed AdapterType query, and checks its synthetic handle is closed. No native handle, GPU workload, model service, driver setting or OS counter is changed. Recovery restores the actual native backend. Its first observation must rebuild the baseline without reporting utilization; later valid observations recover. Only previously known pure D3D categories are required to recover; already-unknown or invalid counters remain unknown. Independent Windows CPU and memory collection stays real. Snapshots taken during injection are test evidence, not this computer's workload history or a physical driver-fault/accuracy test.");

        static bool D3DSource(string source) => source.StartsWith("D3D ", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("GPU D3D ", StringComparison.OrdinalIgnoreCase);
        static GpuEngineSample[] D3DEngines(LiveSnapshot snapshot) => snapshot.Sample.GpuEngines?
            .Where(e => e.Sources.Length > 0 && e.Sources.All(D3DSource)).ToArray() ?? [];
        static bool WindowsAvailable(LiveSnapshot snapshot) => snapshot.Sample.Cpu is >= 0 and <= 100
            && snapshot.Sample.CoreMax is >= 0 and <= 100 && snapshot.Sample.MemoryTotalGb > 0
            && snapshot.Sample.MemoryUsedGb >= 0 && snapshot.Sample.Seconds is > 0 and <= 15;
        static bool SameSources(GpuEngineSample first, GpuEngineSample second) => first.Kind == second.Kind
            && first.Sources.Order(StringComparer.Ordinal).SequenceEqual(second.Sources.Order(StringComparer.Ordinal));
        static bool UnknownD3D(LiveSnapshot snapshot, GpuEngineSample[] expected)
        {
            var engines = D3DEngines(snapshot);
            return expected.Length > 0 && expected.All(e => engines.Any(v => SameSources(e, v)))
                && engines.All(e => e.Load == null && !e.Complete);
        }
        static bool KnownD3D(LiveSnapshot snapshot, GpuEngineSample[] expected)
        {
            var engines = D3DEngines(snapshot);
            return expected.Length > 0 && expected.All(e => engines.Any(v => SameSources(e, v)
                && v.Load is >= 0 and <= 100 && v.Complete));
        }
        async Task<LiveSnapshot> NextFrame(LiveSnapshot previous, string phase)
        {
            for (int i = 0; i < 300; i++)
            {
                if (live is { } current && !ReferenceEquals(current, previous)) return current;
                await Task.Delay(50);
            }
            throw new TimeoutException("Production Collector did not deliver the next live frame: " + phase);
        }
        void Save(string phase, LiveSnapshot snapshot)
        {
            frames.Add(new { Phase = phase, Snapshot = snapshot });
            File.WriteAllText(Path.Combine(output, "gpu-freshness-frames.json"), JsonSerializer.Serialize(frames, json));
        }
        bool ControlsMatch(LiveSnapshot snapshot) => GpuEngineLiveDetails.Text == GpuEngineEvidence.Describe(snapshot.Sample)
            && GpuValue.Text == Percent(snapshot.Sample.Gpu) && CpuValue.Text == Percent(snapshot.Sample.Cpu)
            && GpuMetricLabel.Text.Contains("核心/3D");

        require(original == null && collector!.GpuReadOverride == null && collector.HardwareUpdateOverride == null && !paused,
            "Focused GPU freshness test starts on the actual native backend and production Collector without engine or hardware overrides");
        try
        {
            var healthy = live!;
            for (int i = 0; i < 6 && !D3DEngines(healthy).Any(e => e.Load.HasValue && e.Complete); i++)
                healthy = await NextFrame(healthy, "initial actual D3D baseline");
            Save("actual-healthy", healthy);
            var tracked = D3DEngines(healthy).Where(e => e.Load.HasValue && e.Complete).ToArray();
            require(tracked.Length > 0 && WindowsAvailable(healthy) && GpuEngineEvidence.Valid(healthy.Sample),
                "Actual hardware exposes at least one known pure D3D category alongside real Windows CPU and memory");
            LiveNav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            GpuEngineLiveExpander.IsExpanded = true;
            GpuEngineLiveExpander.BringIntoView(new Rect(0, 0, 1, 1));
            require(ControlsMatch(healthy) && tracked.SelectMany(e => e.Sources).All(GpuEngineLiveDetails.Text.Contains),
                "Healthy native controls bind the actual D3D readings and raw node labels");
            await screenshot("50-gpu-freshness-healthy");

            D3DDisplayDevice.QueryBackendOverride = failure;
            var failed = live!;
            for (int i = 0; i < 6; i++)
            {
                failed = await NextFrame(failed, "injected D3D query failure");
                Save("injected-query-failure", failed);
                if (failure.FailedQueries > 0 && UnknownD3D(failed, tracked)) break;
            }
            require(failure.Opens > 0 && failure.FailedQueries == failure.Opens && failure.Closes == failure.Opens,
                "Actual GPU update reaches the injected failed D3D query and closes every successfully opened test handle");
            require(UnknownD3D(failed, tracked) && GpuEngineEvidence.Valid(failed.Sample),
                "Failed D3D queries clear previously known node loads while retaining their identities as unknown, never cached or zero");
            require(WindowsAvailable(failed) && failed.Sample.HardwareId == healthy.Sample.HardwareId && failed.GpuName == healthy.GpuName,
                "Independent real Windows CPU and memory polling continue on the same hardware during the D3D query failure");
            require(ControlsMatch(failed) && GpuEngineLiveDetails.Text.Contains("未取得")
                && GpuEngineLiveDetails.Text.Contains("部分读数缺失")
                && tracked.SelectMany(e => e.Sources).All(GpuEngineLiveDetails.Text.Contains),
                "Native live controls display the failed D3D nodes as unknown and incomplete with original source labels");
            await screenshot("51-gpu-freshness-query-failed");

            // Wait for a freshly delivered failed frame before restoring. CollectionLoop
            // awaits that UI delivery and then delays 2 seconds, so the next delivered
            // frame is captured without skipping the restored baseline-only observation.
            var lastFailed = await NextFrame(live!, "last failed frame before restore");
            Save("last-injected-failure", lastFailed);
            if (!UnknownD3D(lastFailed, tracked)) throw new InvalidDataException("D3D failure state changed before native restoration.");
            D3DDisplayDevice.QueryBackendOverride = original;
            var baseline = await NextFrame(lastFailed, "first actual frame after restoration");
            Save("actual-restored-first-baseline", baseline);
            require(ReferenceEquals(D3DDisplayDevice.QueryBackendOverride, original) && UnknownD3D(baseline, tracked)
                && baseline.Sample.HardwareId == healthy.Sample.HardwareId && baseline.GpuName == healthy.GpuName,
                "The first actual frame after restoring D3D rebuilds its baseline and keeps node utilization unknown on the same GPU");
            require(WindowsAvailable(baseline) && ControlsMatch(baseline) && GpuEngineLiveDetails.Text.Contains("未取得"),
                "Baseline-only recovery remains visibly unknown while independent Windows readings and live bindings continue");
            await screenshot("52-gpu-freshness-native-baseline");

            var recovered = baseline;
            for (int i = 0; i < 6; i++)
            {
                recovered = await NextFrame(recovered, "subsequent actual D3D recovery");
                Save("actual-restored-following-frame", recovered);
                if (KnownD3D(recovered, tracked)) break;
            }
            require(KnownD3D(recovered, tracked) && WindowsAvailable(recovered)
                && recovered.Sample.HardwareId == healthy.Sample.HardwareId && recovered.GpuName == healthy.GpuName
                && recovered.Sample.Time > baseline.Sample.Time && GpuEngineEvidence.Valid(recovered.Sample),
                "Subsequent actual counter deltas restore the previously known D3D categories without changing GPU or interrupting Windows collection");
            require(ControlsMatch(recovered) && tracked.SelectMany(e => e.Sources).All(GpuEngineLiveDetails.Text.Contains)
                && D3DDisplayDevice.QueryBackendOverride == null,
                "Recovered native controls bind the current real readings after the injection has been fully removed");
            await screenshot("53-gpu-freshness-native-recovered");
            File.WriteAllText(Path.Combine(output, "actual-gpu-freshness-inventory.json"), JsonSerializer.Serialize(new
            {
                recovered.GpuName, recovered.Sample.Time, recovered.Sample.HardwareId, recovered.Sample.Gpu,
                recovered.Sample.GpuMetricSource, recovered.Sample.GpuEngineVersion, recovered.Sample.GpuEngines,
                recovered.SensorStatus, recovered.SensorFailures,
                Notice = "Actual post-restoration inventory. Missing/invalid categories stay unknown; exposed sensors do not establish all-engine coverage, physical accuracy or long-term stability."
            }, json));
        }
        finally
        {
            D3DDisplayDevice.QueryBackendOverride = original;
            File.WriteAllText(Path.Combine(output, "gpu-freshness-query-lifetime.json"), JsonSerializer.Serialize(new
            {
                failure.Opens, failure.FailedQueries, failure.Closes, Identifiers = failure.Identifiers.Order(StringComparer.Ordinal).ToArray(),
                OverrideRestored = ReferenceEquals(D3DDisplayDevice.QueryBackendOverride, original),
                Notice = "Managed query-boundary injection through production GPU.Update; these counted handles are synthetic, not native driver handles."
            }, json));
        }
    }

    private sealed class FailedD3DQueryUiBackend : ID3DQueryBackend
    {
        private int opens, failedQueries, closes;
        private readonly ConcurrentDictionary<string, byte> identifiers = new(StringComparer.Ordinal);
        internal int Opens => Volatile.Read(ref opens);
        internal int FailedQueries => Volatile.Read(ref failedQueries);
        internal int Closes => Volatile.Read(ref closes);
        internal IEnumerable<string> Identifiers => identifiers.Keys;
        public bool Open(string identifier, out D3DAdapter adapter)
        {
            identifiers.TryAdd(identifier, 0);
            adapter = new((uint)Interlocked.Increment(ref opens), long.MinValue);
            return true;
        }
        public bool AdapterType(D3DAdapter adapter, out bool software, out bool integrated)
        {
            Interlocked.Increment(ref failedQueries); software = false; integrated = false; return false;
        }
        public bool Close(D3DAdapter adapter) { Interlocked.Increment(ref closes); return true; }
        public bool Counts(D3DAdapter adapter, out uint nodes, out uint segments) => throw new InvalidOperationException("Failed query must stop before node counts.");
        public bool NodeName(D3DAdapter adapter, uint id, out string name) => throw new InvalidOperationException("Failed query must stop before node names.");
        public bool NodeCounter(D3DAdapter adapter, uint id, out long runningTime) => throw new InvalidOperationException("Failed query must stop before node counters.");
        public long Timestamp() => throw new InvalidOperationException("Failed query must stop before timestamps.");
        public bool MemoryLimits(D3DAdapter adapter, out D3DMemoryLimits limits) => throw new InvalidOperationException("Failed query must stop before memory limits.");
        public bool Segment(D3DAdapter adapter, uint id, out D3DSegment segment) => throw new InvalidOperationException("Failed query must stop before memory segments.");
    }
}
