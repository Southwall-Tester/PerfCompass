namespace PerfCompass;

/// <summary>
/// One category of named GPU load sensors from a single detached device snapshot.
/// Complete covers only the sensors exposed in that category in this poll: it does
/// not establish driver freshness, support for other engines, or whole-GPU coverage.
/// When incomplete, Load is the maximum known reading, not an estimate for missing nodes.
/// </summary>
internal sealed record GpuLoadObservation(string Kind, string Label, string[] Sources, double? Load, bool Complete);

internal static class GpuLoadObservations
{
    internal const string Version = "gpu-engines-1.0";

    // These are execution-engine names exposed by LibreHardwareMonitor 0.9.6.
    // D3D OTHER nodes may have driver-provided names, such as Compute_0; accept
    // only the named engines below and an optional numeric node suffix.
    // In particular, SensorType.Load also contains memory/controller/bus/power
    // measurements, which must never become an execution-engine maximum.
    private static readonly (string Name, string Kind, string Label)[] D3dNames =
    [
        ("3D", "3d", "3D"),
        ("Compute", "compute", "计算"),
        ("Video Encode", "encode", "编码"),
        ("Video Decode", "decode", "解码"),
        ("JPEG Decode", "decode", "解码"),
        ("Video Processing", "processing", "视频处理"),
        ("Copy", "copy", "复制")
    ];

    internal static IReadOnlyList<GpuLoadObservation> Normalize(IEnumerable<(string Name, double? Value)> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var recognized = new List<(string Kind, string Label, string Source, double? Value)>();
        foreach (var reading in source)
        {
            var category = Classify(reading.Name);
            if (category is not { } engine) continue;
            double? value = reading.Value is double number && double.IsFinite(number) && number is >= 0 and <= 100
                ? number : null;
            recognized.Add((engine.Kind, engine.Label, reading.Name, value));
        }
        return recognized.GroupBy(r => r.Kind, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new GpuLoadObservation(group.Key, group.First().Label,
                // Preserve duplicate names: different D3D nodes can share a label.
                group.Select(r => r.Source).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                group.Max(r => r.Value), group.All(r => r.Value.HasValue)))
            .ToArray();
    }

    private static (string Kind, string Label)? Classify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string normalized = name.Trim();
        if (normalized.Equals("GPU Core", StringComparison.OrdinalIgnoreCase)) return ("core", "核心");
        // The vendor Video Engine reading does not distinguish encode from decode.
        if (normalized.Equals("GPU Video Engine", StringComparison.OrdinalIgnoreCase)) return ("video", "视频引擎（合并）");
        if (normalized.StartsWith("GPU D3D ", StringComparison.OrdinalIgnoreCase)) normalized = normalized[4..];
        if (!normalized.StartsWith("D3D ", StringComparison.OrdinalIgnoreCase)) return null;
        string nodeName = normalized[4..];
        foreach (var engine in D3dNames)
            if (MatchesNode(nodeName, engine.Name)) return (engine.Kind, engine.Label);
        return null;
    }

    private static bool MatchesNode(string name, string expected)
    {
        if (name.Equals(expected, StringComparison.OrdinalIgnoreCase)) return true;
        if (!name.StartsWith(expected, StringComparison.OrdinalIgnoreCase) || name.Length <= expected.Length + 1) return false;
        char separator = name[expected.Length];
        if (separator is not (' ' or '_')) return false;
        var suffix = name.AsSpan(expected.Length + 1);
        foreach (char character in suffix)
            if (character is < '0' or > '9') return false;
        return true;
    }
}
