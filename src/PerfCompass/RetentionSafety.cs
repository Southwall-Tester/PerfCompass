using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PerfCompass;

public sealed partial class LocalStore
{
    private const string RetentionVersion = "retention-1.0";
    private string RetentionStatePath => Path.Combine(Root, "retention-state.json");
    private string ProtectionPath => Path.Combine(Root, "restore-protection.json");
    private readonly Func<SamplingInstant> retentionTime;
    private SamplingInstant retentionBaseline;
    private volatile bool retentionBlocked;
    private bool retentionStateUnreadable;
    public string RetentionNotice { get; private set; } = "";
    public bool RequiresPausedStart
    {
        get
        {
            if (SettingsRecoveryPending) return true;
            if (!File.Exists(ProtectionPath)) return false;
            try
            {
                if (new FileInfo(ProtectionPath).Length > 65536) return true;
                using var doc = JsonDocument.Parse(File.ReadAllBytes(ProtectionPath));
                return !doc.RootElement.TryGetProperty("Code", out var code) || code.GetString() != "retention-review";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return true; }
        }
    }
    private static string Day(DateTimeOffset time) => time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private void ProtectRetention(string reason, SamplingInstant instant)
    {
        retentionBlocked = true; RetentionNotice = reason;
        if (!File.Exists(ProtectionPath)) SettingsFile.AtomicWrite(ProtectionPath,
            JsonSerializer.SerializeToUtf8Bytes(new { Reason = reason, Code = "retention-review", Observed = instant.Time }));
    }
    private SamplingInstant CheckRetentionTime(bool includeCheckpoint = true)
    {
        var instant = retentionTime(); var elapsedSpan = Stopwatch.GetElapsedTime(retentionBaseline.Timestamp, instant.Timestamp); double elapsed = elapsedSpan.TotalSeconds;
        double calendar = (instant.Time - retentionBaseline.Time).TotalSeconds;
        bool dateMismatch;
        try { dateMismatch = retentionBaseline.Time.Add(elapsedSpan).Date != instant.Time.Date; }
        catch (ArgumentOutOfRangeException) { dateMismatch = true; }
        if (File.Exists(ProtectionPath) && RetentionNotice.Length == 0)
        {
            try
            {
                if (new FileInfo(ProtectionPath).Length <= 65536)
                {
                    using var marker = JsonDocument.Parse(File.ReadAllBytes(ProtectionPath));
                    if (marker.RootElement.TryGetProperty("Reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                        RetentionNotice = (reason.GetString() ?? "")[..Math.Min(500, reason.GetString()?.Length ?? 0)];
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        }
        if (elapsed < 0 || Math.Abs(calendar - elapsed) > 5 || dateMismatch || instant.Time.Offset != retentionBaseline.Time.Offset)
            ProtectRetention("采样期间日期或时间发生变化，请先核对系统日期，再核对到期文件。", instant);
        if (!includeCheckpoint) return instant;
        if (File.Exists(RetentionStatePath))
        {
            try
            {
                DataBackup.NoLinks(RetentionStatePath);
                if (new FileInfo(RetentionStatePath).Length > 4096) throw new InvalidDataException();
                using var doc = JsonDocument.Parse(File.ReadAllBytes(RetentionStatePath)); var value = doc.RootElement;
                if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count()
                    || value.GetProperty("Version").GetString() != RetentionVersion) throw new InvalidDataException();
                var previous = DateOnly.ParseExact(value.GetProperty("Date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                int offset = value.GetProperty("OffsetMinutes").GetInt32();
                if (offset is < -840 or > 840) throw new InvalidDataException();
                retentionStateUnreadable = false;
                int days = DateOnly.FromDateTime(instant.Time.Date).DayNumber - previous.DayNumber;
                if (days < 0 || days > 1 || offset != instant.Time.Offset.TotalMinutes) ProtectRetention("日期或时区变化，或距离上次清理核对已超过一天，请先核对系统日期与到期文件。", instant);
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or FormatException or KeyNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentNullException)
            { retentionStateUnreadable = true; ProtectRetention("清理日期记录无法读取，原件保留。请先核对系统日期与到期文件。", instant); }
        }
        else if (ExpiredAt(instant.Time).Length > 0)
            ProtectRetention("尚未核对旧记录的清理日期，请先确认系统日期与到期文件。", instant);
        return instant;
    }
    private void SaveRetentionDate(SamplingInstant instant, CancellationToken cancellation)
    {
        DataBackup.NoLinks(RetentionStatePath);
        if (retentionStateUnreadable && File.Exists(RetentionStatePath))
        {
            string folder = Path.Combine(Root, "recovery"); Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, Guid.NewGuid().ToString("N") + "-retention-state.json");
            string partial = target + ".partial";
            try
            {
                using (var source = new FileStream(RetentionStatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var destination = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[65536]; int read;
                    while ((read = source.Read(buffer)) > 0) { cancellation.ThrowIfCancellationRequested(); destination.Write(buffer, 0, read); }
                    destination.Flush(true);
                }
                cancellation.ThrowIfCancellationRequested(); File.Move(partial, target);
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
        cancellation.ThrowIfCancellationRequested();
        SettingsFile.AtomicWrite(RetentionStatePath, JsonSerializer.SerializeToUtf8Bytes(new { Version = RetentionVersion, Date = Day(instant.Time), OffsetMinutes = (int)instant.Time.Offset.TotalMinutes }));
        retentionStateUnreadable = false;
    }
    private string[] ExpiredAt(DateTimeOffset now) => Directory.GetFiles(Root, "samples-*.jsonl").Concat(Directory.GetFiles(Root, "experience-*.jsonl"))
        .Where(file => { string name = Path.GetFileNameWithoutExtension(file); string suffix = name.StartsWith("experience-", StringComparison.Ordinal) ? name[11..] : name[8..];
            return DateTime.TryParseExact(suffix, "yyyy-MM-dd", null, DateTimeStyles.None, out var date) && date.Date < now.Date.AddDays(-Settings.RetentionDays); }).Order(StringComparer.Ordinal).ToArray();
    private string ProtectionStamp()
    {
        if (!File.Exists(ProtectionPath)) return "";
        using var source = new FileStream(ProtectionPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(source));
    }
    private RetentionPreview DescribeRetention(SamplingInstant instant, CancellationToken cancellation)
    {
        var files = new List<RetentionFile>();
        foreach (string path in ExpiredAt(instant.Time))
        {
            cancellation.ThrowIfCancellationRequested(); using var held = new RetentionFileLease(path); files.Add(held.Describe(Path.GetFileName(path), cancellation));
        }
        return new(Root, Day(instant.Time), Settings.RetentionDays, ProtectionStamp(), files.ToArray());
    }
    public RetentionPreview PreviewRetention(CancellationToken cancellation = default)
    { lock (gate) { var instant = CheckRetentionTime(); return DescribeRetention(instant, cancellation); } }
    public string[] ExpiredHistoryFiles() { lock (gate) return ExpiredAt(retentionTime().Time); }
    internal bool ObserveRetentionTime()
    {
        lock (gate)
        {
            bool protectedBefore = RetentionProtected;
            try { CheckRetentionTime(includeCheckpoint: false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            { retentionBlocked = true; if (RetentionNotice.Length == 0) RetentionNotice = "清理时间无法校验，自动清理已暂停。"; Log(error); }
            return !protectedBefore && RetentionProtected;
        }
    }
    public void Cleanup()
    {
        lock (gate)
        {
            try
            {
                var instant = CheckRetentionTime(); if (RetentionProtected) return;
                var preview = DescribeRetention(instant, CancellationToken.None);
                ApplyRetention(preview, false, CancellationToken.None);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            {
                try { ProtectRetention("自动清理未完成，后续清理已暂停。请检查文件占用、权限与保留记录。", retentionTime()); }
                catch (Exception markerError) when (markerError is IOException or UnauthorizedAccessException) { Log(markerError); }
                Log(error);
            }
        }
    }
    public void EnableRetention(RetentionPreview preview, CancellationToken cancellation = default)
    { lock (gate) { if (SettingsRecoveryPending) throw new InvalidOperationException("请先核对并保存恢复的设置。"); ApplyRetention(preview, true, cancellation); } }
    private void ApplyRetention(RetentionPreview preview, bool explicitReview, CancellationToken cancellation)
    {
        var held = new List<RetentionFileLease>();
        try
        {
            var instant = CheckRetentionTime();
            if (!explicitReview && RetentionProtected) return;
            var names = ExpiredAt(instant.Time).Select(Path.GetFileName).ToArray();
            if (preview.Root != Root || preview.Date != Day(instant.Time) || preview.Days != Settings.RetentionDays || preview.ProtectionStamp != ProtectionStamp()
                || !names.SequenceEqual(preview.Files.Select(f => f.Name))) throw new InvalidOperationException("日期、清理设置或到期文件已变化，请重新预览并核对。");
            foreach (var file in preview.Files)
            {
                cancellation.ThrowIfCancellationRequested(); var lease = new RetentionFileLease(Path.Combine(Root, file.Name)); held.Add(lease);
                if (lease.Describe(file.Name, cancellation) != file) throw new InvalidOperationException("到期文件内容已变化，请重新预览并核对。");
            }
            cancellation.ThrowIfCancellationRequested();
            var finalTime = CheckRetentionTime();
            if (Day(finalTime.Time) != preview.Date || ProtectionStamp() != preview.ProtectionStamp || (!explicitReview && RetentionProtected)
                || !ExpiredAt(finalTime.Time).Select(Path.GetFileName).SequenceEqual(names))
                throw new InvalidOperationException("日期或保护状态已变化，请重新预览并核对。");
            SaveRetentionDate(finalTime, cancellation);
            // All files are validated and held without write/rename sharing before the first disposition.
            // Cancellation is no longer observed once deletion starts, to avoid cancellation-induced partial deletion.
            foreach (var lease in held) lease.Delete();
            foreach (var lease in held) lease.Dispose(); held.Clear();
            if (explicitReview && File.Exists(ProtectionPath)) File.Delete(ProtectionPath);
            retentionBaseline = finalTime; retentionBlocked = false; RetentionNotice = "";
        }
        catch
        {
            ProtectRetention("清理未完成，自动清理保护继续保留。请检查文件占用、权限或空间后重新核对。", retentionTime()); throw;
        }
        finally { foreach (var lease in held) lease.Dispose(); }
    }
}
