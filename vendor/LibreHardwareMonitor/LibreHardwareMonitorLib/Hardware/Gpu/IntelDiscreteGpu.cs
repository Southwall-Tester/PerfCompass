// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// Copyright (C) LibreHardwareMonitor and Contributors.
// All Rights Reserved.

using System;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Interop;
using Windows.Win32;
using Windows.Win32.Devices.DeviceAndDriverInstallation;
using Windows.Win32.Devices.Properties;
using Windows.Win32.Foundation;

namespace LibreHardwareMonitor.Hardware.Gpu;

internal sealed class IntelDiscreteGpu : GenericGpu
{
    // Constants
    private const double MemoryFrequencyDivisor = 8.0; // Intel GCL returns memory frequency multiplied by 8

    // Clock sensors
    private readonly Sensor _clockCore;
    private readonly Sensor _clockMemory;

    // Fan sensors
    private readonly Sensor[] _fans;

    // Utilization sensors
    private readonly Sensor _loadGlobalActivity;
    private readonly Sensor _loadMedia;
    private readonly Sensor _loadRenderCompute;

    // Power sensors
    private readonly Sensor _powerGpu;
    private readonly Sensor _powerTotal;

    // Temperature sensors
    private readonly Sensor _temperatureGpuCore;
    private readonly Sensor _temperatureMemory;

    // Voltage sensors
    private readonly Sensor _voltageCore;
    private readonly Sensor _voltageMemory;

    // Memory sensors
    private readonly Sensor _memoryFree;
    private readonly Sensor _memoryTotal;
    private readonly Sensor _memoryUsed;
    private readonly Sensor _memoryLoad;

    // Bandwidth sensors
    private readonly Sensor _memoryBandwidthRead;
    private readonly Sensor _memoryBandwidthWrite;

    private string _deviceId;
    private string _d3dDeviceId;

    // Intel GCL properties and data
    private readonly IntelGcl.ctl_device_adapter_handle_t _handle;

    private readonly IntelTelemetryCounters _counters = new();
    private readonly IntelDiscreteGpuQueries _queries;
    private IntelGcl.ctl_device_adapter_properties_t _properties;

    // Telemetry data
    private IntelGcl.ctl_power_telemetry_t _telemetry;

    public IntelDiscreteGpu(IntelGcl.ctl_device_adapter_handle_t handle, ISettings settings)
        : this(handle, settings, GetDeviceName(handle), GetDeviceId(handle), null)
    { }

    // A source-level test seam: no native initialization or implicit first poll.
    internal IntelDiscreteGpu(ISettings settings, IntelDiscreteGpuQueries queries)
        : this(default, settings, "Synthetic Intel GPU", "synthetic", queries ?? throw new ArgumentNullException(nameof(queries)))
    { }

    private IntelDiscreteGpu(IntelGcl.ctl_device_adapter_handle_t handle, ISettings settings, string name, string id, IntelDiscreteGpuQueries queries)
        : base(name, new Identifier("gpu-intel", id), settings)
    {
        _handle = handle;
        _queries = queries;
        IsValid = false;

        // Initialize device properties
        if (_queries == null && !InitializeDevice())
            return;
        if (_queries != null) { IsValid = true; _deviceId = id; }

        // Try to get D3D device identifier for memory monitoring
        if (_queries == null) _d3dDeviceId = GetD3DDeviceId();

        // Initialize temperature sensors
        _temperatureGpuCore = new Sensor("GPU Core", 0, SensorType.Temperature, this, settings);
        _temperatureMemory = new Sensor("GPU Memory", 1, SensorType.Temperature, this, settings);

        // Initialize clock sensors
        _clockCore = new Sensor("GPU Core", 0, SensorType.Clock, this, settings);
        _clockMemory = new Sensor("GPU Memory", 1, SensorType.Clock, this, settings);

        // Initialize voltage sensors
        _voltageCore = new Sensor("GPU Core", 0, SensorType.Voltage, this, settings);
        _voltageMemory = new Sensor("GPU Memory", 1, SensorType.Voltage, this, settings);

        // Initialize power sensors
        _powerGpu = new Sensor("GPU Package", 0, SensorType.Power, this, settings);
        _powerTotal = new Sensor("GPU Total", 1, SensorType.Power, this, settings);

        // Initialize utilization sensors
        _loadGlobalActivity = new Sensor("GPU Core", 0, SensorType.Load, this, settings);
        _loadRenderCompute = new Sensor("GPU Render/Compute", 1, SensorType.Load, this, settings);
        _loadMedia = new Sensor("GPU Media", 2, SensorType.Load, this, settings);

        // Initialize memory sensors
        _memoryFree = new Sensor("GPU Memory Free", 0, SensorType.SmallData, this, settings);
        _memoryUsed = new Sensor("GPU Memory Used", 1, SensorType.SmallData, this, settings);
        _memoryTotal = new Sensor("GPU Memory Total", 2, SensorType.SmallData, this, settings);
        _memoryLoad = new Sensor("GPU Memory", 3, SensorType.Load, this, settings);

        // Initialize bandwidth sensors
        _memoryBandwidthRead = new Sensor("GPU Memory Read", 0, SensorType.Throughput, this, settings);
        _memoryBandwidthWrite = new Sensor("GPU Memory Write", 1, SensorType.Throughput, this, settings);

        // Initialize fan sensors based on available fans
        int fanCount = _queries == null ? (int)GetFanCount() : Math.Max(0, Math.Min(16, _queries.FanCount));
        _fans = new Sensor[fanCount];

        for (int i = 0; i < fanCount; i++)
        {
            string fanName = fanCount == 1 ? "GPU Fan" : $"GPU Fan {i + 1}";
            _fans[i] = new Sensor(fanName, i, SensorType.Fan, this, settings);
        }

        if (_queries == null) Update();
    }

