using System.IO;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Gpu;
using LibreHardwareMonitor.Interop;

namespace PerfCompass;

public static partial class SelfTests
{
    private sealed class IntelTelemetrySettings : ISettings
    {
        public bool Contains(string name) => false;
        public string GetValue(string name, string value) => value;
        public void SetValue(string name, string value) { }
        public void Remove(string name) { }
    }

    private static void RunIntelTelemetryTests(Action<string, Action> check, Action<bool, string> assert)
    {
        static IntelGcl.ctl_oc_telemetry_item_t Item(double value, IntelGcl.ctl_units_t unit, bool supported = true) => new()
        {
            bSupported = supported, units = unit, type = IntelGcl.ctl_data_type_t.CTL_DATA_TYPE_DOUBLE,
            value = new IntelGcl.ctl_data_value_t { datadouble = value }
        };
        static IntelGcl.ctl_power_telemetry_t Frame(double time, double active, double energy) => new()
        {
            timeStamp = Item(time, IntelGcl.ctl_units_t.CTL_UNITS_TIME_SECONDS),
            globalActivityCounter = Item(active, IntelGcl.ctl_units_t.CTL_UNITS_TIME_SECONDS),
            renderComputeActivityCounter = Item(active / 2, IntelGcl.ctl_units_t.CTL_UNITS_TIME_SECONDS),
            mediaActivityCounter = Item(active / 4, IntelGcl.ctl_units_t.CTL_UNITS_TIME_SECONDS),
            gpuEnergyCounter = Item(energy, IntelGcl.ctl_units_t.CTL_UNITS_ENERGY_JOULES),
            totalCardEnergyCounter = Item(energy * 2, IntelGcl.ctl_units_t.CTL_UNITS_ENERGY_JOULES),
            gpuCurrentTemperature = Item(55, IntelGcl.ctl_units_t.CTL_UNITS_TEMPERATURE_CELSIUS),
            vramCurrentTemperature = Item(60, IntelGcl.ctl_units_t.CTL_UNITS_TEMPERATURE_CELSIUS),
            gpuCurrentClockFrequency = Item(1200, IntelGcl.ctl_units_t.CTL_UNITS_FREQUENCY_MHZ),
            vramCurrentClockFrequency = Item(600, IntelGcl.ctl_units_t.CTL_UNITS_FREQUENCY_MHZ),
            gpuVoltage = Item(1.05, IntelGcl.ctl_units_t.CTL_UNITS_VOLTAGE_VOLTS),
            vramVoltage = Item(1.1, IntelGcl.ctl_units_t.CTL_UNITS_VOLTAGE_VOLTS),
            vramReadBandwidth = Item(2, IntelGcl.ctl_units_t.CTL_UNITS_BANDWIDTH_MBPS),
            vramWriteBandwidth = Item(3, IntelGcl.ctl_units_t.CTL_UNITS_MEM_SPEED_GBPS)
        };
        static D3DDisplayDevice.D3DDeviceInfo Memory(ulong totalGiB = 8, ulong usedGiB = 3) => new()
        { GpuVideoMemoryLimit = totalGiB * 1024 * 1024 * 1024, GpuDedicatedUsed = usedGiB * 1024 * 1024 * 1024 };
        static float? Value(IntelDiscreteGpu gpu, SensorType type, string name) => gpu.Sensors.SingleOrDefault(s => s.SensorType == type && s.Name == name)?.Value;
        static bool Near(float? actual, double expected) => actual.HasValue && Math.Abs(actual.Value - expected) < .0001;

        check("Intel telemetry uses seconds and joules with unknown initial values and valid unchanged loads", () =>
        {
            var counter = new IntelTelemetryCounters();
            var first = counter.Read(Frame(10, 20, 100)); var second = counter.Read(Frame(12, 21, 120));
            assert(first.Global == null && first.PowerGpu == null && Near(second.Global, 50) && Near(second.RenderCompute, 25)
                && Near(second.Media, 12.5) && Near(second.PowerGpu, 10) && Near(second.PowerTotal, 20), "initial or unit-correct delta wrong");
            var sameLoad = counter.Read(Frame(14, 22, 140));
            assert(Near(sameLoad.Global, 50), "unchanged utilization was mistaken for stale telemetry");
            var idle = counter.Read(Frame(16, 22, 140));
            assert(idle.Global == 0 && idle.PowerGpu == 0, "advanced timestamp with unchanged counters did not preserve valid zero");
        });
        check("Intel missing invalid wrong-unit and non-advancing timestamps have no wall-clock fallback", () =>
        {
            var invalid = new[]
            {
                Item(14, IntelGcl.ctl_units_t.CTL_UNITS_TIME_SECONDS, false),
                Item(14, IntelGcl.ctl_units_t.CTL_UNITS_ENERGY_JOULES),
                Item(double.NaN, IntelGcl.ctl_units_t.CTL_UNITS_TIME_SECONDS),
                Item(double.PositiveInfinity, IntelGcl.ctl_units_t.CTL_UNITS_TIME_SECONDS),
                Item(-1, IntelGcl.ctl_units_t.CTL_UNITS_TIME_SECONDS),
                Item(12, IntelGcl.ctl_units_t.CTL_UNITS_TIME_SECONDS), Item(11, IntelGcl.ctl_units_t.CTL_UNITS_TIME_SECONDS)
            };
            foreach (var timestamp in invalid)
            {
                var counters = new IntelTelemetryCounters(); counters.Read(Frame(10, 20, 100)); counters.Read(Frame(12, 21, 120));
                var bad = Frame(14, 22, 140); bad.timeStamp = timestamp;
                var result = counters.Read(bad); var prime = counters.Read(Frame(20, 24, 200)); var recovered = counters.Read(Frame(22, 25, 220));
                assert(result.Global == null && result.PowerGpu == null && prime.Global == null && prime.PowerGpu == null
                    && Near(recovered.Global, 50) && Near(recovered.PowerGpu, 10), "invalid source time reused a baseline or host-clock denominator");
            }
        });
        check("Intel provider time gaps invalidate the interval and require a new baseline", () =>
        {
            foreach (double gap in new[] { 15.01, 60, 3600, double.MaxValue })
            {
                var counter = new IntelTelemetryCounters(); counter.Read(Frame(10, 20, 100));
                var failed = counter.Read(Frame(10 + gap, 21, 120));
                var baseline = counter.Read(Frame(100, 30, 200));
                var recovered = counter.Read(Frame(102, 31, 220));
                assert(failed.Global == null && failed.PowerGpu == null && baseline.Global == null
                    && Near(recovered.Global, 50), "long interval was assigned to a short current sample or recovery failed");
            }
        });
        check("Intel D3D matching requires a unique model and complete BDF before choosing a device", () =>
        {
            const string first = @"\\?\PCI#VEN_8086&DEV_56A0&SUBSYS_00000000#one#{guid}";
            const string second = @"\\?\PCI#VEN_8086&DEV_56A0&SUBSYS_00000000#two#{guid}";
            const string other = @"\\?\PCI#VEN_8086&DEV_56A1&SUBSYS_00000000#three#{guid}";
            var expected = new IntelGcl.ctl_adapter_bdf_t { bus = 2, device = 3, function = 1 };
            string? Select(string[] ids, Func<string, (int Bus, int Device, int Function)?> bdf) =>
                IntelDiscreteGpu.SelectD3DDevice(ids, 0x8086, 0x56A0, expected, bdf);
            assert(Select([first], _ => null) == null && Select([first], _ => (2, 3, 0)) == null
                && Select([first], _ => (2, 4, 1)) == null && Select([other], _ => (2, 3, 1)) == null,
                "missing BDF, wrong function/device, or other model selected");
            assert(Select([first, second], _ => (2, 3, 1)) == null, "ambiguous address selected first adapter");
            assert(Select([first, second, other], id => id == second ? (2, 3, 1) : (2, 3, 0)) == second,
                "uniquely matched device was not retained independently of sensor-query success");
        });
        check("Intel one-counter failures reset that metric without erasing valid independent counter results", () =>
        {
            var invalid = new[]
            {
                Item(22, IntelGcl.ctl_units_t.CTL_UNITS_TIME_SECONDS, false),
                Item(22, IntelGcl.ctl_units_t.CTL_UNITS_PERCENT), Item(19, IntelGcl.ctl_units_t.CTL_UNITS_TIME_SECONDS),
                Item(24, IntelGcl.ctl_units_t.CTL_UNITS_TIME_SECONDS), Item(double.NaN, IntelGcl.ctl_units_t.CTL_UNITS_TIME_SECONDS),
                Item(double.PositiveInfinity, IntelGcl.ctl_units_t.CTL_UNITS_TIME_SECONDS)
            };
            foreach (var item in invalid)
            {
                var counters = new IntelTelemetryCounters(); counters.Read(Frame(10, 20, 100)); counters.Read(Frame(12, 21, 120));
                var bad = Frame(14, 22, 140); bad.globalActivityCounter = item;
                var result = counters.Read(bad); var prime = counters.Read(Frame(16, 23, 160)); var recovered = counters.Read(Frame(18, 24, 180));
                assert(result.Global == null && Near(result.RenderCompute, 25) && Near(result.PowerGpu, 10)
                    && prime.Global == null && Near(recovered.Global, 50), "counter contradiction was clamped or affected another metric");
            }
            var energy = new IntelTelemetryCounters(); energy.Read(Frame(10, 20, 100));
            var rollback = energy.Read(Frame(12, 21, 99));
            assert(rollback.PowerGpu == null && Near(rollback.Global, 50), "energy rollback became zero watts or suppressed valid activity");
            foreach (var invalidEnergy in new[]
            {
                Item(120, IntelGcl.ctl_units_t.CTL_UNITS_POWER_WATTS), Item(120, IntelGcl.ctl_units_t.CTL_UNITS_ENERGY_JOULES, false),
                Item(double.PositiveInfinity, IntelGcl.ctl_units_t.CTL_UNITS_ENERGY_JOULES)
            })
            {
                var counters = new IntelTelemetryCounters(); counters.Read(Frame(10, 20, 100));
                var bad = Frame(12, 21, 120); bad.gpuEnergyCounter = invalidEnergy; var result = counters.Read(bad);
                assert(result.PowerGpu == null && Near(result.PowerTotal, 20) && Near(result.Global, 50),
                    "unsupported or wrong-unit energy acquired watts or erased another counter");
            }
        });
        check("Intel real Update entry clears silent telemetry failure and re-primes while independent sources continue", () =>
        {
            IntelGcl.ctl_power_telemetry_t? frame = Frame(10, 20, 100);
            var backend = new IntelDiscreteGpuQueries { Telemetry = () => frame, Memory = () => Memory(), MemoryFrequencyMhz = () => 800, FanSpeeds = () => [1200] };
            var gpu = new IntelDiscreteGpu(new IntelTelemetrySettings(), backend);
            gpu.Update(); frame = Frame(12, 21, 120);
            var good = HardwareSnapshot.Capture([gpu], _ => { });
            assert(good.Value(gpu, SensorType.Load, "GPU Core") == 50, "fake backend did not reach real Intel Update and snapshot path");
            frame = null;
            var failed = HardwareSnapshot.Capture([gpu], _ => { });
            assert(failed.Value(gpu, SensorType.Load, "GPU Core") == null && Value(gpu, SensorType.Power, "GPU Package") == null
                && Value(gpu, SensorType.Temperature, "GPU Core") == null
                && Value(gpu, SensorType.SmallData, "GPU Memory Used") == 3072 && Value(gpu, SensorType.Clock, "GPU Memory") == 800
                && Value(gpu, SensorType.Fan, "GPU Fan") == 1200 && good.Value(gpu, SensorType.Load, "GPU Core") == 50,
                "silent failure retained cache, erased another source, or changed detached snapshot");
            frame = Frame(20, 24, 200); gpu.Update();
            assert(Value(gpu, SensorType.Load, "GPU Core") == null && Value(gpu, SensorType.Temperature, "GPU Core") == 55, "recovery reused pre-failure counter baseline");
            frame = Frame(22, 25, 220); gpu.Update();
            assert(Near(Value(gpu, SensorType.Load, "GPU Core"), 50), "counter did not recover after a new consecutive pair");
        });
        check("Intel telemetry exceptions and unsupported timestamp cannot preserve cached derived readings", () =>
        {
            var frame = Frame(10, 20, 100);
            var backend = new IntelDiscreteGpuQueries { Telemetry = () => frame, Memory = () => Memory() };
            var gpu = new IntelDiscreteGpu(new IntelTelemetrySettings(), backend);
            gpu.Update(); frame = Frame(12, 21, 120); gpu.Update();
            backend.Telemetry = () => throw new IOException("synthetic IGCL exception"); gpu.Update();
            assert(Value(gpu, SensorType.Load, "GPU Core") == null && Value(gpu, SensorType.Power, "GPU Package") == null
                && Value(gpu, SensorType.SmallData, "GPU Memory Used") == 3072, "exception retained telemetry or suppressed D3D memory");
            frame = Frame(20, 24, 200); frame.timeStamp.bSupported = false; backend.Telemetry = () => frame; gpu.Update();
            assert(Value(gpu, SensorType.Load, "GPU Core") == null && Value(gpu, SensorType.Temperature, "GPU Core") == 55,
                "unsupported timestamp acquired a host-clock rate or erased independent instantaneous telemetry");
            frame = Frame(22, 25, 220); gpu.Update(); assert(Value(gpu, SensorType.Load, "GPU Core") == null, "unsupported timestamp seeded recovery");
            frame = Frame(24, 26, 240); gpu.Update(); assert(Near(Value(gpu, SensorType.Load, "GPU Core"), 50), "timestamp support recovery failed");
        });
        check("Intel D3D memory failures and contradictions stay independent of valid telemetry", () =>
        {
            var frame = Frame(10, 20, 100);
            D3DDisplayDevice.D3DDeviceInfo? memory = Memory();
            var backend = new IntelDiscreteGpuQueries { Telemetry = () => frame, Memory = () => memory };
            var gpu = new IntelDiscreteGpu(new IntelTelemetrySettings(), backend);
            gpu.Update(); frame = Frame(12, 21, 120); gpu.Update();
            foreach (var badMemory in new D3DDisplayDevice.D3DDeviceInfo?[] { null, Memory(0, 0), Memory(8, 9) })
            {
                frame.timeStamp.value.datadouble += 2; frame.globalActivityCounter.value.datadouble += 1;
                memory = badMemory; gpu.Update();
                assert(Value(gpu, SensorType.SmallData, "GPU Memory Used") == null && Value(gpu, SensorType.SmallData, "GPU Memory Total") == null
                    && Value(gpu, SensorType.Load, "GPU Memory") == null && Near(Value(gpu, SensorType.Load, "GPU Core"), 50)
                    && Value(gpu, SensorType.Temperature, "GPU Core") == 55, "D3D failure retained memory or erased telemetry");
            }
            backend.Memory = () => throw new IOException("synthetic D3D memory exception"); gpu.Update();
            assert(Value(gpu, SensorType.SmallData, "GPU Memory Used") == null && Value(gpu, SensorType.Temperature, "GPU Core") == 55,
                "memory exception swallowed a valid instantaneous source");
        });
        check("Intel independent frequency and fan failures retain current telemetry fallback only", () =>
        {
            IntelGcl.ctl_power_telemetry_t? frame = Frame(10, 20, 100);
            var backend = new IntelDiscreteGpuQueries { Telemetry = () => frame, MemoryFrequencyMhz = () => 800, FanSpeeds = () => [1200] };
            var gpu = new IntelDiscreteGpu(new IntelTelemetrySettings(), backend); gpu.Update();
            backend.MemoryFrequencyMhz = () => throw new IOException("synthetic frequency exception");
            backend.FanSpeeds = () => throw new IOException("synthetic fan exception");
            frame = Frame(12, 21, 120); gpu.Update();
            assert(Value(gpu, SensorType.Clock, "GPU Memory") == 600 && Value(gpu, SensorType.Fan, "GPU Fan") == null
                && Near(Value(gpu, SensorType.Load, "GPU Core"), 50), "independent query failure erased current fallback or preserved fan cache");
            frame = null; gpu.Update();
            assert(Value(gpu, SensorType.Clock, "GPU Memory") == null, "failed frequency query fell back to an earlier telemetry frame");
        });
        check("Intel instantaneous telemetry checks units finite values and decimal bandwidth scales", () =>
        {
            var frame = Frame(10, 20, 100); var backend = new IntelDiscreteGpuQueries { Telemetry = () => frame };
            var gpu = new IntelDiscreteGpu(new IntelTelemetrySettings(), backend); gpu.Update();
            assert(Value(gpu, SensorType.Throughput, "GPU Memory Read") == 2_000_000
                && Value(gpu, SensorType.Throughput, "GPU Memory Write") == 3_000_000_000f, "bandwidth units became binary MiB/GiB");
            frame.gpuVoltage = Item(double.NaN, IntelGcl.ctl_units_t.CTL_UNITS_VOLTAGE_VOLTS);
            frame.gpuCurrentTemperature = Item(double.PositiveInfinity, IntelGcl.ctl_units_t.CTL_UNITS_TEMPERATURE_CELSIUS);
            frame.gpuCurrentClockFrequency = Item(1200, IntelGcl.ctl_units_t.CTL_UNITS_TIME_SECONDS);
            frame.vramReadBandwidth = Item(2, IntelGcl.ctl_units_t.CTL_UNITS_MEMORY_BYTES); gpu.Update();
            assert(Value(gpu, SensorType.Voltage, "GPU Core") == null && Value(gpu, SensorType.Temperature, "GPU Core") == null
                && Value(gpu, SensorType.Clock, "GPU Core") == null && Value(gpu, SensorType.Throughput, "GPU Memory Read") == null,
                "invalid instantaneous item retained a previous or invented reading");
        });
    }
}
