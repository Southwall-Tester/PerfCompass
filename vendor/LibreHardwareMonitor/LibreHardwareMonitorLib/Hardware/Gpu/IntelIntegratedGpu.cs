using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Interop;
using LibreHardwareMonitor.PawnIo;
using Microsoft.Win32;

namespace LibreHardwareMonitor.Hardware.Gpu;

internal class IntelIntegratedGpu : GenericGpu
{
    private const uint MSR_PP1_ENERGY_STATUS = 0x641;

    private readonly Sensor _dedicatedMemoryUsage;
    private readonly Sensor _sharedMemoryLimit;
    private readonly Sensor _sharedMemoryFree;
    private readonly string _deviceId;
    private readonly float _energyUnitMultiplier;
    private readonly D3DNodeSensors _nodeSensors;
    private readonly Sensor _powerSensor;
    private readonly Sensor _sharedMemoryUsage;
    private readonly Sensor _gtCoresTemperature;
    private readonly Sensor _gpuClockFrequency;
    private readonly Sensor _gpuVoltage;

    private uint _lastEnergyConsumed;
    private long _lastEnergyTimestamp;
    private bool _energyBaselineValid;

    private readonly IntelMsr _pawnModule;

    private readonly IntelGcl.ctl_device_adapter_handle_t? _igclHandle;

    public IntelIntegratedGpu(Cpu.IntelCpu intelCpu, string deviceId, D3DDisplayDevice.D3DDeviceInfo deviceInfo, ISettings settings)
        : base(GetName(deviceId),
               new Identifier("gpu-intel-integrated", deviceId.ToString(CultureInfo.InvariantCulture)),
               settings)
    {
        _pawnModule = new IntelMsr();
        _deviceId = deviceId;

        _igclHandle = FindIgclHandle();

        if (_igclHandle.HasValue && TryReadTelemetry(_igclHandle.Value, out IntelGcl.ctl_power_telemetry_t probeTelemetry))
        {
            if (probeTelemetry.gpuCurrentTemperature.bSupported)
            {
                _gtCoresTemperature = new Sensor("GPU Core", 0, SensorType.Temperature, this, settings);
                ActivateSensor(_gtCoresTemperature);
            }

            if (probeTelemetry.gpuCurrentClockFrequency.bSupported)
            {
                _gpuClockFrequency = new Sensor("GPU Core", 0, SensorType.Clock, this, settings);
                ActivateSensor(_gpuClockFrequency);
            }

            if (probeTelemetry.gpuVoltage.bSupported)
            {
                _gpuVoltage = new Sensor("GPU Core", 0, SensorType.Voltage, this, settings);
                ActivateSensor(_gpuVoltage);
            }
        }

        int memorySensorIndex = 0;

        if (deviceInfo.GpuDedicatedLimit > 0)
        {
            _dedicatedMemoryUsage = new Sensor("D3D Dedicated Memory Used", memorySensorIndex++, SensorType.SmallData, this, settings);
        }

        _sharedMemoryUsage = new Sensor("D3D Shared Memory Used", memorySensorIndex++, SensorType.SmallData, this, settings);

        if (deviceInfo.GpuSharedLimit > 0)
        {
            _sharedMemoryFree = new Sensor("D3D Shared Memory Free", memorySensorIndex++, SensorType.SmallData, this, settings);
            _sharedMemoryLimit = new Sensor("D3D Shared Memory Total", memorySensorIndex++, SensorType.SmallData, this, settings);
        }

        if (_pawnModule.ReadMsr(MSR_PP1_ENERGY_STATUS, out uint eax, out uint _))
        {
            _energyUnitMultiplier = intelCpu.EnergyUnitsMultiplier;
            if (_energyUnitMultiplier != 0)
            {
                _lastEnergyTimestamp = Stopwatch.GetTimestamp();
                _energyBaselineValid = true;
                _lastEnergyConsumed = eax;
                _powerSensor = new Sensor("GPU Power", 0, SensorType.Power, this, settings);
                ActivateSensor(_powerSensor);
            }
        }

        _nodeSensors = new D3DNodeSensors(this, settings, 0, ActivateSensor, DeactivateSensor);
    }

