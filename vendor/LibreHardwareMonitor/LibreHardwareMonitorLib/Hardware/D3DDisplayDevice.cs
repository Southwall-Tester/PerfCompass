// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// Copyright (C) LibreHardwareMonitor and Contributors.
// All Rights Reserved.
// Ported from: https://github.com/processhacker/processhacker/blob/master/plugins/ExtendedTools/gpumon.c

using System;
using System.Diagnostics;
using System.Linq;
using Windows.Wdk.Graphics.Direct3D;
using Windows.Win32.Devices.DeviceAndDriverInstallation;
using Windows.Win32.Foundation;
using PInvoke = Windows.Win32.PInvoke;

namespace LibreHardwareMonitor.Hardware;

internal static class D3DDisplayDevice
{
    // Explicit managed seam for query-lifetime tests. Production uses the native
    // backend; a test override never changes the driver or hardware configuration.
    internal static volatile ID3DQueryBackend QueryBackendOverride;

    internal readonly record struct D3DAdapter(uint Handle, long Luid);
    internal readonly record struct D3DMemoryLimits(ulong Shared, ulong Video, ulong Dedicated);
    internal readonly record struct D3DSegment(ulong Resident, ulong Committed, bool Shared);

    internal interface ID3DQueryBackend
    {
        bool Open(string identifier, out D3DAdapter adapter);
        bool AdapterType(D3DAdapter adapter, out bool software, out bool integrated);
        bool Counts(D3DAdapter adapter, out uint nodes, out uint segments);
        bool NodeName(D3DAdapter adapter, uint id, out string name);
        bool NodeCounter(D3DAdapter adapter, uint id, out long runningTime);
        long Timestamp();
        bool MemoryLimits(D3DAdapter adapter, out D3DMemoryLimits limits);
        bool Segment(D3DAdapter adapter, uint id, out D3DSegment segment);
        bool Close(D3DAdapter adapter);
    }
    public static unsafe string[] GetDeviceIdentifiers()
    {
        if (PInvoke.CM_Get_Device_Interface_List_Size(out uint size, PInvoke.GUID_DISPLAY_DEVICE_ARRIVAL, null, CM_GET_DEVICE_INTERFACE_LIST_FLAGS.CM_GET_DEVICE_INTERFACE_LIST_PRESENT) !=
            CONFIGRET.CR_SUCCESS)
            return null;

        char[] data = new char[size];
        fixed (char* pData = data)
        {
            if (PInvoke.CM_Get_Device_Interface_List(PInvoke.GUID_DISPLAY_DEVICE_ARRIVAL, null, pData, (uint)data.Length, CM_GET_DEVICE_INTERFACE_LIST_FLAGS.CM_GET_DEVICE_INTERFACE_LIST_PRESENT) ==
                CONFIGRET.CR_SUCCESS)
            {
                return new string(data).Split('\0').Where(m => !string.IsNullOrEmpty(m)).ToArray();
            }
        }

        return null;
    }

