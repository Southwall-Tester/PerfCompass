using System.Diagnostics;
using System.IO;
using LibreHardwareMonitor.Hardware;

namespace PerfCompass;

public static partial class SelfTests
{
    private sealed class D3DTestSettings : ISettings
    {
        public bool Contains(string name) => false;
        public string GetValue(string name, string value) => value;
        public void SetValue(string name, string value) { }
        public void Remove(string name) { }
    }

    private sealed class D3DTestHardware : Hardware
    {
        internal readonly D3DNodeSensors Nodes;
        internal D3DTestBackend? Backend;
        internal D3DTestHardware(bool reserveLoadZero = false) : base("synthetic D3D GPU", new Identifier("d3d-test"), new D3DTestSettings())
        {
            if (reserveLoadZero) ActivateSensor(new Sensor("GPU Core", 0, SensorType.Load, this, _settings) { Value = 7 });
            Nodes = new D3DNodeSensors(this, _settings, 0, ActivateSensor, DeactivateSensor);
        }
        public override HardwareType HardwareType => HardwareType.GpuIntel;
        public override void Update()
        {
            if (Backend == null) return;
            try
            {
                if (D3DDisplayDevice.GetDeviceInfoByIdentifier("matched-test-device", out var frame, Backend)) Nodes.Update(frame);
                else Nodes.Invalidate();
            }
            catch { Nodes.Invalidate(); throw; }
        }
    }

    private sealed class D3DTestBackend : D3DDisplayDevice.ID3DQueryBackend
    {
        internal string FailAt = "", ThrowAt = "";
        internal uint NodeCount = 2, SegmentCount = 2;
        internal int OpenCount, CloseCount;
        internal long CurrentCounter = 10_000_000, CurrentTimestamp = Stopwatch.Frequency;
        internal bool Software;
        internal readonly List<string> Calls = [];
        private bool Pass(string stage)
        {
            Calls.Add(stage);
            if (ThrowAt == stage) throw new IOException("synthetic D3D query exception at " + stage);
            return FailAt != stage;
        }
        public bool Open(string identifier, out D3DDisplayDevice.D3DAdapter adapter)
        { OpenCount++; adapter = new(17, unchecked((long)0xABCD000000000123)); return Pass("open"); }
        public bool AdapterType(D3DDisplayDevice.D3DAdapter adapter, out bool software, out bool integrated)
        { software = Software; integrated = true; return Pass("adapter-type"); }
        public bool Counts(D3DDisplayDevice.D3DAdapter adapter, out uint nodes, out uint segments)
        { nodes = NodeCount; segments = SegmentCount; return Pass("counts"); }
        public bool NodeName(D3DDisplayDevice.D3DAdapter adapter, uint id, out string name)
        { name = id == 0 ? "D3D Compute_0" : "D3D Video Encode"; return Pass("node-name:" + id); }
        public bool NodeCounter(D3DDisplayDevice.D3DAdapter adapter, uint id, out long runningTime)
        { runningTime = CurrentCounter + id * 100; return Pass("node-counter:" + id); }
        public long Timestamp() { Pass("timestamp"); return CurrentTimestamp + Calls.Count; }
        public bool MemoryLimits(D3DDisplayDevice.D3DAdapter adapter, out D3DDisplayDevice.D3DMemoryLimits limits)
        { limits = new(1_000, 2_000, 300); return Pass("memory-limits"); }
        public bool Segment(D3DDisplayDevice.D3DAdapter adapter, uint id, out D3DDisplayDevice.D3DSegment segment)
        { segment = new(id == 0 ? 100UL : 200UL, id == 0 ? 110UL : 220UL, id == 0); return Pass("segment:" + id); }
        public bool Close(D3DDisplayDevice.D3DAdapter adapter) { CloseCount++; return Pass("close"); }
    }