    /// <inheritdoc />
    public override string DeviceId => D3DDisplayDevice.GetActualDeviceIdentifier(_deviceId);

    public override HardwareType HardwareType => HardwareType.GpuIntel;

    public override void Update()
    {
        try
        {
            // A successful Update call may contain failed native subqueries. Never
            // retain the previous poll's values behind that successful method return.
            ClearCurrentTelemetryValues();
            // Update IGCL telemetry (temperature, clock, voltage).
            if (_igclHandle.HasValue && (_gtCoresTemperature != null || _gpuClockFrequency != null || _gpuVoltage != null))
            {
                if (TryReadTelemetry(_igclHandle.Value, out IntelGcl.ctl_power_telemetry_t telemetry))
                {
                    UpdateSensorFromTelemetry(telemetry.gpuCurrentTemperature, _gtCoresTemperature);
                    UpdateSensorFromTelemetry(telemetry.gpuCurrentClockFrequency, _gpuClockFrequency);
                    UpdateSensorFromTelemetry(telemetry.gpuVoltage, _gpuVoltage);
                }
                else
                {
                    if (_gtCoresTemperature != null) _gtCoresTemperature.Value = null;
                    if (_gpuClockFrequency != null) _gpuClockFrequency.Value = null;
                    if (_gpuVoltage != null) _gpuVoltage.Value = null;
                }
            }

            if (D3DDisplayDevice.GetDeviceInfoByIdentifier(_deviceId, out D3DDisplayDevice.D3DDeviceInfo deviceInfo))
            {
                if (_dedicatedMemoryUsage != null)
                {
                    _dedicatedMemoryUsage.Value = 1f * deviceInfo.GpuDedicatedUsed / 1024 / 1024;
                    ActivateSensor(_dedicatedMemoryUsage);
                }

                _sharedMemoryUsage.Value = 1f * deviceInfo.GpuSharedUsed / 1024 / 1024;
                ActivateSensor(_sharedMemoryUsage);

                if (_sharedMemoryLimit != null)
                {
                    _sharedMemoryLimit.Value = 1f * deviceInfo.GpuSharedLimit / 1024 / 1024;
                    ActivateSensor(_sharedMemoryLimit);
                    if (_sharedMemoryUsage != null)
                    {
                        _sharedMemoryFree.Value = _sharedMemoryLimit.Value >= _sharedMemoryUsage.Value
                            ? _sharedMemoryLimit.Value - _sharedMemoryUsage.Value : null;
                        ActivateSensor(_sharedMemoryFree);
                    }
                }

                _nodeSensors.Update(deviceInfo);
            }
            else _nodeSensors.Invalidate();
            UpdateEnergy();
        }
        catch
        {
            _nodeSensors.Invalidate(); _energyBaselineValid = false;
            ClearCurrentTelemetryValues();
            throw;
        }
    }

    private void ClearCurrentTelemetryValues()
    {
        foreach (Sensor sensor in Sensors.OfType<Sensor>()) sensor.Value = null;
    }

    private void UpdateEnergy()
    {
        if (_powerSensor == null) return;
        _powerSensor.Value = null;
        if (!_pawnModule.ReadMsr(MSR_PP1_ENERGY_STATUS, out uint energy, out uint _))
        { _energyBaselineValid = false; return; }
        long timestamp = Stopwatch.GetTimestamp();
        if (_energyBaselineValid)
        {
            double elapsed = (timestamp - _lastEnergyTimestamp) / (double)Stopwatch.Frequency;
            if (elapsed is >= .01 and <= D3DNodeSampler.MaximumIntervalSeconds)
            {
                // RAPL's existing 32-bit modular energy-counter semantics are
                // retained; only the elapsed-time and failed-read handling change.
                double power = _energyUnitMultiplier * (double)unchecked(energy - _lastEnergyConsumed) / elapsed;
                if (double.IsFinite(power) && power >= 0 && power <= float.MaxValue) _powerSensor.Value = (float)power;
            }
            else { _energyBaselineValid = false; return; }
        }
        _lastEnergyTimestamp = timestamp; _lastEnergyConsumed = energy; _energyBaselineValid = true;
    }

