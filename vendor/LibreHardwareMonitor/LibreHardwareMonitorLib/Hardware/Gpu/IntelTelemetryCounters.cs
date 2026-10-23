// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// PerfCompass source adaptation: typed IGCL telemetry validation and counter intervals.

using System;
using LibreHardwareMonitor.Interop;

namespace LibreHardwareMonitor.Hardware.Gpu;

internal sealed class IntelDiscreteGpuQueries
{
    internal Func<IntelGcl.ctl_power_telemetry_t?> Telemetry = () => null;
    internal Func<D3DDisplayDevice.D3DDeviceInfo?> Memory = () => null;
    internal Func<double?> MemoryFrequencyMhz = () => null;
    internal Func<float?[]> FanSpeeds = () => Array.Empty<float?>();
    internal int FanCount = 1;
}

internal struct IntelTelemetryCounterReadings
{
    internal float? Global, RenderCompute, Media, PowerGpu, PowerTotal;
}

internal sealed class IntelTelemetryCounters
{
    private double? _timestamp;
    private readonly Counter _global = new(), _render = new(), _media = new(), _powerGpu = new(), _powerTotal = new();

    internal void Reset()
    {
        _timestamp = null;
        _global.Reset(); _render.Reset(); _media.Reset(); _powerGpu.Reset(); _powerTotal.Reset();
    }

    internal IntelTelemetryCounterReadings Read(IntelGcl.ctl_power_telemetry_t data)
    {
        // IGCL telemetry uses seconds, not ctl_engine_stats_t's microseconds or .NET
        // ticks. Unsupported timestamps have no host-clock replacement or mixed epoch.
        if (!TryRead(data.timeStamp, IntelGcl.ctl_units_t.CTL_UNITS_TIME_SECONDS, out double timestamp) || timestamp < 0
            || _timestamp.HasValue && timestamp <= _timestamp.Value)
        {
            Reset();
            return default;
        }

        double delta = _timestamp.HasValue ? timestamp - _timestamp.Value : double.NaN;
        if (_timestamp.HasValue && (!Finite(delta) || delta > D3DNodeSampler.MaximumIntervalSeconds))
        {
            // A long counter interval cannot be attributed to the current short
            // collection sample after suspension or a provider time discontinuity.
            Reset();
            return default;
        }
        _timestamp = timestamp;
        return new IntelTelemetryCounterReadings
        {
            Global = _global.Read(data.globalActivityCounter, IntelGcl.ctl_units_t.CTL_UNITS_TIME_SECONDS, delta, 100, 100),
            RenderCompute = _render.Read(data.renderComputeActivityCounter, IntelGcl.ctl_units_t.CTL_UNITS_TIME_SECONDS, delta, 100, 100),
            Media = _media.Read(data.mediaActivityCounter, IntelGcl.ctl_units_t.CTL_UNITS_TIME_SECONDS, delta, 100, 100),
            PowerGpu = _powerGpu.Read(data.gpuEnergyCounter, IntelGcl.ctl_units_t.CTL_UNITS_ENERGY_JOULES, delta, 1, float.MaxValue),
            PowerTotal = _powerTotal.Read(data.totalCardEnergyCounter, IntelGcl.ctl_units_t.CTL_UNITS_ENERGY_JOULES, delta, 1, float.MaxValue)
        };
    }

    private sealed class Counter
    {
        private double? _previous;
        internal void Reset() => _previous = null;
        internal float? Read(IntelGcl.ctl_oc_telemetry_item_t item, IntelGcl.ctl_units_t unit, double seconds, double scale, double maximum)
        {
            if (!TryRead(item, unit, out double current) || current < 0) { Reset(); return null; }
            if (!_previous.HasValue || !Finite(seconds) || seconds <= 0) { _previous = current; return null; }
            double difference = current - _previous.Value;
            double result = difference / seconds * scale;
            if (difference < 0 || !Finite(difference) || !Finite(result) || result < 0 || result > maximum)
            {
                // Never clamp a reset/contradiction into a plausible idle/full-load
                // value. The next valid sample establishes a new baseline only.
                Reset(); return null;
            }
            _previous = current;
            return (float)result;
        }
    }

    internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    internal static float? AsFloat(double value) => Finite(value) && value >= -float.MaxValue && value <= float.MaxValue ? (float)value : null;
    internal static bool TryRead(IntelGcl.ctl_oc_telemetry_item_t item, IntelGcl.ctl_units_t unit, out double value)
    {
        value = item.type switch
        {
            IntelGcl.ctl_data_type_t.CTL_DATA_TYPE_FLOAT => item.value.datafloat,
            IntelGcl.ctl_data_type_t.CTL_DATA_TYPE_DOUBLE => item.value.datadouble,
            IntelGcl.ctl_data_type_t.CTL_DATA_TYPE_UINT32 => item.value.datau32,
            IntelGcl.ctl_data_type_t.CTL_DATA_TYPE_INT32 => item.value.data32,
            IntelGcl.ctl_data_type_t.CTL_DATA_TYPE_UINT64 => item.value.datau64,
            IntelGcl.ctl_data_type_t.CTL_DATA_TYPE_INT64 => item.value.data64,
            IntelGcl.ctl_data_type_t.CTL_DATA_TYPE_UINT16 => item.value.datau16,
            IntelGcl.ctl_data_type_t.CTL_DATA_TYPE_INT16 => item.value.data16,
            IntelGcl.ctl_data_type_t.CTL_DATA_TYPE_UINT8 => item.value.datau8,
            IntelGcl.ctl_data_type_t.CTL_DATA_TYPE_INT8 => item.value.data8,
            _ => double.NaN
        };
        return item.bSupported && item.units == unit && Finite(value);
    }
}
