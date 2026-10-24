using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PerfCompass;

public record ExperienceReadQuality(int ReadDays, int IssueCount, int AffectedFiles, string Signature, HistoryIssue[] Issues)
{
    public static ExperienceReadQuality Unchecked { get; } = new(0, 0, 0, "", []);
    public bool HasIssues => IssueCount > 0;
    public string Message => HasIssues ? $"最近 {ReadDays} 天的体验记录有 {IssueCount} 处读取异常。已读体验与性能观测保留，请核对原文件后再判断降档。" : "";
    public string Details => string.Join("\n", Issues.Select(i => i.Label)) + (IssueCount > Issues.Length ? "\n其余异常位置未展开。" : "");
}
public record ExperienceReadResult(List<ExperienceEvent> Events, ExperienceReadQuality Quality);

public sealed partial class LocalStore
{
    internal Action? ExperiencePrefixCapturedForTest { get; set; }
    public List<ExperienceEvent> ReadExperiences(int days, string hardwareId) => ReadExperiencesChecked(days, hardwareId).Events;

    public ExperienceReadResult ReadExperiencesChecked(int days, string hardwareId, DateTimeOffset? asOf = null, string? scene = null)
    {
        var now = asOf ?? DateTimeOffset.Now; var cutoff = now.AddDays(-days);
        using var diagnostics = new HistoryDiagnostics(days);
        var found = new Dictionary<string, (ExperienceEvent Event, string Identity, string File, long Line)>();
        var conflicts = new HashSet<string>();
        var encoding = new UTF8Encoding(false, true);
        string[] files;
        try { files = Directory.GetFiles(Root, "experience-*.jsonl").Order(StringComparer.Ordinal).ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { diagnostics.Add("数据目录", 0, "无法枚举体验记录"); Log(error); return Finish(); }
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            bool dated = DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file)[11..], "yyyy-MM-dd", null, DateTimeStyles.None, out var date);
            if (dated && date.Date < cutoff.LocalDateTime.Date) continue;
            if (!dated) diagnostics.Add(name, 0, "文件名无法确定体验日期");
            FileStream? stream = null; long length;
            try { lock (gate) { stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); length = stream.Length; } }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { stream?.Dispose(); diagnostics.Add(name, 0, "体验文件无法读取"); Log(error); continue; }
            using (stream)
            using (var lines = ReadHistoryLines(stream, length).GetEnumerator())
            {
                ExperiencePrefixCapturedForTest?.Invoke();
                while (true)
                {
                    bool next;
                    try { next = lines.MoveNext(); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { diagnostics.Add(name, 0, "体验文件读取中发生 I/O 错误"); Log(error); break; }
                    if (!next) break;
                    var line = lines.Current;
                    if (line.Bytes == null) { diagnostics.Add(name, line.Number, "体验单行超过 4 MiB 限制", fingerprint: line.Fingerprint); continue; }
                    void Issue(string reason) => diagnostics.Add(name, line.Number, reason, line.Bytes);
                    try
                    {
                        var bytes = line.Bytes.AsMemory();
                        if (line.Number == 1 && bytes.Span.StartsWith(new byte[] { 239, 187, 191 })) bytes = bytes[3..];
                        if (bytes.Span.Trim("\r \t"u8).IsEmpty) continue;
                        _ = encoding.GetCharCount(bytes.Span);
                        using var doc = JsonDocument.Parse(bytes); var root = doc.RootElement;
                        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("体验记录不是 JSON 对象");
                        UniqueMembers(root);
                        if (!root.TryGetProperty("Time", out var time) || !time.TryGetDateTimeOffset(out var timestamp)) throw new InvalidDataException("体验时间缺失或无效");
                        if (timestamp < cutoff || timestamp > now) continue;
                        if (!root.TryGetProperty("HardwareId", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())) throw new InvalidDataException("体验缺少硬件标识，无法确认归属");
                        if (id.GetString() != hardwareId) continue;
                        if (scene != null && root.TryGetProperty("Scene", out var selected) && selected.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(selected.GetString()) && selected.GetString() != scene) continue;
                        if (!root.TryGetProperty("Clues", out var clues) || clues.ValueKind != JsonValueKind.Array) throw new InvalidDataException("体验负载线索缺失或无效");
                        foreach (var clue in clues.EnumerateArray()) { if (clue.ValueKind != JsonValueKind.Object) throw new InvalidDataException("体验负载线索不是对象"); UniqueMembers(clue); }
                        var value = JsonSerializer.Deserialize<ExperienceEvent>(bytes.Span) ?? throw new InvalidDataException("体验记录为空");
                        ValidateExperience(value);
                        // Only the resolved state may vary in successive records of
                        // one event. Conflicting identities must not silently win.
                        string identity = JsonSerializer.Serialize(value with { Resolved = false });
                        if (found.TryGetValue(value.Id, out var prior) && prior.Identity != identity)
                        {
                            diagnostics.Add(prior.File, prior.Line, "相同体验标识对应不同内容", Encoding.UTF8.GetBytes(prior.Identity));
                            Issue("相同体验标识对应不同内容"); conflicts.Add(value.Id); continue;
                        }
                        found[value.Id] = (value, identity, name, line.Number);
                    }
                    catch (DecoderFallbackException) { Issue("体验记录不是有效 UTF-8 文本"); }
                    catch (JsonException) { Issue("体验 JSON 损坏或未写完整"); }
                    catch (InvalidOperationException) { Issue("体验字段类型无效"); }
                    catch (InvalidDataException error) { Issue(error.Message); }
                }
            }
        }
        return Finish();
        ExperienceReadResult Finish()
        {
            var quality = diagnostics.Build();
            return new(found.Where(p => !conflicts.Contains(p.Key)).Select(p => p.Value.Event).OrderByDescending(e => e.Time).ToList(),
                new(days, quality.IssueCount, quality.AffectedFiles, quality.Signature, quality.Issues));
        }
    }
    private static void UniqueMembers(JsonElement value)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!names.Add(property.Name)) throw new InvalidDataException("体验记录存在重复字段");
    }
    private static void ValidateExperience(ExperienceEvent value)
    {
        if (string.IsNullOrWhiteSpace(value.Id) || string.IsNullOrWhiteSpace(value.Scene) || value.Note == null || value.Symptom is not ("操作卡顿" or "任务等待过久")) throw new InvalidDataException("体验内容缺失或类型尚不支持");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in value.Clues)
        {
            if (c == null || c.Key is not ("cpu" or "core" or "memory" or "gpu" or "disk") || !keys.Add(c.Key) || string.IsNullOrWhiteSpace(c.Name)) throw new InvalidDataException("体验线索类别缺失、重复或尚不支持");
            if (!double.IsFinite(c.HighSeconds) || !double.IsFinite(c.CoveredSeconds) || c.HighSeconds < 0 || c.CoveredSeconds is < 0 or > 120.000001 || c.HighSeconds > c.CoveredSeconds + .000001
                || c.Peak is double peak && (!double.IsFinite(peak) || peak is < 0 or > 100)
                || c.CoveredSeconds > 0 && c.Peak == null || c.CoveredSeconds == 0 && (c.Peak != null || c.HighSeconds > 0)
                || c.HighSeconds > 0 && c.Peak < (c.Key == "gpu" ? 95 : 90)) throw new InvalidDataException("体验线索的峰值或采样时长矛盾");
        }
    }
}