    /// <inheritdoc />
    public override void Close()
    {
        base.Close();
        _pawnModule.Close();
    }

    private static bool TryReadTelemetry(IntelGcl.ctl_device_adapter_handle_t handle, out IntelGcl.ctl_power_telemetry_t telemetry)
    {
        telemetry = new IntelGcl.ctl_power_telemetry_t
        {
            Size = (uint)Marshal.SizeOf<IntelGcl.ctl_power_telemetry_t>(),
            Version = 1,
            psu = new IntelGcl.ctl_psu_info_t[IntelGcl.CTL_PSU_COUNT],
            fanSpeed = new IntelGcl.ctl_oc_telemetry_item_t[IntelGcl.CTL_FAN_COUNT]
        };

        int result = IntelGcl.ctlPowerTelemetryGet(handle, ref telemetry);
        return result == (int)IntelGcl.ctl_result_t.CTL_RESULT_SUCCESS;
    }

    private static void UpdateSensorFromTelemetry(IntelGcl.ctl_oc_telemetry_item_t telemetryItem, Sensor sensor)
    {
        if (sensor == null)
            return;

        if (telemetryItem.bSupported)
        {
            double value = GetTelemetryValue(telemetryItem);
            sensor.Value = double.IsFinite(value) && Math.Abs(value) <= float.MaxValue ? (float)value : null;
        }
        else
        {
            sensor.Value = null;
        }
    }

    private static double GetTelemetryValue(IntelGcl.ctl_oc_telemetry_item_t item)
    {
        return item.type switch
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
    }

    private IntelGcl.ctl_device_adapter_handle_t? FindIgclHandle()
    {
        if (!IntelGcl.IsInitialized)
            return null;

        IntelGcl.ctl_device_adapter_handle_t[] handles = IntelGcl.GetDeviceHandles();

        IntelGcl.ctl_device_adapter_handle_t? matched = null;
        foreach (IntelGcl.ctl_device_adapter_handle_t handle in handles)
        {
            var properties = new IntelGcl.ctl_device_adapter_properties_t
            {
                Size = (uint)Marshal.SizeOf<IntelGcl.ctl_device_adapter_properties_t>(),
                Version = 2
            };

            int result = IntelGcl.ctlGetDeviceProperties(handle, ref properties);

            if (result != (int)IntelGcl.ctl_result_t.CTL_RESULT_SUCCESS)
                continue;

            if (properties.device_type != IntelGcl.ctl_device_type_t.CTL_DEVICE_TYPE_GRAPHICS)
                continue;

            // Use the IGCL integrated-adapter flag — the same method IntelGpuGroup uses
            // to filter iGPUs out of the discrete GPU enumeration path.
            if ((properties.graphics_adapter_properties & (uint)IntelGcl.ctl_adapter_properties_flag_t.CTL_ADAPTER_PROPERTIES_FLAG_INTEGRATED) != 0)
            {
                string identity = IntelDiscreteGpu.SelectD3DDevice(new[] { _deviceId }, properties.pci_vendor_id,
                    properties.pci_device_id, properties.adapter_bdf,
                    identifier => IntelDiscreteGpu.GetBdfFromDeviceIdentifier(identifier, out int bus, out int device, out int function)
                        ? (bus, device, function) : null);
                if (identity == null) continue;
                if (matched.HasValue) return null;
                matched = handle;
            }
        }

        return matched;
    }

    private static string GetName(string deviceId)
    {
        string path = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Enum\" + D3DDisplayDevice.GetActualDeviceIdentifier(deviceId);

        if (Registry.GetValue(path, "DeviceDesc", null) is string deviceDesc)
        {
            return deviceDesc.Split(';').Last();
        }

        return "Intel Integrated Graphics";
    }
}
