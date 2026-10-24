using System.IO;
using System.Text.Json;

namespace PerfCompass;

public sealed partial class LocalStore
{
    private readonly object gate = new();
    public string Root { get; }
    public Settings Settings { get; private set; }
    public string SettingsNotice { get; private set; } = "";
    public bool SettingsRecoveryPending { get; private set; }
    private string RecoveryMarker => Path.Combine(Root, "settings-recovery-pending.json");
    public bool RetentionProtected => retentionBlocked || File.Exists(Path.Combine(Root, "restore-protection.json")) || SettingsRecoveryPending;
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    public LocalStore(string root) : this(root, SamplingInstant.Now) { }
    internal LocalStore(string root, Func<SamplingInstant> timeSource)
    {
        retentionTime = timeSource; retentionBaseline = timeSource();
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        string path = Path.Combine(Root, "settings.json"), previous = Path.Combine(Root, "settings.previous.json");
        Settings = new(); bool needsFallback = false;
        if (File.Exists(path))
        {
            try { Settings = SettingsFile.Read(path); }
            catch (Exception e) when (SettingsFile.IsInvalid(e))
            {
                string saved = PreserveSettings(path);
                SettingsRecoveryPending = true;
                needsFallback = true;
                SettingsNotice = $"设置损坏，原件已另存到 recovery/{Path.GetFileName(saved)}。";
            }
        }
        if (needsFallback || !File.Exists(path) && File.Exists(previous))
        {
            SettingsRecoveryPending = true;
            try { Settings = SettingsFile.Read(previous); SettingsNotice += "已读取上次有效设置，请核对并保存。"; }
            catch (FileNotFoundException) { SettingsNotice += "没有上次有效设置，暂用默认值，请重新核对需求。"; }
            catch (Exception e) when (SettingsFile.IsInvalid(e))
            { PreserveSettings(previous); SettingsNotice += "上次设置也已损坏并保留，暂用默认值，请重新核对需求。"; }
        }
        if (SettingsRecoveryPending && !File.Exists(RecoveryMarker))
            SettingsFile.AtomicWrite(RecoveryMarker, JsonSerializer.SerializeToUtf8Bytes(new { Created = DateTimeOffset.UtcNow }));
        if (File.Exists(RecoveryMarker))
        {
            SettingsRecoveryPending = true;
            if (SettingsNotice.Length == 0) SettingsNotice = "此前的设置恢复尚未核对，请核对当前设置并保存恢复结果。";
        }
        Cleanup();
    }
    private string PreserveSettings(string path)
    {
        string folder = Path.Combine(Root, "recovery"); Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(path)}-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}.json");
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(destination); destination.Flush(true); return target;
    }
    public void SaveSettings(Settings value, bool acknowledgeRecovery = false)
    {
        lock (gate)
        {
            var path = Path.Combine(Root, "settings.json");
            byte[] bytes = SettingsFile.Encode(value);
            if (File.Exists(path))
            {
                try
                {
                    _ = SettingsFile.Read(path);
                    SettingsFile.AtomicWrite(Path.Combine(Root, "settings.previous.json"), File.ReadAllBytes(path));
                }
                catch (Exception e) when (SettingsFile.IsInvalid(e)) { PreserveSettings(path); }
            }
            SettingsFile.AtomicWrite(path, bytes);
            Settings = value;
            if (acknowledgeRecovery)
            {
                if (File.Exists(RecoveryMarker)) File.Delete(RecoveryMarker);
                SettingsRecoveryPending = false;
            }
        }
    }
    public void Append(Sample sample)
    {
        lock (gate)
        {
            var path = Path.Combine(Root, $"samples-{sample.Time.LocalDateTime:yyyy-MM-dd}.jsonl");
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            if (stream.Length > 0)
            {
                stream.Seek(-1, SeekOrigin.End);
                bool complete = stream.ReadByte() == '\n';
                stream.Seek(0, SeekOrigin.End);
                if (!complete) stream.WriteByte((byte)'\n');
            }
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            writer.WriteLine(JsonSerializer.Serialize(sample));
        }
    }
    public IEnumerable<Sample> Read(int days, string? hardwareId = null) => ReadChecked(days, hardwareId, DateTimeOffset.Now, null);
    internal T WithDataLock<T>(Func<T> operation) { lock (gate) return operation(); }
    public void AppendExperience(ExperienceEvent value)
    {
        lock (gate)
        {
            var path = Path.Combine(Root, $"experience-{value.Time.LocalDateTime:yyyy-MM-dd}.jsonl");
            // Always separate a potentially truncated crash tail from the next complete event.
            File.AppendAllText(path, "\n" + JsonSerializer.Serialize(value) + "\n", new System.Text.UTF8Encoding(false));
        }
    }
    public void Log(Exception e)
    {
        try
        {
            lock (gate)
            {
                var path = Path.Combine(Root, "errors.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.Move(path, path + ".1", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {e}\n");
            }
        }
        catch { }
    }
}