    private static void RunD3DNodeTests(Action<string, Action> check, Action<bool, string> assert)
    {
        static D3DDisplayDevice.D3DDeviceNodeInfo Node(ulong id, long counter, long timestamp, string name = "D3D Compute_0")
            => new() { Id = id, Name = name, RunningTime = counter, QueryTimestamp = timestamp };
        static D3DDisplayDevice.D3DDeviceInfo Frame(params D3DDisplayDevice.D3DDeviceNodeInfo[] nodes)
            => new() { AdapterLuid = 27, Nodes = nodes };
        static float? Value(D3DNodeSampler sampler, D3DDisplayDevice.D3DDeviceInfo frame)
        {
            if (!sampler.TryUpdate(frame, out var readings)) throw new Exception("unexpected invalid raw frame");
            return readings.Single().Value;
        }

        check("D3D node load uses stable identities and each node's monotonic interval rather than list positions", () =>
        {
            var sampler = new D3DNodeSampler(1000);
            assert(sampler.TryUpdate(Frame(Node(7, 0, 1000), Node(2, 10_000_000, 2000, "D3D Video Encode")), out var first)
                && first.All(r => r.Value == null), "first frame fabricated utilization");
            assert(sampler.TryUpdate(Frame(Node(2, 20_000_000, 3000, "D3D Video Encode"), Node(7, 10_000_000, 3000)), out var second)
                && second.Single(r => r.Key.Id == 7).Value == 50 && second.Single(r => r.Key.Id == 2).Value == 100,
                "node reordering or different per-node intervals mixed the counters");
        });
        check("D3D large cumulative counters are subtracted before conversion and valid idle remains zero", () =>
        {
            var sampler = new D3DNodeSampler(1000);
            Value(sampler, Frame(Node(0, long.MaxValue - 20_000_000, long.MaxValue - 3000)));
            assert(Value(sampler, Frame(Node(0, long.MaxValue - 15_000_000, long.MaxValue - 2000))) == 50,
                "large cumulative totals lost the small busy delta");
            assert(Value(sampler, Frame(Node(0, long.MaxValue - 15_000_000, long.MaxValue - 1000))) == 0,
                "successful complete idle counters became missing or stale high load");
        });
        check("D3D reset, invalid counters, nonadvancing clocks and long gaps clear baselines without clamping", () =>
        {
            var invalid = new[] { Node(0, -1, 2000), Node(0, 9_000_000, 2000), Node(0, 15_000_000, 1000),
                Node(0, 15_000_000, 999), Node(0, 15_000_000, -1), Node(0, 25_000_000, 2000), Node(0, 20_000_000, 17000) };
            foreach (var bad in invalid)
            {
                var sampler = new D3DNodeSampler(1000); Value(sampler, Frame(Node(0, 10_000_000, 1000)));
                assert(Value(sampler, Frame(bad)) == null, "invalid interval was clamped or accepted");
                assert(Value(sampler, Frame(Node(0, 30_000_000, 20000))) == null, "first recovery frame reused a rejected baseline");
                assert(Value(sampler, Frame(Node(0, 35_000_000, 21000))) == 50, "second stable recovery frame failed to recover");
            }
            var boundary = new D3DNodeSampler(1000); Value(boundary, Frame(Node(0, 0, 0)));
            assert(Value(boundary, Frame(Node(0, 150_000_000, 15000))) == 100, "valid exact 15-second/100-percent boundary rejected");
        });
        check("D3D topology and adapter changes prime a new baseline while unchanged identities retain readings", () =>
        {
            var sampler = new D3DNodeSampler(1000); Value(sampler, Frame(Node(0, 0, 1000)));
            assert(Value(sampler, Frame(Node(0, 5_000_000, 2000))) == 50, "stable node not measured");
            assert(Value(sampler, Frame(Node(0, 10_000_000, 3000, "D3D Compute_1"))) == null, "renamed ordinal inherited the old node baseline");
            assert(Value(sampler, Frame(Node(0, 15_000_000, 4000, "D3D Compute_1"))) == 50, "renamed node did not recover");
            var moved = Frame(Node(0, 20_000_000, 5000, "D3D Compute_1")); moved.AdapterLuid = 28;
            assert(Value(sampler, moved) == null, "different adapter LUID reused a prior baseline");
            assert(sampler.TryUpdate(Frame(Node(0, 25_000_000, 6000), Node(9, 0, 6000)), out var added)
                && added.All(r => r.Value == null), "topology additions did not rebuild the frame baseline");
        });
        check("Malformed D3D node identities invalidate the frame and cannot leave a usable hidden baseline", () =>
        {
            D3DDisplayDevice.D3DDeviceInfo[] invalid = [default, Frame(Node(0, 0, 1000), Node(0, 0, 1000)),
                Frame(Node((ulong)uint.MaxValue + 1, 0, 1000)), Frame(Node(0, 0, 1000, ""))];
            foreach (var bad in invalid)
            {
                var sampler = new D3DNodeSampler(1000); Value(sampler, Frame(Node(0, 0, 0)));
                assert(!sampler.TryUpdate(bad, out var rejected) && rejected.Length == 0, "malformed raw topology accepted");
                assert(Value(sampler, Frame(Node(0, 5_000_000, 1000))) == null, "malformed frame preserved a baseline for later use");
            }
        });
        check("D3D sensor mapping exposes named unknowns, preserves duplicate labels and reserves vendor indexes", () =>
        {
            var hardware = new D3DTestHardware(reserveLoadZero: true); long f = Stopwatch.Frequency;
            hardware.Nodes.Update(Frame(Node(7, 0, f), Node(2, 0, f)));
            var nodes = hardware.Sensors.Where(s => s.Name == "D3D Compute_0").OrderBy(s => s.Index).ToArray();
            assert(nodes.Length == 2 && nodes.All(s => s.Value == null) && nodes.All(s => s.Index > 0)
                && nodes.Select(s => s.Identifier.ToString()).Distinct().Count() == 2, "duplicate labels collapsed or overlaid a vendor sensor index");
            hardware.Nodes.Update(Frame(Node(2, 10_000_000, 2 * f), Node(7, 5_000_000, 2 * f)));
            assert(nodes[0].Value == 50 && nodes[1].Value == 100 && hardware.Sensors.Single(s => s.Name == "GPU Core").Value == 7,
                "reordered nodes updated the wrong sensor or cleared an independent vendor value");
        });
        check("D3D removed and renamed sensors clear values and never recycle their identifiers into a new node", () =>
        {
            var hardware = new D3DTestHardware(); long f = Stopwatch.Frequency;
            hardware.Nodes.Update(Frame(Node(0, 0, f))); var original = hardware.Sensors.Single();
            hardware.Nodes.Update(Frame(Node(0, 5_000_000, 2 * f)));
            hardware.Nodes.Update(Frame(Node(0, 10_000_000, 3 * f, "D3D Video Encode")));
            var replacement = hardware.Sensors.Single();
            assert(original.Value == null && replacement.Value == null && replacement.Identifier.ToString() != original.Identifier.ToString(),
                "removed node retained a value or its identity was silently reassigned");
            hardware.Nodes.Update(Frame(Node(0, 15_000_000, 4 * f, "D3D Video Encode")));
            assert(replacement.Value == 50, "new stable topology did not recover");
            hardware.Nodes.Invalidate();
            assert(replacement.Value == null && hardware.Sensors.Contains(replacement), "failure disappeared as absence or retained a cached value");
            hardware.Nodes.Update(Frame(Node(0, 20_000_000, 5 * f, "D3D Video Encode")));
            assert(replacement.Value == null, "failed query reused a prior sensor baseline");
            hardware.Nodes.Update(Frame());
            assert(hardware.Sensors.Length == 0 && replacement.Value == null, "an empty successful topology retained removed sensors");
        });
        check("D3D native query false paths close every opened handle exactly once and publish no partial frame", () =>
        {
            foreach (string stage in new[] { "open", "adapter-type", "counts", "node-name:0", "node-counter:0", "node-name:1", "node-counter:1", "memory-limits", "segment:0", "segment:1", "close" })
            {
                var backend = new D3DTestBackend { FailAt = stage };
                bool success = D3DDisplayDevice.GetDeviceInfoByIdentifier("matched-test-device", out var frame, backend);
                assert(!success && frame.Nodes == null && frame.AdapterLuid == 0 && frame.GpuSharedUsed == 0 && frame.GpuDedicatedUsed == 0,
                    "failed query exposed partial data at " + stage);
                assert(backend.OpenCount == 1 && backend.CloseCount == (stage == "open" ? 0 : 1), "adapter lifetime wrong at " + stage);
            }
        });
        check("D3D native exceptions still close handles and a close exception cannot publish successful data", () =>
        {
            foreach (string stage in new[] { "open", "adapter-type", "node-counter:1", "timestamp", "memory-limits", "segment:1" })
            {
                var backend = new D3DTestBackend { ThrowAt = stage }; bool threw = false;
                D3DDisplayDevice.D3DDeviceInfo frame = default;
                try { D3DDisplayDevice.GetDeviceInfoByIdentifier("matched-test-device", out frame, backend); }
                catch (IOException) { threw = true; }
                assert(threw && frame.Nodes == null && backend.CloseCount == (stage == "open" ? 0 : 1), "exception leaked a frame or handle at " + stage);
            }
            var close = new D3DTestBackend { ThrowAt = "close" };
            assert(!D3DDisplayDevice.GetDeviceInfoByIdentifier("matched-test-device", out var failed, close) && failed.Nodes == null && close.CloseCount == 1,
                "close exception became a successful snapshot");
        });
        check("D3D native query success retains LUID, node-local QPC and independent memory segment totals", () =>
        {
            var backend = new D3DTestBackend();
            assert(D3DDisplayDevice.GetDeviceInfoByIdentifier("matched-test-device", out var frame, backend)
                && frame.AdapterLuid == unchecked((long)0xABCD000000000123) && frame.Nodes.Length == 2
                && frame.Nodes[0].QueryTimestamp != frame.Nodes[1].QueryTimestamp && frame.Nodes[0].RunningTime == 10_000_000
                && frame.GpuSharedUsed == 100 && frame.GpuDedicatedUsed == 200 && frame.GpuSharedMax == 110 && frame.GpuDedicatedMax == 220
                && backend.CloseCount == 1, "successful frame lost identity, timing or memory data");
            foreach (var invalid in new[] { new D3DTestBackend { Software = true }, new D3DTestBackend { NodeCount = 4097 }, new D3DTestBackend { SegmentCount = 4097 } })
                assert(!D3DDisplayDevice.GetDeviceInfoByIdentifier("matched-test-device", out _, invalid) && invalid.CloseCount == 1,
                    "unsupported or oversized device escaped cleanup");
        });
        check("The production D3D entrypoint shares the injected query implementation without invoking native hardware", () =>
        {
            var previous = D3DDisplayDevice.QueryBackendOverride; var backend = new D3DTestBackend();
            try
            {
                D3DDisplayDevice.QueryBackendOverride = backend;
                assert(D3DDisplayDevice.GetDeviceInfoByIdentifier("matched-test-device", out var frame) && frame.Nodes.Length == 2
                    && backend.OpenCount == 1 && backend.CloseCount == 1, "production entrypoint bypassed the tested query boundary");
            }
            finally { D3DDisplayDevice.QueryBackendOverride = previous; }
        });
        check("Patched D3D sensors flow through production snapshots with failure unknowns and two-frame recovery", () =>
        {
            var backend = new D3DTestBackend { NodeCount = 1 }; var hardware = new D3DTestHardware { Backend = backend };
            var first = HardwareSnapshot.Capture([hardware], _ => { });
            assert(first.GpuLoads(hardware).Single().Load == null, "first native-counter baseline became zero");
            backend.CurrentCounter += 5_000_000; backend.CurrentTimestamp += Stopwatch.Frequency;
            var measured = HardwareSnapshot.Capture([hardware], _ => { });
            assert(measured.GpuLoads(hardware).Single().Load is > 49.9 and < 50.1, "patched source did not reach detached production snapshot");
            backend.FailAt = "segment:1";
            var failed = HardwareSnapshot.Capture([hardware], _ => { });
            assert(failed.GpuLoads(hardware).Single().Load == null && !failed.GpuLoads(hardware).Single().Complete,
                "late native failure retained the previously measured engine value");
            backend.FailAt = ""; backend.CurrentCounter += 5_000_000; backend.CurrentTimestamp += Stopwatch.Frequency;
            var baseline = HardwareSnapshot.Capture([hardware], _ => { });
            assert(baseline.GpuLoads(hardware).Single().Load == null, "recovery stretched the old interval across a failed query");
            backend.CurrentCounter += 5_000_000; backend.CurrentTimestamp += Stopwatch.Frequency;
            var recovered = HardwareSnapshot.Capture([hardware], _ => { });
            assert(recovered.GpuLoads(hardware).Single().Load is > 49.9 and < 50.1
                && measured.GpuLoads(hardware).Single().Load is > 49.9 and < 50.1, "recovery failed or detached old snapshots changed");
        });
    }
}
