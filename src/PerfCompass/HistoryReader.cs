using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PerfCompass;

public sealed partial class LocalStore
{
    internal const int MaximumHistoryLineBytes = 4 * 1024 * 1024;
    private sealed record HistoryLine(long Number, byte[]? Bytes, string Fingerprint);
    private static IEnumerable<HistoryLine> ReadHistoryLines(Stream stream, long length)
    {
        byte[] buffer = new byte[64 * 1024]; long remaining = length, number = 1;
        using var line = new MemoryStream(); bool oversized = false;
        using var oversizedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (remaining > 0)
        {
            int read = stream.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
            if (read == 0) throw new IOException("历史文件在读取过程中被缩短。");
            remaining -= read; int offset = 0;
            while (offset < read)
            {
                int end = Array.IndexOf(buffer, (byte)'\n', offset, read - offset);
                int count = (end < 0 ? read : end) - offset;
                if (!oversized && line.Length + count > MaximumHistoryLineBytes)
                { oversized = true; oversizedHash.AppendData(line.GetBuffer(), 0, (int)line.Length); }
                if (oversized) oversizedHash.AppendData(buffer, offset, count); else line.Write(buffer, offset, count);
                offset += count;
                if (end < 0) break;
                yield return new(number++, oversized ? null : line.ToArray(), oversized ? Convert.ToHexString(oversizedHash.GetHashAndReset()) : "");
                line.SetLength(0); oversized = false; offset++;
            }
        }
        if (line.Length > 0 || oversized) yield return new(number, oversized ? null : line.ToArray(), oversized ? Convert.ToHexString(oversizedHash.GetHashAndReset()) : "");
    }
    internal IEnumerable<Sample> ReadChecked(int days, string? hardwareId, DateTimeOffset now, HistoryDiagnostics? diagnostics)
    {
        var cutoff = now.AddDays(-days); string[] files;
        try { files = Directory.GetFiles(Root, "samples-*.jsonl").Order(StringComparer.Ordinal).ToArray(); }
        catch (Exception error) when (diagnostics != null && error is IOException or UnauthorizedAccessException)
        { diagnostics.Add("数据目录", 0, "无法枚举历史文件"); Log(error); yield break; }
        var encoding = new UTF8Encoding(false, true);
        var audit = diagnostics == null ? null : new HistoryAudit();
        foreach (var file in files)
        {
            string name = Path.GetFileName(file);
            bool dated = DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file)[8..], "yyyy-MM-dd", null, DateTimeStyles.None, out var date);
            if (dated && date.Date < cutoff.LocalDateTime.Date) continue;
            if (!dated) diagnostics?.Add(name, 0, "文件名无法确定记录日期");
            FileStream? stream = null; long length = 0;
            try
            {
                // Capture the committed prefix while this instance's append lock is held.
                // Read only that prefix, so later appends cannot expose an unfinished tail.
                lock (gate) { stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); length = stream.Length; }
            }
            catch (Exception error) when (diagnostics != null && error is IOException or UnauthorizedAccessException)
            { stream?.Dispose(); diagnostics.Add(name, 0, "文件无法读取"); Log(error); continue; }
            using (stream!)
            using (var lines = ReadHistoryLines(stream!, length).GetEnumerator())
            {
                while (true)
                {
                    bool next;
                    try { next = lines.MoveNext(); }
                    catch (Exception error) when (diagnostics != null && error is IOException or UnauthorizedAccessException)
                    { diagnostics.Add(name, 0, "文件在读取时发生 I/O 错误"); Log(error); break; }
                    if (!next) break;
                    var line = lines.Current;
                    if (line.Bytes == null) { diagnostics?.Add(name, line.Number, "单行超过 4 MiB 限制", fingerprint: line.Fingerprint); continue; }
                    void Issue(string reason) => diagnostics?.Add(name, line.Number, reason, line.Bytes);
                    Sample? sample = null; bool accepted = false;
                    try
                    {
                        var bytes = line.Bytes.AsMemory();
                        if (line.Number == 1 && bytes.Span.StartsWith(new byte[] { 239, 187, 191 })) bytes = bytes[3..];
                        if (bytes.Span.Trim("\r \t"u8).IsEmpty) continue;
                        _ = encoding.GetCharCount(bytes.Span);
                        using var doc = JsonDocument.Parse(bytes);
                        var root = doc.RootElement;
                        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("记录不是 JSON 对象");
                        var names = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var property in root.EnumerateObject()) if (!names.Add(property.Name)) throw new InvalidDataException("存在重复字段");
                        if (!root.TryGetProperty("Time", out var time) || !time.TryGetDateTimeOffset(out var timestamp)) throw new InvalidDataException("缺少有效时间");
                        if (timestamp < cutoff || timestamp > now) continue;
                        if (hardwareId != null && root.TryGetProperty("HardwareId", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()) && id.GetString() != hardwareId) continue;
                        if (!root.TryGetProperty("HardwareId", out var identity) || identity.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(identity.GetString()))
                            throw new InvalidDataException("缺少硬件标识，无法确认归属");
                        if (!root.TryGetProperty("Seconds", out var seconds) || !seconds.TryGetDouble(out double duration) || !double.IsFinite(duration) || duration is < 0 or > 15)
                            throw new InvalidDataException("采样时长缺失或超出 0–15 秒");
                        if (!root.TryGetProperty("Active", out var active) || active.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("缺少有效使用状态");
                        sample = root.Deserialize<Sample>();
                        if (sample != null && !string.IsNullOrEmpty(sample.SamplingIssue)
                            && (!SamplingClock.KnownIssue(sample.SamplingIssue) || sample.Seconds != 0))
                            throw new InvalidDataException("采样中断状态无效，或中断记录包含有效时长");
                        if (sample?.SamplingIssue == null && sample != null) sample = sample with { SamplingIssue = "" };
                        if (sample != null && (sample.Scene == null || !root.TryGetProperty("Scene", out _))) sample = sample with { Scene = "未标记" };
                        if (sample != null && audit != null)
                        {
                            if (audit.IsDuplicate(HistoryFingerprint.From(bytes.Span.Trim("\r \t"u8)))) { diagnostics!.Duplicate(name, line.Number); continue; }
                            var covered = audit.Cover(sample);
                            sample = sample with { UniqueObservationSeconds = covered.Total, UniqueSceneSeconds = covered.Scene };
                            if (covered.Overlap)
                            {
                                diagnostics!.HasIntervalConflicts = true;
                                Issue("同一配置的时间片重叠，无法确认记录来源和负载份额");
                            }
                        }
                        accepted = sample != null;
                    }
                    catch (DecoderFallbackException) { Issue("不是有效 UTF-8 文本"); }
                    catch (JsonException) { Issue("JSON 损坏或记录未写完整"); }
                    catch (InvalidOperationException) { Issue("字段类型无效"); }
                    catch (InvalidDataException error) { Issue(error.Message); }
                    if (accepted && sample != null && (hardwareId == null || sample.HardwareId == hardwareId)) yield return sample;
                }
            }
        }
    }
}
