using System.Reflection;
using System.Runtime.CompilerServices;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Gpu;
using LibreHardwareMonitor.Interop;

namespace PerfCompass;

public static partial class SelfTests
{
    private sealed class VendorPollSettings : ISettings
    {
        private readonly Dictionary<string, string> entries = [];
        public int Changes;
        public bool Contains(string name) => entries.ContainsKey(name);
        public string GetValue(string name, string value) => entries.GetValueOrDefault(name, value);
        public void SetValue(string name, string value) { entries[name] = value; Changes++; }
        public void Remove(string name) { entries.Remove(name); Changes++; }
    }
    private sealed class VendorPollHardware(ISettings settings) : Hardware("synthetic vendor sensor owner", new Identifier("vendor-test"), settings)
    {
        public override HardwareType HardwareType => HardwareType.GpuNvidia;
        public override void Update() { }
    }

    private static void RunGpuVendorPollTests(Action<string, Action> check, Action<bool, string> assert)
    {
        var settings = new VendorPollSettings(); var owner = new VendorPollHardware(settings); int sensorIndex = 20;
        Sensor Reading(string name, float value, SensorType type = SensorType.Load) => new(name, sensorIndex++, type, owner, settings) { Value = value };
        void Put(object target, string field, object? value) => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
        void Clear(object target) => target.GetType().GetMethod("ClearDynamicValues", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
        // Uninitialized vendor objects are only used to call their managed clear/
        // selection methods. No constructor, Update, Computer or native API runs.
        object Shell(Type type)
        {
            object shell = RuntimeHelpers.GetUninitializedObject(type);
            typeof(Hardware).GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, new HashSet<ISensor>());
            return shell;
        }

        check("Nvidia managed poll reset clears all product load memory and temperature outputs without native queries", () =>
        {
            object gpu = Shell(typeof(NvidiaGpu));
            var core = Reading("GPU Core", 0); var video = Reading("GPU Video Engine", 100);
            var temperature = Reading("GPU Core", 65, SensorType.Temperature);
            Put(gpu, "_loads", new[] { core, video }); Put(gpu, "_temperatures", new[] { temperature });
            var dynamic = new[] { "_memoryUsed", "_memoryFree", "_memoryTotal", "_memoryLoad", "_gpuDedicatedMemoryUsage", "_gpuSharedMemoryUsage", "_hotSpotTemperature", "_memoryJunctionTemperature" }
                .Select(field => (Field: field, Sensor: Reading(field, 50))).ToArray();
            foreach (var item in dynamic) Put(gpu, item.Field, item.Sensor);
            int changes = settings.Changes; Clear(gpu);
            assert(core.Value == null && video.Value == null && temperature.Value == null && dynamic.All(item => item.Sensor.Value == null)
                && core.Min == 0 && video.Max == 100 && settings.Changes == changes,
                "a silent failed Nvidia query could reuse old output, reset extrema or change settings");
            core.Value = 0; video.Value = 100; Clear(gpu);
            assert(core.Value == null && video.Value == null, "a repeated poll retained unchanged high or low cached readings");
        });
        check("AMD poll reset preserves construction-only capacity and control settings while clearing dynamic evidence", () =>
        {
            object gpu = Shell(typeof(AmdGpu)); var capacity = Reading("GPU Memory Total", 8192, SensorType.SmallData);
            var controlSensor = Reading("GPU Fan", 60, SensorType.Control);
            var control = new Control(controlSensor, settings, 0, 100); control.SetSoftware(55);
            Put(gpu, "_memoryTotal", capacity); Put(gpu, "_fanControl", control); Put(gpu, "_controlSensor", controlSensor);
            var dynamic = new[] { "_coreLoad", "_memoryLoad", "_memoryUsed", "_memoryFree", "_gpuDedicatedMemoryUsage", "_gpuDedicatedMemoryFree", "_gpuDedicatedMemoryTotal",
                "_gpuSharedMemoryUsage", "_gpuSharedMemoryFree", "_gpuSharedMemoryTotal", "_temperatureCore", "_temperatureHotSpot", "_temperatureLiquid",
                "_temperatureMemory", "_temperatureMvdd", "_temperaturePlx", "_temperatureSoC", "_temperatureVddc" }
                .Select(field => (Field: field, Sensor: Reading(field, 90))).ToArray();
            foreach (var item in dynamic) Put(gpu, item.Field, item.Sensor);
            int changes = settings.Changes; Clear(gpu);
            assert(capacity.Value == 8192 && dynamic.All(item => item.Sensor.Value == null) && controlSensor.Value == null
                && control.ControlMode == ControlMode.Software && control.SoftwareValue == 55 && settings.Changes == changes,
                "AMD static memory capacity/control configuration was erased or dynamic output retained");
        });
        check("Sparse Nvidia sensor indices keep video and clock domains associated with their original sensor", () =>
        {
            var core = new Sensor("GPU Core", 0, SensorType.Load, owner, settings) { Value = 1 };
            var video = new Sensor("GPU Video Engine", 2, SensorType.Load, owner, settings) { Value = 80 };
            GpuVendorPoll.Clear(core, video);
            GpuVendorPoll.WriteIndexed([core, video], 2, 95);
            assert(core.Value == null && video.Value == 95, "sparse VideoEngine index wrote the compact array position");
            GpuVendorPoll.WriteIndexed([core, video], 1, 100);
            assert(core.Value == null && video.Value == 95, "unsupported native domain changed another sensor");
            GpuVendorPoll.WriteIndexed([core, video], 0, 0);
            assert(core.Value == 0 && video.Value == 95, "new valid zero was mistaken for missing or stale output");
        });
        check("PMLog accepts unchanged values only when provider timestamp advances and rebuilds after invalid snapshots", () =>
        {
            var tracker = new GpuProviderTimestamp(); var load = Reading("GPU Core", 0);
            bool Poll(ulong stamp, uint rate, float value)
            {
                GpuVendorPoll.Clear(load); bool fresh = tracker.Observe(stamp, rate); if (fresh) load.Value = value; return fresh;
            }
            assert(!Poll(100, 1000, 90) && load.Value == null, "first provider timestamp was assumed current");
            assert(Poll(101, 1000, 90) && load.Value == 90 && Poll(102, 1000, 90) && load.Value == 90,
                "equal sensor values with advancing timestamps were rejected as stale");
            assert(!Poll(102, 1000, 90) && load.Value == null, "repeated provider snapshot acquired another poll's coverage");
            assert(!Poll(50, 1000, 90) && load.Value == null && Poll(51, 1000, 90), "provider timestamp regression did not rebuild its baseline");
            foreach (var invalid in new[] { (Stamp: 0UL, Rate: 1000U), (Stamp: 60UL, Rate: 0U), (Stamp: 60UL, Rate: 86400001U) })
            {
                assert(!Poll(invalid.Stamp, invalid.Rate, 100) && load.Value == null && !Poll(70, 1000, 100)
                    && Poll(71, 1000, 100), "invalid provider data skipped baseline rebuilding");
            }
            tracker.Reset(); assert(!Poll(80, 1000, 90) && Poll(81, 1000, 90), "explicit query failure reused the old timestamp baseline");
        });
        check("PMLog sparse sensor tables reject malformed duplicate and absent entries without manufacturing zero", () =>
        {
            assert(GpuVendorPoll.TryReadPmLog([8, 40, 19, 0, 0, 0], 19, out float zero) && zero == 0,
                "valid idle PMLog entry was rejected");
            foreach (uint[] table in new uint[][] { [], [19], [8, 40, 0, 0], [19, 100, 19, 0], new uint[514] })
                assert(!GpuVendorPoll.TryReadPmLog(table, 19, out _), "malformed, missing or duplicate PMLog entry acquired a value");
        });
        check("AMD managed PMLog selection retains same-poll fallback and does not reuse an older poll", () =>
        {
            object gpu = Shell(typeof(AmdGpu)); var load = Reading("GPU Core", 15); Put(gpu, "_coreLoad", load);
            var method = typeof(AmdGpu).GetMethod("GetAdlSensor", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var kind = AtiAdlxx.ADLPMLogSensors.ADL_PMLOG_INFO_ACTIVITY_GFX; int id = (int)kind;
            var pm = new AtiAdlxx.ADLPMLogData(); var od8 = new AtiAdlxx.ADLPMLogDataOutput();
            bool Apply(bool reset) => (bool)method.Invoke(gpu, [pm, od8, kind, load, 1f, reset])!;
            assert(!Apply(false) && load.Value == 15, "missing later provider erased a successful same-poll fallback");
            Clear(gpu); assert(!Apply(false) && load.Value == null, "reset:false carried a previous poll's fallback");
            od8.sensors = new AtiAdlxx.ADLSingleSensorData[id + 1]; od8.sensors[id] = new() { supported = 1, value = 70 };
            Put(gpu, "_overdrive8LogExists", true);
            assert(Apply(false) && load.Value == 70, "successful current OD8 fallback was discarded");
            var supported = new AtiAdlxx.ADLPMLogSupportInfo { usSensors = new ushort[AtiAdlxx.ADL_PMLOG_MAX_SENSORS] };
            supported.usSensors[0] = (ushort)id; Put(gpu, "_adlPMLogSupportInfo", supported); Put(gpu, "_pmLogStarted", true);
            Put(gpu, "_pmLogSampleCurrent", true); pm.ulValues = [(uint)id, 90, 0, 0];
            assert(Apply(false) && load.Value == 90, "new shared PMLog sample lost its same-poll priority");
            Put(gpu, "_pmLogSampleCurrent", false);
            assert(Apply(false) && load.Value == 70, "unchanged PMLog snapshot suppressed a successful fresh query fallback");
        });
        check("GPU identity matching cannot accept partial PNP identity or another PCI device on the same bus", () =>
        {
            const string pnp = "PCI\\VEN_10DE&DEV_1234\\4&ABC&0&0010";
            assert(GpuVendorPoll.SameDevice(pnp, pnp.ToLowerInvariant()) && !GpuVendorPoll.SameDevice(pnp, "PCI\\VEN_10DE&DEV_1234")
                && !GpuVendorPoll.SameDevice("", ""), "partial or empty PNP identity selected an adapter");
            const string location = "@pci.sys;PCI bus %1, device %2, function %3;(38,1,2)";
            assert(GpuVendorPoll.MatchesPciLocation(location, 0, 38, 1, "0000:26:01.2")
                && !GpuVendorPoll.MatchesPciLocation(location, 0, 38, 0, "0000:26:00.2")
                && !GpuVendorPoll.MatchesPciLocation(location, 0, 38, 1, "0000:26:01.0")
                && !GpuVendorPoll.MatchesPciLocation(location, 1, 38, 1, "0001:26:01.2"),
                "bus-only, function or PCI domain mismatch selected another adapter");
        });
        check("AMD successful activity queries cannot clamp malformed utilization into credible idle or full load", () =>
        {
            assert(GpuVendorPoll.LoadPercent(0) == 0 && GpuVendorPoll.LoadPercent(100) == 100
                && GpuVendorPoll.LoadPercent(54.5f) == 54.5f, "valid activity limits or fractional values were discarded");
            foreach (float value in new[] { -1f, 101f, float.MaxValue, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                assert(GpuVendorPoll.LoadPercent(value) == null, "invalid activity became a plausible 0% or 100% observation");
        });

        const uint model = 0x2D60, subsystem = 0x123410DE;
        string PathFor(string instance, uint device = model, uint board = subsystem) =>
            $@"\\?\PCI#VEN_10DE&DEV_{device:X4}&SUBSYS_{board:X8}&REV_A1#{instance}#{{5b45201d-f2f2-4f3b-85bb-30ff1f953599}}";
        var targetIdentity = new GpuVendorPoll.NvidiaIdentity(38, model, subsystem);
        var otherIdentity = targetIdentity with { Bus = 77 };
        string targetPath = PathFor("4&TARGET&0&0012"), otherPath = PathFor("4&OTHER&0&0000");
        var targetAddress = new GpuVendorPoll.PciAddress(38, 1, 2);
        var addresses = new Dictionary<string, GpuVendorPoll.PciAddress>(StringComparer.OrdinalIgnoreCase)
        { [targetPath] = targetAddress, [otherPath] = new(77, 0, 0) };
        GpuVendorPoll.PciAddress? Address(string path) => addresses.TryGetValue(path, out var address) ? address : null;

        check("Nvidia same-model adapters are matched by unique native identity independently of enumeration order", () =>
        {
            foreach (var physical in new GpuVendorPoll.NvidiaIdentity?[][] { [targetIdentity, otherIdentity], [otherIdentity, targetIdentity] })
                foreach (var paths in new[] { new[] { targetPath, otherPath }, new[] { otherPath, targetPath } })
                {
                    var match = GpuVendorPoll.SelectNvidiaD3D(targetIdentity, physical, paths, Address);
                    assert(match?.Identifier == targetPath && match.Address == targetAddress,
                        "same-model GPU or reordered native enumeration selected the first/index-correlated adapter");
                }
        });
        check("Nvidia same-bus different-model candidates remain distinguishable while same-identity ambiguity is refused", () =>
        {
            var differentModel = targetIdentity with { DeviceId = 0x2704 };
            string differentPath = PathFor("4&DIFFERENT&0&0000", differentModel.DeviceId);
            GpuVendorPoll.PciAddress? OnSameBus(string path) => path == targetPath ? targetAddress : new(38, 0, 0);
            assert(GpuVendorPoll.SelectNvidiaD3D(targetIdentity, [differentModel, targetIdentity], [differentPath, targetPath], OnSameBus)?.Identifier == targetPath,
                "distinct model on the same bus blocked an unambiguous adapter");
            assert(GpuVendorPoll.SelectNvidiaD3D(targetIdentity, [targetIdentity, targetIdentity], [targetPath, otherPath], Address) == null,
                "same-bus same-model physical adapters were guessed from enumeration order");
            assert(GpuVendorPoll.SelectNvidiaD3D(targetIdentity, [targetIdentity], [targetPath, otherPath], OnSameBus) == null,
                "ambiguous same-model Windows candidates were reduced to first match");
        });
        check("Nvidia incomplete candidate identities or duplicate Windows BDF addresses cannot authorize a cross-API match", () =>
        {
            foreach (var physical in new GpuVendorPoll.NvidiaIdentity?[][] { [], [null, targetIdentity], [otherIdentity], [targetIdentity with { DeviceId = 0 }], [targetIdentity with { Bus = 256 }] })
                assert(GpuVendorPoll.SelectNvidiaD3D(targetIdentity, physical, [targetPath], Address) == null,
                    "missing/failed native identity or missing target was treated as a complete unique inventory");
            string collision = PathFor("4&COLLISION&0&0012", 0x2704);
            assert(GpuVendorPoll.SelectNvidiaD3D(targetIdentity, [targetIdentity], [targetPath, collision], _ => targetAddress) == null,
                "Windows address collision with a different identity was ignored");
            assert(GpuVendorPoll.SelectNvidiaD3D(targetIdentity, [targetIdentity], [targetPath, otherPath], p => p == targetPath ? targetAddress : null) == null,
                "an unreadable candidate could hide a second adapter with the same reduced address");
        });
        check("Nvidia sparse Windows PCI addresses and repeated aliases preserve exact device and function", () =>
        {
            var sparse = new GpuVendorPoll.PciAddress(38, 23, 6);
            var match = GpuVendorPoll.SelectNvidiaD3D(targetIdentity, [targetIdentity], [targetPath.ToLowerInvariant(), targetPath], _ => sparse);
            assert(match != null && match.Address == sparse, "sparse slot/function was replaced with hard-coded 00.0 or duplicate spelling was considered another adapter");
            foreach (var invalid in new GpuVendorPoll.PciAddress[] { new(38, 32, 0), new(38, 0, 8), new(-1, 0, 0) })
                assert(GpuVendorPoll.SelectNvidiaD3D(targetIdentity, [targetIdentity], [targetPath], _ => invalid) == null,
                    "invalid Windows BDF became a valid adapter address");
        });
        check("Nvidia NVML PCI validation rejects another model domain function or internally inconsistent address", () =>
        {
            var match = GpuVendorPoll.SelectNvidiaD3D(targetIdentity, [targetIdentity], [targetPath], Address)!;
            var pci = new NvidiaML.NvmlPciInfo { domain = 0, bus = 38, device = 1, busId = "0000:26:01.2",
                pciVendorId = 0x10DE, pciDeviceId = (ushort)model, pciSubSystemId = subsystem };
            assert(GpuVendorPoll.MatchesNvml(match, pci), "complete matching PCI identity was rejected");
            var wrongBus = pci; wrongBus.bus = 77;
            var wrongModel = pci; wrongModel.pciDeviceId = 0x2704;
            var wrongBoard = pci; wrongBoard.pciSubSystemId++;
            var wrongDomain = pci; wrongDomain.domain = 1; wrongDomain.busId = "0001:26:01.2";
            var wrongFunction = pci; wrongFunction.busId = "0000:26:01.0";
            var contradictoryAddress = pci; contradictoryAddress.busId = "0000:4D:01.2";
            foreach (var invalid in new[] { wrongBus, wrongModel, wrongBoard, wrongDomain, wrongFunction, contradictoryAddress })
                assert(!GpuVendorPoll.MatchesNvml(match, invalid), "unverified NVML identity could supply readings for another GPU");
        });
        check("Nvidia optional NVML lookup uses exact PCI address and failures cannot discard an already matched D3D path", () =>
        {
            var match = GpuVendorPoll.SelectNvidiaD3D(targetIdentity, [targetIdentity], [targetPath], Address)!;
            var handle = new NvidiaML.NvmlDevice { Handle = new IntPtr(77) };
            var pci = new NvidiaML.NvmlPciInfo { domain = 0, bus = 38, device = 1, busId = "0000:26:01.2",
                pciVendorId = 0x10DE, pciDeviceId = (ushort)model, pciSubSystemId = subsystem };
            string? query = null;
            var found = GpuVendorPoll.ResolveNvml(match, () => true, key => { query = key; return handle; }, _ => pci);
            assert(found?.Handle == handle.Handle && query == "0000:26:01.2", "NVML lookup guessed an enumeration index, whitespace or hard-coded PCI slot/function");
            foreach (Func<string, NvidiaML.NvmlDevice?> lookup in new Func<string, NvidiaML.NvmlDevice?>[]
                { _ => null, _ => throw new InvalidOperationException("synthetic lookup failure") })
                assert(GpuVendorPoll.ResolveNvml(match, () => true, lookup, _ => pci) == null && match.Identifier == targetPath,
                    "optional NVML failure aborted the independently verified D3D identity");
            assert(GpuVendorPoll.ResolveNvml(match, () => false, _ => handle, _ => pci) == null
                && GpuVendorPoll.ResolveNvml(match, () => throw new InvalidOperationException("synthetic init failure"), _ => handle, _ => pci) == null
                && GpuVendorPoll.ResolveNvml(match, () => true, _ => handle, _ => null) == null
                && match.Identifier == targetPath, "initialization/PCI-info failure promoted an unverified NVML handle or destroyed the D3D match");
        });
    }
}
