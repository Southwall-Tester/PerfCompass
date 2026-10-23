using System.IO;
using System.Reflection;
using LibreHardwareMonitor.Hardware;

namespace PerfCompass;

public static partial class SelfTests
{
    // Test doubles implement the installed library interfaces, keeping its mutable cache
    // behavior observable without writing drivers or changing a physical device.
    public class HardwareTestProxy : DispatchProxy
    {
        public string Label = "synthetic GPU";
        public ISensor[] Sensors = [];
        public IHardware[] Children = [];
        public Action OnUpdate = () => { };
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_Name" => Label,
            "get_Sensors" => Sensors,
            "get_SubHardware" => Children,
            "Update" => Update(),
            _ => throw new NotSupportedException(method.Name)
        };
        private object? Update() { OnUpdate(); return null; }
    }
    public class SensorTestProxy : DispatchProxy
    {
        public string Label = "GPU Core";
        public SensorType Type = SensorType.Load;
        public float? Reading = 5;
        public bool ThrowOnRead;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_Name" => Label,
            "get_SensorType" => Type,
            "get_Value" => ThrowOnRead ? throw new IOException("synthetic sensor read failure") : Reading,
            _ => throw new NotSupportedException(method.Name)
        };
    }
    private static void RunHardwareSnapshotTests(Action<string, Action> check, Action<bool, string> assert)
    {
        (IHardware Device, HardwareTestProxy Hardware, SensorTestProxy Sensor) Device(string label = "synthetic GPU")
        {
            var device = DispatchProxy.Create<IHardware, HardwareTestProxy>(); var hardware = (HardwareTestProxy)(object)device;
            var sensor = DispatchProxy.Create<ISensor, SensorTestProxy>(); var values = (SensorTestProxy)(object)sensor;
            hardware.Label = label; hardware.Sensors = [sensor]; return (device, hardware, values);
        }
        check("Failed hardware refresh cannot reuse either low or high cached readings", () =>
        {
            foreach (float cached in new float[] { 0, 100 })
            {
                var d = Device(); d.Sensor.Reading = cached; int errors = 0;
                var good = HardwareSnapshot.Capture([d.Device], _ => errors++);
                d.Hardware.OnUpdate = () => throw new IOException("synthetic update failure");
                var failed = HardwareSnapshot.Capture([d.Device], _ => errors++);
                assert(good.Value(d.Device, SensorType.Load, "GPU Core") == cached && failed.Value(d.Device, SensorType.Load, "GPU Core") == null
                    && failed.Failures.SequenceEqual(new[] { d.Hardware.Label }) && errors == 1, "cached load survived a failed update");
            }
        });
        check("Hardware snapshots detach values and recover without inheriting failure state", () =>
        {
            var d = Device(); var first = HardwareSnapshot.Capture([d.Device], _ => { }); d.Sensor.Reading = 80;
            d.Hardware.OnUpdate = () => throw new IOException("synthetic temporary failure");
            var failed = HardwareSnapshot.Capture([d.Device], _ => { }); d.Hardware.OnUpdate = () => { };
            var recovered = HardwareSnapshot.Capture([d.Device], _ => { });
            assert(first.Value(d.Device, SensorType.Load, "GPU Core") == 5 && failed.Value(d.Device, SensorType.Load, "GPU Core") == null
                && recovered.Value(d.Device, SensorType.Load, "GPU Core") == 80 && recovered.Failures.Length == 0, "snapshot changed after polling or failed to recover");
        });
        check("A child update failure invalidates its root while other devices remain independent", () =>
        {
            var a = Device("GPU A"); var b = Device("GPU B"); var child = Device("child"); var grandchild = Device("grandchild");
            a.Hardware.Children = [child.Device]; child.Hardware.Children = [grandchild.Device]; grandchild.Hardware.OnUpdate = () => throw new IOException("synthetic descendant failure");
            var frame = HardwareSnapshot.Capture([a.Device, b.Device], _ => { });
            assert(frame.Value(a.Device, SensorType.Load, "GPU Core") == null && frame.Value(b.Device, SensorType.Load, "GPU Core") == 5
                && frame.Failures.SequenceEqual(new[] { "GPU A" }), "failed tree reused root values or suppressed another device");
        });
        check("Reading failure discards the entire partially captured device", () =>
        {
            var d = Device(); var broken = DispatchProxy.Create<ISensor, SensorTestProxy>(); ((SensorTestProxy)(object)broken).ThrowOnRead = true;
            d.Hardware.Sensors = [d.Hardware.Sensors[0], broken];
            var frame = HardwareSnapshot.Capture([d.Device], _ => { });
            assert(frame.Value(d.Device, SensorType.Load, "GPU Core") == null && frame.Failures.Length == 1, "partial read became a valid device");
        });
        check("Out of range or nonfinite sensor values stay missing with valid aliases available", () =>
        {
            foreach (float invalid in new float[] { -1, 101, float.NaN, float.PositiveInfinity })
            {
                var d = Device(); d.Sensor.Reading = invalid; var alternative = DispatchProxy.Create<ISensor, SensorTestProxy>();
                var alternate = (SensorTestProxy)(object)alternative; alternate.Label = "GPU D3D 3D"; alternate.Reading = 45;
                d.Hardware.Sensors = [d.Hardware.Sensors[0], alternative]; var frame = HardwareSnapshot.Capture([d.Device], _ => { });
                assert(frame.Value(d.Device, SensorType.Load, "GPU Core") == null && frame.Value(d.Device, SensorType.Load, "GPU Core", "GPU D3D 3D") == 45, "invalid load clamped or valid fallback discarded");
                d.Sensor.Type = SensorType.SmallData; d.Sensor.Reading = -1;
                assert(HardwareSnapshot.Capture([d.Device], _ => { }).Value(d.Device, SensorType.SmallData, "GPU Core") == null, "negative VRAM accepted");
            }
        });
        check("Failed sensor polls retain active time but reduce history coverage and downgrade eligibility", () =>
        {
            var d = Device(); var now = DateTimeOffset.Now;
            var trace = new List<Sample>();
            for (int day = 0; day < 3; day++)
                for (int bucket = 0; bucket < 1200; bucket++)
                {
                    var raw = new List<Sample>();
                    for (int i = 0; i < 5; i++)
                    {
                        bool fail = i == 0; d.Hardware.OnUpdate = () => { if (fail) throw new IOException("synthetic update failure"); };
                        var frame = HardwareSnapshot.Capture([d.Device], _ => { });
                        raw.Add(new Sample { Time = now.AddDays(-day).AddSeconds(-bucket * 10 - i * 2), Seconds = 2, Active = true,
                            HardwareId = "sensor-test", Cpu = 5, CoreMax = 10, MemoryUsedGb = 9, MemoryTotalGb = 64, CommitGb = 11,
                            Gpu = frame.Value(d.Device, SensorType.Load, "GPU Core") });
                    }
                    var sample = SampleBatch.Merge(raw); assert(sample.Seconds == 10 && sample.Gpu == 5 && sample.GpuCoverage == 8, "failed poll became a zero or gained coverage"); trace.Add(sample);
                }
            var report = AnalysisEngine.Analyze(trace, new Settings(Representative: true) { RepresentativeHardwareId = "sensor-test", RepresentativeObservationVersion = ActivityRules.Version }, 7, asOf: now, hardwareId: "sensor-test");
            assert(report.Ready && report.ActiveHours == 10 && report.Cards.Single(c => c.Key == "gpu").Status == "unknown"
                && report.Cards.Single(c => c.Key == "cpu").Status == "surplus", "GPU failure hidden by average or suppressed independent CPU evidence");
        });
    }
}
