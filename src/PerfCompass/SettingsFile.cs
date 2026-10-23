using System.IO;
using System.Text;
using System.Text.Json;

namespace PerfCompass;

internal static class SettingsFile
{
    internal const int MaximumBytes = 2 * 1024 * 1024;
    internal static Settings Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaximumBytes) throw new InvalidDataException("设置文件超过 2 MiB。");
        using var reader = new StreamReader(file, new UTF8Encoding(false, true), true);
        using var json = JsonDocument.Parse(reader.ReadToEnd());
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("设置必须为 JSON 对象。");
        void Unique(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in element.EnumerateObject())
                { if (!names.Add(p.Name)) throw new InvalidDataException("设置存在重复字段。"); Unique(p.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var e in element.EnumerateArray()) Unique(e);
        }
        Unique(json.RootElement);
        var value = json.RootElement.Deserialize<Settings>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("设置为空。");
        Validate(value); return value;
    }
    internal static void Validate(Settings value)
    {
        if (value.Scene == null || value.FutureNeeds == null || value.ModelEndpoint == null || value.ModelName == null
            || value.RepresentativeHardwareId == null || value.RepresentativeHardwareId.Length > 200
            || value.RepresentativeObservationVersion == null || value.RepresentativeObservationVersion.Length > 64
            || value.RetentionDays is < 1 or > 90) throw new InvalidDataException("设置字段缺失或历史保留天数无效。");
        if (value.Requirements is { } r && (r.Source == null || r.RuleVersion == null || r.AiMode == null || r.Facts == null
            || r.Facts.Any(f => f == null || f.Key == null || f.Label == null || f.Evidence == null)))
            throw new InvalidDataException("规则需求记录不完整。");
        if (value.SemanticRequirements is { } s && (s.Source == null || s.Model == null || s.Provider == null || s.ContractVersion == null || s.Facts == null
            || s.Facts.Any(f => f == null || f.Subject == null || f.Kind == null || f.Polarity == null || f.Timeframe == null || f.Value == null || f.Unit == null || f.Condition == null || f.Evidence == null)))
            throw new InvalidDataException("模型需求记录不完整。");
    }
    internal static bool IsInvalid(Exception e) => e is JsonException or InvalidDataException or DecoderFallbackException;
    internal static void WriteDurable(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes); file.Flush(true);
    }
    internal static void AtomicWrite(string path, byte[] bytes)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { WriteDurable(temp, bytes); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static byte[] Encode(Settings value)
    {
        Validate(value); byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("设置文件超过 2 MiB。");
        return bytes;
    }
}
