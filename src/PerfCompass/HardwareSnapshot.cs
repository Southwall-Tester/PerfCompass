using LibreHardwareMonitor.Hardware;

namespace PerfCompass;

/// <summary>Read detached values only after this poll's entire device tree updated successfully.</summary>
internal sealed class HardwareSnapshot
{
    private sealed record Reading(SensorType Type, string Name, float? Value);
    private readonly Dictionary<IHardware, Reading[]> readings = new(ReferenceEqualityComparer.Instance);
    private readonly List<string> failures = [];
    public string[] Failures => failures.ToArray();

    internal static HardwareSnapshot Capture(IEnumerable<IHardware> devices, Action<Exception> log, Action<IHardware>? update = null)
    {
        var snapshot = new HardwareSnapshot();
        void Refresh(IHardware device)
        {
            if (update == null) device.Update(); else update(device);
            foreach (var child in device.SubHardware) Refresh(child);
        }
        foreach (var device in devices)
        {
            try
            {
                Refresh(device);
                // Never retain the sensor objects: their Value properties may still contain
                // old readings after a later failed update or change during another poll.
                snapshot.readings[device] = device.Sensors.Select(s => new Reading(s.SensorType, s.Name, s.Value)).ToArray();
            }
            catch (Exception error) { snapshot.failures.Add(device.Name); log(error); }
        }
        return snapshot;
    }

    internal double? Value(IHardware? device, SensorType type, params string[] names) => Observe(device, type, names).Value;
    internal (double? Value, string Source) Observe(IHardware? device, SensorType type, params string[] names)
    {
        if (device == null || !readings.TryGetValue(device, out var values)) return (null, "");
        foreach (var name in names)
            foreach (var reading in values.Where(s => s.Type == type && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                if (reading.Value is float value && float.IsFinite(value)
                    && (type != SensorType.Load || value is >= 0 and <= 100)
                    && (type != SensorType.SmallData || value >= 0)) return (value, reading.Name);
        return (null, "");
    }
    internal IReadOnlyList<GpuLoadObservation> GpuLoads(IHardware? device) => device != null && readings.TryGetValue(device, out var values)
        ? GpuLoadObservations.Normalize(values.Where(v => v.Type == SensorType.Load).Select(v => (v.Name, (double?)v.Value))) : [];
}