    public override string DeviceId => _deviceId ?? GetDeviceId(_handle);

    public uint DriverVersion { get; private set; }

    public override HardwareType HardwareType => HardwareType.GpuIntel;

    public bool IsValid { get; private set; }

    public uint RevisionId { get; private set; }

    public uint VendorId { get; private set; }

    private static bool TryGetDeviceProperties(IntelGcl.ctl_device_adapter_handle_t handle, out IntelGcl.ctl_device_adapter_properties_t properties)
    {
        properties = new IntelGcl.ctl_device_adapter_properties_t
        {
            Size = (uint)Marshal.SizeOf(typeof(IntelGcl.ctl_device_adapter_properties_t)),
            Version = 2
        };

        int result = IntelGcl.ctlGetDeviceProperties(handle, ref properties);
        return result == (int)IntelGcl.ctl_result_t.CTL_RESULT_SUCCESS &&
               properties.device_type == IntelGcl.ctl_device_type_t.CTL_DEVICE_TYPE_GRAPHICS;
    }

    private static string GetDeviceName(IntelGcl.ctl_device_adapter_handle_t handle)
    {
        if (TryGetDeviceProperties(handle, out IntelGcl.ctl_device_adapter_properties_t properties))
        {
            return properties.name;
        }

        return "Intel GPU";
    }

    private static string GetDeviceId(IntelGcl.ctl_device_adapter_handle_t handle)
    {
        if (TryGetDeviceProperties(handle, out IntelGcl.ctl_device_adapter_properties_t properties))
        {
            return $"0x{properties.pci_device_id:X4}";
        }

        return "0x0000";
    }

    // Device initialization
    private bool InitializeDevice()
    {
        if (TryGetDeviceProperties(_handle, out _properties))
        {
            _deviceId = $"0x{_properties.pci_device_id:X4}";
            VendorId = _properties.pci_vendor_id;
            RevisionId = _properties.rev_id;
            DriverVersion = (uint)_properties.driver_version;
            IsValid = true;
            return true;
        }

        return false;
    }

    private string GetD3DDeviceId() => SelectD3DDevice(D3DDisplayDevice.GetDeviceIdentifiers(),
        _properties.pci_vendor_id, _properties.pci_device_id, _properties.adapter_bdf,
        identifier => GetBdfFromDeviceIdentifier(identifier, out int bus, out int device, out int function)
            ? (bus, device, function) : null);

