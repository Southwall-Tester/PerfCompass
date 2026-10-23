// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// Copyright (C) PerfCompass contributors.

using System;
using System.Collections.Generic;
using System.Linq;

namespace LibreHardwareMonitor.Hardware;

/// <summary>Maps stable raw node identities to sensors without indexing by enumeration position.</summary>
internal sealed class D3DNodeSensors
{
    private readonly Hardware _owner;
    private readonly ISettings _settings;
    private readonly Action<Sensor> _activate, _deactivate;
    private readonly D3DNodeSampler _sampler = new();
    private readonly Dictionary<D3DNodeKey, Sensor> _known = new();
    private readonly HashSet<D3DNodeKey> _active = new();
    private int _nextIndex;

    internal D3DNodeSensors(Hardware owner, ISettings settings, int firstSensorIndex, Action<Sensor> activate, Action<Sensor> deactivate)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(activate); ArgumentNullException.ThrowIfNull(deactivate);
        if (firstSensorIndex < 0) throw new ArgumentOutOfRangeException(nameof(firstSensorIndex));
        _owner = owner; _settings = settings; _nextIndex = firstSensorIndex; _activate = activate; _deactivate = deactivate;
    }

    internal void Invalidate()
    {
        _sampler.Invalidate();
        foreach (var key in _active) _known[key].Value = null;
    }

    internal void Update(D3DDisplayDevice.D3DDeviceInfo frame)
    {
        foreach (var key in _active) _known[key].Value = null;
        if (!_sampler.TryUpdate(frame, out var loads)) return;
        var current = loads.Select(load => load.Key).ToHashSet();
        // Bound lifetime sensor allocation when a broken provider repeatedly
        // changes its topology. Do not recycle identifiers into unrelated nodes.
        if (_known.Count + current.Count(key => !_known.ContainsKey(key)) > D3DNodeSampler.MaximumNodes)
        { Invalidate(); return; }
        foreach (var key in _active.Where(key => !current.Contains(key)).ToArray())
        {
            _known[key].Value = null; _deactivate(_known[key]); _active.Remove(key);
        }
        foreach (var load in loads)
        {
            if (!_known.TryGetValue(load.Key, out var sensor))
            {
                while (_owner.Sensors.Any(s => s.SensorType == SensorType.Load && s.Index == _nextIndex)) _nextIndex = checked(_nextIndex + 1);
                sensor = new Sensor(load.Key.Name, _nextIndex, SensorType.Load, _owner, _settings);
                _nextIndex = checked(_nextIndex + 1);
                _known.Add(load.Key, sensor);
            }
            // First/baseline-only frames deliberately expose a named null sensor.
            // Consumers can distinguish missing work evidence from a known zero.
            sensor.Value = load.Value; _activate(sensor); _active.Add(load.Key);
        }
    }
}
