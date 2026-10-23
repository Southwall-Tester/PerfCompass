// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// Copyright (C) PerfCompass contributors.

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace LibreHardwareMonitor.Hardware;

internal readonly record struct D3DNodeKey(long AdapterLuid, ulong Id, string Name);
internal readonly record struct D3DNodeLoad(D3DNodeKey Key, float? Value);

/// <summary>
/// Converts complete successful D3D raw frames into independently timed node
/// observations. A failed/reset interval is unknown, never a clamped zero/100.
/// Host QPC timing removes calendar adjustments; it cannot certify driver freshness.
/// </summary>
internal sealed class D3DNodeSampler
{
    internal const int MaximumNodes = 4096;
    internal const double MaximumIntervalSeconds = 15;
    private readonly long _frequency;
    private readonly Dictionary<D3DNodeKey, (long RunningTime, long Timestamp)> _baselines = new();
    private readonly HashSet<D3DNodeKey> _topology = new();
    private bool _hasTopology;

    internal D3DNodeSampler() : this(Stopwatch.Frequency) { }
    internal D3DNodeSampler(long frequency)
    {
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        _frequency = frequency;
    }

    internal void Invalidate()
    {
        _baselines.Clear(); _topology.Clear(); _hasTopology = false;
    }

    internal bool TryUpdate(D3DDisplayDevice.D3DDeviceInfo frame, out D3DNodeLoad[] loads)
    {
        loads = Array.Empty<D3DNodeLoad>();
        if (frame.Nodes == null || frame.Nodes.Length > MaximumNodes) { Invalidate(); return false; }
        var keys = new HashSet<D3DNodeKey>();
        var ordinals = new HashSet<ulong>();
        foreach (var node in frame.Nodes)
        {
            if (node.Id > uint.MaxValue || string.IsNullOrWhiteSpace(node.Name) || !ordinals.Add(node.Id))
            { Invalidate(); return false; }
            keys.Add(new(frame.AdapterLuid, node.Id, node.Name));
        }
        if (!_hasTopology || !_topology.SetEquals(keys))
        {
            _baselines.Clear(); _topology.Clear(); _topology.UnionWith(keys); _hasTopology = true;
        }
        loads = new D3DNodeLoad[frame.Nodes.Length];
        for (int i = 0; i < frame.Nodes.Length; i++)
        {
            var node = frame.Nodes[i]; var key = new D3DNodeKey(frame.AdapterLuid, node.Id, node.Name);
            loads[i] = new(key, null);
            if (node.RunningTime < 0 || node.QueryTimestamp < 0) { _baselines.Remove(key); continue; }
            if (!_baselines.TryGetValue(key, out var previous))
            {
                _baselines[key] = (node.RunningTime, node.QueryTimestamp);
                continue;
            }
            // Subtract integral values before converting; two large cumulative
            // counters can have a small, meaningful difference.
            if (node.RunningTime < previous.RunningTime || node.QueryTimestamp <= previous.Timestamp)
            { _baselines.Remove(key); continue; }
            long runningDelta = node.RunningTime - previous.RunningTime;
            long timestampDelta = node.QueryTimestamp - previous.Timestamp;
            double elapsed = timestampDelta / (double)_frequency;
            double value = 100d * (runningDelta / (double)TimeSpan.TicksPerSecond) / elapsed;
            if (!double.IsFinite(elapsed) || elapsed <= 0 || elapsed > MaximumIntervalSeconds
                || !double.IsFinite(value) || value < 0 || value > 100)
            { _baselines.Remove(key); continue; }
            _baselines[key] = (node.RunningTime, node.QueryTimestamp);
            loads[i] = new(key, (float)value);
        }
        return true;
    }
}
