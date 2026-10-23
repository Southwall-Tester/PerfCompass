// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// PerfCompass changes: per-poll dynamic values and provider-snapshot provenance.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreHardwareMonitor.Interop;

namespace LibreHardwareMonitor.Hardware.Gpu;

internal static class GpuVendorPoll
{
    internal readonly record struct NvidiaIdentity(uint Bus, uint DeviceId, uint SubsystemId);
    internal readonly record struct PciAddress(int Bus, int Device, int Function);
    internal sealed record NvidiaD3DMatch(string Identifier, NvidiaIdentity Identity, PciAddress Address);

    internal static float? LoadPercent(float value) => float.IsFinite(value) && value is >= 0 and <= 100 ? value : null;

    // NvAPI's ExtDeviceId is the external PCI device identifier. Do not infer a
    // vendor/device bit layout from its separately documented internal DeviceId.
    // https://docs.nvidia.com/nvapi/group__gpu.html (GetPCIIdentifiers/GetBusId)
    internal static NvidiaD3DMatch SelectNvidiaD3D(NvidiaIdentity target, IReadOnlyList<NvidiaIdentity?> physical,
        string[] identifiers, Func<string, PciAddress?> readAddress)
    {
        static bool Valid(NvidiaIdentity identity) => identity.Bus <= 255 && identity.DeviceId is > 0 and <= 65535;
        if (!Valid(target) || physical == null || physical.Count == 0 || physical.Any(p => !p.HasValue || !Valid(p.Value))
            || physical.Count(p => p.Value == target) != 1 || identifiers == null) return null;
        var candidates = new List<NvidiaD3DMatch>();
        foreach (string identifier in identifiers.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string actual = D3DDisplayDevice.GetActualDeviceIdentifier(identifier);
            if (actual == null || !actual.StartsWith(@"PCI\VEN_10DE&", StringComparison.OrdinalIgnoreCase)) continue;
            string[] path = actual.Split('\\');
            if (path.Length != 3) return null;
            string[] fields = path[1].Split('&');
            var devices = fields.Where(p => p.StartsWith("DEV_", StringComparison.OrdinalIgnoreCase)).ToArray();
            var subsystems = fields.Where(p => p.StartsWith("SUBSYS_", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (devices.Length != 1 || subsystems.Length != 1) return null;
            string device = devices[0], subsystem = subsystems[0];
            if (device.Length != 8 || subsystem.Length != 15
                || !uint.TryParse(device.Substring(4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint model)
                || !uint.TryParse(subsystem.Substring(7), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint board)) return null;
            PciAddress? address = readAddress(identifier);
            // An unreadable candidate could be the target or an address collision.
            if (!address.HasValue || address.Value.Bus is < 0 or > 255 || address.Value.Device is < 0 or > 31
                || address.Value.Function is < 0 or > 7) return null;
            candidates.Add(new(identifier, new((uint)address.Value.Bus, model, board), address.Value));
        }
        var matches = candidates.Where(c => c.Identity == target).ToArray();
        if (matches.Length != 1) return null;
        var match = matches[0];
        // Windows BDF lacks PCI domain. Multiple physical identities or interface
        // paths with the same reduced identity/address are ambiguous, never first-win.
        return candidates.Count(c => c.Address == match.Address) == 1 ? match : null;
    }

    internal static NvidiaML.NvmlDevice? ResolveNvml(NvidiaD3DMatch match, Func<bool> initialize,
        Func<string, NvidiaML.NvmlDevice?> lookup, Func<NvidiaML.NvmlDevice, NvidiaML.NvmlPciInfo?> readPci)
    {
        if (match == null) return null;
        try
        {
            if (!initialize()) return null;
            var address = match.Address;
            var candidate = lookup($"0000:{address.Bus:X2}:{address.Device:X2}.{address.Function:X}");
            return candidate.HasValue && readPci(candidate.Value) is { } pci && MatchesNvml(match, pci) ? candidate : null;
        }
        catch (Exception) { return null; }
    }

    internal static bool MatchesNvml(NvidiaD3DMatch match, NvidiaML.NvmlPciInfo pci)
    {
        if (match == null || pci.domain != 0 || pci.bus != match.Address.Bus || pci.device != match.Address.Device
            || pci.pciVendorId != 0x10DE || pci.pciDeviceId != match.Identity.DeviceId || pci.pciSubSystemId != match.Identity.SubsystemId
            || string.IsNullOrEmpty(pci.busId)) return false;
        string[] parts = pci.busId.Split(':');
        if (parts.Length != 3) return false;
        string[] slot = parts[2].Split('.');
        return slot.Length == 2
            && uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint domain) && domain == pci.domain
            && uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint bus) && bus == pci.bus
            && uint.TryParse(slot[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint device) && device == pci.device
            && uint.TryParse(slot[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint function) && function == match.Address.Function;
    }

    internal static void Clear(params Sensor[] sensors)
    {
        if (sensors == null) return;
        foreach (Sensor sensor in sensors) if (sensor != null) sensor.Value = null;
    }

    internal static void WriteIndexed(Sensor[] sensors, int index, float? value)
    {
        if (sensors == null) return;
        foreach (Sensor sensor in sensors)
            if (sensor != null && sensor.Index == index) sensor.Value = value;
    }

    internal static bool SameDevice(string first, string second) => !string.IsNullOrWhiteSpace(first)
        && !string.IsNullOrWhiteSpace(second) && string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);

    internal static bool MatchesPciLocation(string location, uint domain, uint bus, uint device, string busId)
    {
        // A bus number alone can identify several devices. The registry location
        // does not include a PCI segment/domain, so do not guess nonzero domains.
        if (domain != 0 || string.IsNullOrEmpty(location) || string.IsNullOrEmpty(busId)) return false;
        int open = location.LastIndexOf('('), close = location.LastIndexOf(')'), dot = busId.LastIndexOf('.');
        if (open < 0 || close <= open || dot < 0 || dot == busId.Length - 1) return false;
        string[] parts = location.Substring(open + 1, close - open - 1).Split(',');
        return parts.Length == 3 && uint.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint foundBus)
            && uint.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint foundDevice)
            && uint.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint foundFunction)
            && uint.TryParse(busId.Substring(dot + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint expectedFunction)
            && foundBus == bus && foundDevice == device && foundFunction == expectedFunction;
    }

    internal static bool TryReadPmLog(uint[] pairs, uint sensorId, out float value)
    {
        value = 0;
        if (pairs == null || pairs.Length < 2 || pairs.Length > 512 || pairs.Length % 2 != 0 || sensorId == 0) return false;
        bool found = false;
        for (int i = 0; i < pairs.Length; i += 2)
        {
            if (pairs[i] == 0) break;
            if (pairs[i] != sensorId) continue;
            if (found) return false;
            found = true; value = pairs[i + 1];
        }
        return found;
    }
}

internal sealed class GpuProviderTimestamp
{
    private ulong? previous;
    internal void Reset() => previous = null;
    internal bool Observe(ulong timestamp, uint sampleRateMilliseconds)
    {
        // ADL documents a timestamp but not its epoch or unit. Require observed
        // forward progress, never compare it with FILETIME or the sensor value.
        if (timestamp == 0 || sampleRateMilliseconds == 0 || sampleRateMilliseconds > 86400000)
        { Reset(); return false; }
        bool advanced = previous.HasValue && timestamp > previous.Value;
        previous = timestamp;
        return advanced;
    }
}