    internal static string SelectD3DDevice(string[] identifiers, uint vendorId, uint deviceId,
        IntelGcl.ctl_adapter_bdf_t expected, Func<string, (int Bus, int Device, int Function)?> readBdf)
    {
        if (identifiers == null || vendorId == 0 || deviceId == 0) return null;
        string prefix = $@"PCI\VEN_{vendorId:X4}&DEV_{deviceId:X4}&";
        string matched = null;
        foreach (string identifier in identifiers)
        {
            string actual = D3DDisplayDevice.GetActualDeviceIdentifier(identifier);
            if (actual == null || !actual.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var address = readBdf(identifier);
            if (!address.HasValue || address.Value.Bus != expected.bus || address.Value.Device != expected.device
                || address.Value.Function != expected.function) continue;
            // Do not guess if multiple adapter paths share a model and address.
            if (matched != null && !string.Equals(matched, identifier, StringComparison.OrdinalIgnoreCase)) return null;
            matched = identifier;
        }
        // Retain the matched identity even if its first telemetry query fails.
        // Subsequent polls retry this device, never another same-model adapter.
        return matched;
    }

    public override void Update()
    {
        // This class owns dynamic sensors; its memory capacity is queried each poll.
        // Counter baselines are separate and must be invalidated on source failures.
        foreach (ISensor sensor in Sensors)
            if (sensor is Sensor value) value.Value = null;
        _telemetry = default;
        if (!IsValid) { _counters.Reset(); return; }

        try
        {
            if (UpdateTelemetry())
            {
                var counters = _counters.Read(_telemetry);
                Set(_powerGpu, counters.PowerGpu); Set(_powerTotal, counters.PowerTotal);
                Set(_loadGlobalActivity, counters.Global); Set(_loadRenderCompute, counters.RenderCompute); Set(_loadMedia, counters.Media);
                UpdateSensorFromTelemetry(_telemetry.gpuCurrentTemperature, _temperatureGpuCore, IntelGcl.ctl_units_t.CTL_UNITS_TEMPERATURE_CELSIUS, false);
                UpdateSensorFromTelemetry(_telemetry.vramCurrentTemperature, _temperatureMemory, IntelGcl.ctl_units_t.CTL_UNITS_TEMPERATURE_CELSIUS, false);
                UpdateSensorFromTelemetry(_telemetry.gpuCurrentClockFrequency, _clockCore, IntelGcl.ctl_units_t.CTL_UNITS_FREQUENCY_MHZ);
                UpdateSensorFromTelemetry(_telemetry.gpuVoltage, _voltageCore, IntelGcl.ctl_units_t.CTL_UNITS_VOLTAGE_VOLTS);
                UpdateSensorFromTelemetry(_telemetry.vramVoltage, _voltageMemory, IntelGcl.ctl_units_t.CTL_UNITS_VOLTAGE_VOLTS);
                UpdateBandwidth(_telemetry.vramReadBandwidth, _memoryBandwidthRead);
                UpdateBandwidth(_telemetry.vramWriteBandwidth, _memoryBandwidthWrite);
            }
            else _counters.Reset();
        }
        catch (Exception ex)
        {
            _telemetry = default; _counters.Reset();
            foreach (Sensor sensor in new[] { _powerGpu, _powerTotal, _loadGlobalActivity, _loadRenderCompute, _loadMedia,
                _temperatureGpuCore, _temperatureMemory, _clockCore, _voltageCore, _voltageMemory, _memoryBandwidthRead, _memoryBandwidthWrite })
                sensor.Value = null;
            System.Diagnostics.Debug.WriteLine($"Error updating Intel GPU telemetry: {ex.Message}");
        }

        // Independent source failures cannot erase another source's valid readings.
        UpdateMemoryFrequency(_clockMemory);
        try { UpdateMemorySensors(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Error updating Intel GPU memory: {ex.Message}"); }
        try { UpdateFanSpeeds(_fans); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Error updating Intel GPU fans: {ex.Message}"); }
    }

    private bool UpdateTelemetry()
    {
        if (!IsValid)
            return false;

        if (_queries != null)
        {
            var result = _queries.Telemetry();
            if (!result.HasValue) return false;
            _telemetry = result.Value;
            return true;
        }

        var telemetry = new IntelGcl.ctl_power_telemetry_t
        {
            Size = (uint)Marshal.SizeOf(typeof(IntelGcl.ctl_power_telemetry_t)),
            Version = 1,
            psu = new IntelGcl.ctl_psu_info_t[IntelGcl.CTL_PSU_COUNT],
            fanSpeed = new IntelGcl.ctl_oc_telemetry_item_t[IntelGcl.CTL_FAN_COUNT]
        };

        if (IntelGcl.ctlPowerTelemetryGet(_handle, ref telemetry) == (int)IntelGcl.ctl_result_t.CTL_RESULT_SUCCESS)
        {
            _telemetry = telemetry;
            return true;
        }

        return false;
    }

    private void UpdateMemoryFrequency(Sensor sensor)
    {
        double frequency = double.NaN;
        try { frequency = _queries == null ? ReadMemoryFrequency() : _queries.MemoryFrequencyMhz() ?? double.NaN; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Error updating Intel GPU memory frequency: {ex.Message}"); }
        if (!IntelTelemetryCounters.Finite(frequency) || frequency < 0)
            frequency = IntelTelemetryCounters.TryRead(_telemetry.vramCurrentClockFrequency,
                IntelGcl.ctl_units_t.CTL_UNITS_FREQUENCY_MHZ, out double fallback) && fallback >= 0 ? fallback : double.NaN;
        Set(sensor, IntelTelemetryCounters.AsFloat(frequency));
    }

    private double ReadMemoryFrequency()
    {
        double frequency = double.NaN;

        uint freqCount = 0;
        int result = IntelGcl.ctlEnumFrequencyDomains(_handle, ref freqCount, null);

        if (result == (int)IntelGcl.ctl_result_t.CTL_RESULT_SUCCESS && freqCount > 0)
        {
            var freqHandles = new IntelGcl.ctl_freq_handle_t[freqCount];
            result = IntelGcl.ctlEnumFrequencyDomains(_handle, ref freqCount, freqHandles);

            if (result == (int)IntelGcl.ctl_result_t.CTL_RESULT_SUCCESS)
            {
                for (int i = 0; i < freqCount; i++)
                {
                    var properties = new IntelGcl.ctl_freq_properties_t
                    {
                        Size = (uint)Marshal.SizeOf(typeof(IntelGcl.ctl_freq_properties_t)),
                        Version = 0
                    };

                    result = IntelGcl.ctlFrequencyGetProperties(freqHandles[i], ref properties);

                    if (result == (int)IntelGcl.ctl_result_t.CTL_RESULT_SUCCESS &&
                        properties.type == IntelGcl.ctl_freq_domain_t.CTL_FREQ_DOMAIN_MEMORY)
                    {
                        var state = new IntelGcl.ctl_freq_state_t
                        {
                            Size = (uint)Marshal.SizeOf(typeof(IntelGcl.ctl_freq_state_t)),
                            Version = 0
                        };

                        result = IntelGcl.ctlFrequencyGetState(freqHandles[i], ref state);

                        if (result == (int)IntelGcl.ctl_result_t.CTL_RESULT_SUCCESS && state.actual >= 0)
                        {
                            frequency = state.actual / MemoryFrequencyDivisor;
                            break;
                        }
                    }
                }
            }
        }

        return frequency;
    }

    private uint GetFanCount()
    {
        uint fanCount = 0;
        int result = IntelGcl.ctlEnumFans(_handle, ref fanCount, null);

        if (result == (int)IntelGcl.ctl_result_t.CTL_RESULT_SUCCESS)
        {
            return fanCount;
        }

        return 0;
    }

    private void UpdateFanSpeeds(Sensor[] fanSensors)
    {
        if (_queries != null)
        {
            var readings = _queries.FanSpeeds() ?? Array.Empty<float?>();
            for (int i = 0; i < fanSensors.Length; i++)
                Set(fanSensors[i], i < readings.Length && readings[i].HasValue && readings[i].Value >= 0
                    ? IntelTelemetryCounters.AsFloat(readings[i].Value) : null);
            return;
        }
        uint fanCount = (uint)Math.Min(Math.Max(0, GetFanCount()), fanSensors.Length);
        if (fanCount == 0)
            return;

        var fanHandles = new IntelGcl.ctl_fan_handle_t[fanCount];
        int result = IntelGcl.ctlEnumFans(_handle, ref fanCount, fanHandles);

        if (result == (int)IntelGcl.ctl_result_t.CTL_RESULT_SUCCESS)
        {
            for (int i = 0; i < fanCount; i++)
            {
                int fanSpeed = -1;
                result = IntelGcl.ctlFanGetState(fanHandles[i], IntelGcl.ctl_fan_speed_units_t.CTL_FAN_SPEED_UNITS_RPM, ref fanSpeed);

                if (result == (int)IntelGcl.ctl_result_t.CTL_RESULT_SUCCESS && fanSpeed >= 0)
                {
                    fanSensors[i].Value = fanSpeed;
                    ActivateSensor(fanSensors[i]);
                }
                else
                {
                    fanSensors[i].Value = null;
                }
            }

            for (int i = (int)fanCount; i < fanSensors.Length; i++)
            {
                fanSensors[i].Value = null;
            }
        }
    }

    private void Set(Sensor sensor, float? value)
    {
        sensor.Value = value;
        if (value.HasValue) ActivateSensor(sensor);
    }

    private void UpdateSensorFromTelemetry(IntelGcl.ctl_oc_telemetry_item_t item, Sensor sensor,
        IntelGcl.ctl_units_t unit, bool nonnegative = true)
    {
        Set(sensor, IntelTelemetryCounters.TryRead(item, unit, out double value) && (!nonnegative || value >= 0)
            ? IntelTelemetryCounters.AsFloat(value) : null);
    }

    private void UpdateMemorySensors()
    {
        D3DDisplayDevice.D3DDeviceInfo info;
        if (_queries != null)
        {
            var result = _queries.Memory();
            if (!result.HasValue) return;
            info = result.Value;
        }
        else
        {
            // Never substitute another Intel adapter after this adapter's query fails.
            if (string.IsNullOrEmpty(_d3dDeviceId)) _d3dDeviceId = GetD3DDeviceId();
            if (string.IsNullOrEmpty(_d3dDeviceId) || !D3DDisplayDevice.GetDeviceInfoByIdentifier(_d3dDeviceId, out info)) return;
        }

        ulong total = info.GpuVideoMemoryLimit, used = info.GpuDedicatedUsed;
        if (total == 0 || used > total) return;
        Set(_memoryTotal, total / (1024.0f * 1024.0f));
        Set(_memoryUsed, used / (1024.0f * 1024.0f));
        Set(_memoryFree, (total - used) / (1024.0f * 1024.0f));
        Set(_memoryLoad, (float)((double)used / total * 100.0));
    }

    private void UpdateBandwidth(IntelGcl.ctl_oc_telemetry_item_t item, Sensor sensor)
    {
        double scale = item.units == IntelGcl.ctl_units_t.CTL_UNITS_BANDWIDTH_MBPS ? 1_000_000d
            : item.units == IntelGcl.ctl_units_t.CTL_UNITS_MEM_SPEED_GBPS ? 1_000_000_000d : double.NaN;
        Set(sensor, IntelTelemetryCounters.Finite(scale) && IntelTelemetryCounters.TryRead(item, item.units, out double value) && value >= 0
            ? IntelTelemetryCounters.AsFloat(value * scale) : null);
    }

    internal static unsafe bool GetBdfFromDeviceIdentifier(string deviceIdentifier, out int bus, out int device, out int function)
    {
        bus = -1;
        device = -1;
        function = -1;

        if (string.IsNullOrEmpty(deviceIdentifier))
            return false;

        // deviceIdentifier is like "\\?\PCI#VEN_8086&DEV_56A0&SUBSYS_10208086&REV_08#4&3834663c&0&0008#{5b45201d-f2f2-4f3b-85bb-30ff1f953599}"
        // We need to extract the instance ID from this.
        // The instance ID is "PCI\VEN_8086&DEV_56A0&SUBSYS_10208086&REV_08\4&3834663c&0&0008"

        // Extract the device instance path from the interface path
        // Remove "\\?\" prefix and the GUID suffix
        string instanceId = deviceIdentifier;
        if (instanceId.StartsWith(@"\\?\"))
            instanceId = instanceId.Substring(4);

        int lastHash = instanceId.LastIndexOf('#');
        if (lastHash == -1)
            return false;

        instanceId = instanceId.Substring(0, lastHash);
        // Replace '#' with '\' to match the Instance ID format expected by CM
        instanceId = instanceId.Replace('#', '\\');

        uint devInst;
        fixed (char* pInstanceId = instanceId)
        {
            if (PInvoke.CM_Locate_DevNode(out devInst, (PWSTR)pInstanceId, CM_LOCATE_DEVNODE_FLAGS.CM_LOCATE_DEVNODE_NORMAL) != CONFIGRET.CR_SUCCESS)
            {
                return false;
            }
        }

        DEVPROPTYPE propertyType;
        uint bufferSize = sizeof(uint);
        uint busNum = 0;
        uint address = 0;

        fixed (DEVPROPKEY* pBusKey = &PInvoke.DEVPKEY_Device_BusNumber)
        {
            if (PInvoke.CM_Get_DevNode_Property(devInst, pBusKey, &propertyType, (byte*)&busNum, &bufferSize, 0) == CONFIGRET.CR_SUCCESS && propertyType == DEVPROPTYPE.DEVPROP_TYPE_UINT32)
            {
                bus = (int)busNum;
            }
            else
            {
                return false;
            }
        }

        bufferSize = sizeof(uint);
        propertyType = default;
        fixed (DEVPROPKEY* pAddrKey = &PInvoke.DEVPKEY_Device_Address)
        {
            if (PInvoke.CM_Get_DevNode_Property(devInst, pAddrKey, &propertyType, (byte*)&address, &bufferSize, 0) == CONFIGRET.CR_SUCCESS &&
                propertyType == DEVPROPTYPE.DEVPROP_TYPE_UINT32)
            {
                // Address contains device and function per Windows DEVPKEY_Device_Address spec
                // Bits 16-31: Device number
                // Bits 0-15: Function number
                device = (int)(address >> 16) & 0xFFFF;
                function = (int)address & 0xFFFF;
                return true;
            }
        }

        return false;
    }
}