    public static string GetActualDeviceIdentifier(string deviceIdentifier)
    {
        if (string.IsNullOrEmpty(deviceIdentifier))
            return deviceIdentifier;

        // For example:
        // \\?\ROOT#BasicRender#0000#{1ca05180-a699-450a-9a0c-de4fbe3ddd89}  -->  ROOT\BasicRender\0000
        // \\?\PCI#VEN_1002&DEV_731F&SUBSYS_57051682&REV_C4#6&e539058&0&00000019#{1ca05180-a699-450a-9a0c-de4fbe3ddd89}  -->  PCI\VEN_1002&DEV_731F&SUBSYS_57051682&REV_C4\6&e539058&0&00000019

        if (deviceIdentifier.StartsWith(@"\\?\"))
            deviceIdentifier = deviceIdentifier.Substring(4);

        if (deviceIdentifier.Length > 0 && deviceIdentifier[deviceIdentifier.Length - 1] == '}')
        {
            int lastIndex = deviceIdentifier.LastIndexOf('{');
            if (lastIndex > 0)
                deviceIdentifier = deviceIdentifier.Substring(0, lastIndex - 1);
        }

        return deviceIdentifier.Replace('#', '\\');
    }

    public static bool GetDeviceInfoByIdentifier(string deviceIdentifier, out D3DDeviceInfo deviceInfo)
        => GetDeviceInfoByIdentifier(deviceIdentifier, out deviceInfo, QueryBackendOverride ?? NativeQueryBackend.Instance);

    internal static bool GetDeviceInfoByIdentifier(string deviceIdentifier, out D3DDeviceInfo deviceInfo, ID3DQueryBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        deviceInfo = default;
        if (!backend.Open(deviceIdentifier, out D3DAdapter adapter)) return false;
        bool readSucceeded = false, closeSucceeded = false;
        D3DDeviceInfo candidate = default;
        try
        {
            readSucceeded = ReadOpenedAdapter(adapter, backend, out candidate);
        }
        finally
        {
            // Every successful open owns exactly one close attempt, including a
            // failed intermediate query or managed exception. A failed close
            // cannot publish the otherwise completed frame as successful.
            try { closeSucceeded = backend.Close(adapter); }
            catch (Exception) { closeSucceeded = false; }
        }
        if (!readSucceeded || !closeSucceeded) return false;
        deviceInfo = candidate;
        return true;
    }

    private static bool ReadOpenedAdapter(D3DAdapter adapter, ID3DQueryBackend backend, out D3DDeviceInfo result)
    {
        result = default;
        if (!backend.AdapterType(adapter, out bool software, out bool integrated) || software) return false;
        if (!backend.Counts(adapter, out uint nodeCount, out uint segmentCount)
            || nodeCount > D3DNodeSampler.MaximumNodes || segmentCount > 4096) return false;
        var candidate = new D3DDeviceInfo { AdapterLuid = adapter.Luid, Integrated = integrated, Nodes = new D3DDeviceNodeInfo[nodeCount] };
        for (uint nodeId = 0; nodeId < nodeCount; nodeId++)
        {
            if (!backend.NodeName(adapter, nodeId, out string name) || string.IsNullOrWhiteSpace(name)
                || !backend.NodeCounter(adapter, nodeId, out long runningTime)) return false;
            candidate.Nodes[nodeId] = new D3DDeviceNodeInfo
            {
                Id = nodeId, Name = name, RunningTime = runningTime, QueryTimestamp = backend.Timestamp()
            };
        }
        if (!backend.MemoryLimits(adapter, out D3DMemoryLimits limits)) return false;
        candidate.GpuSharedLimit = limits.Shared;
        candidate.GpuVideoMemoryLimit = limits.Video;
        candidate.GpuDedicatedLimit = limits.Dedicated;
        for (uint segmentId = 0; segmentId < segmentCount; segmentId++)
        {
            if (!backend.Segment(adapter, segmentId, out D3DSegment segment)) return false;
            if (segment.Shared)
            {
                candidate.GpuSharedUsed = checked(candidate.GpuSharedUsed + segment.Resident);
                candidate.GpuSharedMax = checked(candidate.GpuSharedMax + segment.Committed);
            }
            else
            {
                candidate.GpuDedicatedUsed = checked(candidate.GpuDedicatedUsed + segment.Resident);
                candidate.GpuDedicatedMax = checked(candidate.GpuDedicatedMax + segment.Committed);
            }
        }
        result = candidate;
        return true;
    }

    private sealed class NativeQueryBackend : ID3DQueryBackend
    {
        internal static readonly NativeQueryBackend Instance = new();
        private static D3DKMT_OPENADAPTERFROMDEVICENAME Native(D3DAdapter adapter) => new()
        {
            hAdapter = adapter.Handle,
            AdapterLuid = new LUID { LowPart = unchecked((uint)adapter.Luid), HighPart = unchecked((int)(adapter.Luid >> 32)) }
        };
        public bool Open(string identifier, out D3DAdapter adapter)
        {
            OpenAdapterFromDeviceName(out NTSTATUS status, identifier, out var native);
            adapter = new(native.hAdapter, ((long)native.AdapterLuid.HighPart << 32) | native.AdapterLuid.LowPart);
            return status == NTSTATUS.STATUS_SUCCESS;
        }
        public bool AdapterType(D3DAdapter adapter, out bool software, out bool integrated)
        {
            GetAdapterType(out NTSTATUS status, Native(adapter), out var type);
            software = type.Anonymous.Anonymous.SoftwareDevice; integrated = type.Anonymous.Anonymous.HybridIntegrated;
            return status == NTSTATUS.STATUS_SUCCESS;
        }
        public bool Counts(D3DAdapter adapter, out uint nodes, out uint segments)
        {
            GetQueryStatisticsAdapterInformation(out NTSTATUS status, Native(adapter), out var info);
            nodes = info.NodeCount; segments = info.NbSegments;
            return status == NTSTATUS.STATUS_SUCCESS;
        }
        public bool NodeName(D3DAdapter adapter, uint id, out string name)
        {
            GetNodeMetaData(out NTSTATUS status, Native(adapter), id, out var metadata);
            name = status == NTSTATUS.STATUS_SUCCESS ? GetNodeEngineTypeString(metadata) : null;
            return status == NTSTATUS.STATUS_SUCCESS;
        }
        public bool NodeCounter(D3DAdapter adapter, uint id, out long runningTime)
        {
            GetQueryStatisticsNode(out NTSTATUS status, Native(adapter), id, out var info);
            runningTime = info.GlobalInformation.RunningTime;
            return status == NTSTATUS.STATUS_SUCCESS;
        }
        public long Timestamp() => Stopwatch.GetTimestamp();
        public bool MemoryLimits(D3DAdapter adapter, out D3DMemoryLimits limits)
        {
            GetSegmentSize(out NTSTATUS status, Native(adapter), out var info);
            limits = new(info.SharedSystemMemorySize, info.DedicatedVideoMemorySize, info.DedicatedSystemMemorySize);
            return status == NTSTATUS.STATUS_SUCCESS;
        }
        public bool Segment(D3DAdapter adapter, uint id, out D3DSegment segment)
        {
            GetQueryStatisticsSegment(out NTSTATUS status, Native(adapter), id, out var info);
            segment = new(info.BytesResident, info.BytesCommitted, info.Aperture == 1);
            return status == NTSTATUS.STATUS_SUCCESS;
        }
        public bool Close(D3DAdapter adapter)
        {
            CloseAdapter(out NTSTATUS status, Native(adapter));
            return status == NTSTATUS.STATUS_SUCCESS;
        }
    }

    private static string GetNodeEngineTypeString(D3DKMT_NODEMETADATA nodeMetaData)
    {
        return nodeMetaData.NodeData.EngineType switch
        {
            DXGK_ENGINE_TYPE.DXGK_ENGINE_TYPE_OTHER => GetOtherName(),
            DXGK_ENGINE_TYPE.DXGK_ENGINE_TYPE_3D => "D3D 3D",
            DXGK_ENGINE_TYPE.DXGK_ENGINE_TYPE_VIDEO_DECODE => "D3D Video Decode",
            DXGK_ENGINE_TYPE.DXGK_ENGINE_TYPE_VIDEO_ENCODE => "D3D Video Encode",
            DXGK_ENGINE_TYPE.DXGK_ENGINE_TYPE_VIDEO_PROCESSING => "D3D Video Processing",
            DXGK_ENGINE_TYPE.DXGK_ENGINE_TYPE_SCENE_ASSEMBLY => "D3D Scene Assembly",
            DXGK_ENGINE_TYPE.DXGK_ENGINE_TYPE_COPY => "D3D Copy",
            DXGK_ENGINE_TYPE.DXGK_ENGINE_TYPE_OVERLAY => "D3D Overlay",
            DXGK_ENGINE_TYPE.DXGK_ENGINE_TYPE_CRYPTO => "D3D Crypto",
            _ => "D3D Unknown"
        };

        string GetOtherName()
        {
            string friendlyName = (nodeMetaData.NodeData.FriendlyName.Length > 0 ? nodeMetaData.NodeData.FriendlyName : "Other").ToString();

            if (friendlyName.StartsWith("JPEG_Decode_"))
                friendlyName = friendlyName.Replace("JPEG_Decode_", "JPEG Decode ");

            if (friendlyName.StartsWith("OFA_"))
                friendlyName = friendlyName.Replace("OFA_", "Optical Flow Accelerator ");

            return "D3D " + friendlyName;
        }
    }

    private static unsafe void GetSegmentSize
    (
        out NTSTATUS status,
        D3DKMT_OPENADAPTERFROMDEVICENAME adapter,
        out D3DKMT_SEGMENTSIZEINFO sizeInformation)
    {
        D3DKMT_SEGMENTSIZEINFO data = default;
        var queryAdapterInfo = new D3DKMT_QUERYADAPTERINFO
        {
            hAdapter = adapter.hAdapter,
            Type = KMTQUERYADAPTERINFOTYPE.KMTQAITYPE_GETSEGMENTSIZE,
            pPrivateDriverData = &data,
            PrivateDriverDataSize = (uint)sizeof(D3DKMT_SEGMENTSIZEINFO)
        };

        status = Windows.Wdk.PInvoke.D3DKMTQueryAdapterInfo(ref queryAdapterInfo);
        sizeInformation = status == NTSTATUS.STATUS_SUCCESS ? data : default;
    }

    private static unsafe void GetNodeMetaData(out NTSTATUS status, D3DKMT_OPENADAPTERFROMDEVICENAME adapter, uint nodeId, out D3DKMT_NODEMETADATA nodeMetaDataResult)
    {
        D3DKMT_NODEMETADATA data = default;
        data.NodeOrdinalAndAdapterIndex = nodeId;

        var queryAdapterInfo = new D3DKMT_QUERYADAPTERINFO
        {
            hAdapter = adapter.hAdapter,
            Type = KMTQUERYADAPTERINFOTYPE.KMTQAITYPE_NODEMETADATA,
            pPrivateDriverData = &data,
            PrivateDriverDataSize = (uint)sizeof(D3DKMT_NODEMETADATA)
        };

        status = Windows.Wdk.PInvoke.D3DKMTQueryAdapterInfo(ref queryAdapterInfo);
        nodeMetaDataResult = status == NTSTATUS.STATUS_SUCCESS ? data : default;
    }

    private static void GetQueryStatisticsNode(out NTSTATUS status, D3DKMT_OPENADAPTERFROMDEVICENAME adapter, uint nodeId, out D3DKMT_QUERYSTATISTICS_NODE_INFORMATION nodeInformation)
    {
        var queryStatistics = new D3DKMT_QUERYSTATISTICS
        {
            AdapterLuid = adapter.AdapterLuid,
            Type = D3DKMT_QUERYSTATISTICS_TYPE.D3DKMT_QUERYSTATISTICS_NODE,
            Anonymous = new D3DKMT_QUERYSTATISTICS._Anonymous_e__Union { QueryNode = new D3DKMT_QUERYSTATISTICS_QUERY_NODE { NodeId = nodeId } }
        };

        status = Windows.Wdk.PInvoke.D3DKMTQueryStatistics(queryStatistics);
        nodeInformation = queryStatistics.QueryResult.NodeInformation;
    }

    private static void GetQueryStatisticsSegment
    (
        out NTSTATUS status,
        D3DKMT_OPENADAPTERFROMDEVICENAME adapter,
        uint segmentId,
        out D3DKMT_QUERYSTATISTICS_SEGMENT_INFORMATION segmentInformation)
    {
        var queryStatistics = new D3DKMT_QUERYSTATISTICS
        {
            AdapterLuid = adapter.AdapterLuid,
            Type = D3DKMT_QUERYSTATISTICS_TYPE.D3DKMT_QUERYSTATISTICS_SEGMENT,
            Anonymous = new D3DKMT_QUERYSTATISTICS._Anonymous_e__Union { QuerySegment = new D3DKMT_QUERYSTATISTICS_QUERY_SEGMENT { SegmentId = segmentId } }
        };

        status = Windows.Wdk.PInvoke.D3DKMTQueryStatistics(queryStatistics);
        segmentInformation = queryStatistics.QueryResult.SegmentInformation;
    }

    private static void GetQueryStatisticsAdapterInformation
    (
        out NTSTATUS status,
        D3DKMT_OPENADAPTERFROMDEVICENAME adapter,
        out D3DKMT_QUERYSTATISTICS_ADAPTER_INFORMATION adapterInformation)
    {
        var queryStatistics = new D3DKMT_QUERYSTATISTICS { AdapterLuid = adapter.AdapterLuid, Type = D3DKMT_QUERYSTATISTICS_TYPE.D3DKMT_QUERYSTATISTICS_ADAPTER };

        status = Windows.Wdk.PInvoke.D3DKMTQueryStatistics(queryStatistics);

        adapterInformation = queryStatistics.QueryResult.AdapterInformation;
    }

    private static unsafe void GetAdapterType(out NTSTATUS status, D3DKMT_OPENADAPTERFROMDEVICENAME adapter, out D3DKMT_ADAPTERTYPE adapterTypeResult)
    {
        D3DKMT_ADAPTERTYPE data = default;
        var queryAdapterInfo = new D3DKMT_QUERYADAPTERINFO
        {
            hAdapter = adapter.hAdapter, Type = KMTQUERYADAPTERINFOTYPE.KMTQAITYPE_ADAPTERTYPE, pPrivateDriverData = &data, PrivateDriverDataSize = (uint)sizeof(D3DKMT_ADAPTERTYPE)
        };

        status = Windows.Wdk.PInvoke.D3DKMTQueryAdapterInfo(ref queryAdapterInfo);
        adapterTypeResult = status == NTSTATUS.STATUS_SUCCESS ? data : default;
    }

    private static unsafe void OpenAdapterFromDeviceName(out NTSTATUS status, string displayDeviceName, out D3DKMT_OPENADAPTERFROMDEVICENAME adapter)
    {
        fixed (char* pDisplayDeviceName = displayDeviceName)
        {
            adapter = new D3DKMT_OPENADAPTERFROMDEVICENAME { pDeviceName = new PCWSTR(pDisplayDeviceName) };
            status = Windows.Wdk.PInvoke.D3DKMTOpenAdapterFromDeviceName(ref adapter);
        }
    }

    private static void CloseAdapter(out NTSTATUS status, D3DKMT_OPENADAPTERFROMDEVICENAME adapter)
    {
        var closeAdapter = new D3DKMT_CLOSEADAPTER { hAdapter = adapter.hAdapter };
        status = Windows.Wdk.PInvoke.D3DKMTCloseAdapter(closeAdapter);
    }

    public struct D3DDeviceNodeInfo
    {
        public ulong Id;
        public string Name;
        public long RunningTime;
        public long QueryTimestamp;
    }

    public struct D3DDeviceInfo
    {
        public long AdapterLuid;
        public ulong GpuSharedLimit;
        public ulong GpuDedicatedLimit;
        public ulong GpuVideoMemoryLimit;

        public ulong GpuSharedUsed;
        public ulong GpuDedicatedUsed;

        public ulong GpuSharedMax;
        public ulong GpuDedicatedMax;

        public D3DDeviceNodeInfo[] Nodes;
        public bool Integrated;
    }
}
