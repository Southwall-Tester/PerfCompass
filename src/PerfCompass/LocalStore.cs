using System.IO;
using System.Text.Json;

namespace PerfCompass;

public sealed class LocalStore
{
    private readonly object gate = new();
    public string Root { get; }
    public Settings Settings { get; private set; }
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    public LocalStore(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
        try { Settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(root, "settings.json"))) ?? new(); }
        catch { Settings = new(); }
        Cleanup();
    }
    public void SaveSettings(Settings value)
    {
        lock (gate)
        {
            var path = Path.Combine(Root, "settings.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value));
            File.Move(path + ".tmp", path, true);
            Settings = value;
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
    public IEnumerable<Sample> Read(int days, string? hardwareId = null)
    {
        var cutoff = DateTimeOffset.Now.AddDays(-days);
        foreach (var file in Directory.GetFiles(Root, "samples-*.jsonl").Order())
        {
            if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file)[8..], "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var date) && date.Date < cutoff.LocalDateTime.Date) continue;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                Sample? sample = null;
                try { sample = JsonSerializer.Deserialize<Sample>(line); } catch (JsonException) { }
                if (sample != null && sample.Time >= cutoff && (hardwareId == null || sample.HardwareId == hardwareId)) yield return sample;
            }
        }
    }
    public void Cleanup()
    {
        foreach (var file in Directory.GetFiles(Root, "samples-*.jsonl").Concat(Directory.GetFiles(Root, "experience-*.jsonl")))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string suffix = name.StartsWith("experience-", StringComparison.Ordinal) ? name[11..] : name[8..];
            if (DateTime.TryParseExact(suffix, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var date)
                && date.Date < DateTime.Today.AddDays(-Math.Clamp(Settings.RetentionDays, 1, 90))) File.Delete(file);
        }
    }
    public void AppendExperience(ExperienceEvent value)
    {
        lock (gate)
        {
            var path = Path.Combine(Root, $"experience-{value.Time.LocalDateTime:yyyy-MM-dd}.jsonl");
            // Always separate a potentially truncated crash tail from the next complete event.
            File.AppendAllText(path, "\n" + JsonSerializer.Serialize(value) + "\n", new System.Text.UTF8Encoding(false));
        }
    }
    public List<ExperienceEvent> ReadExperiences(int days, string hardwareId)
    {
        var found = new Dictionary<string, ExperienceEvent>();
        var cutoff = DateTimeOffset.Now.AddDays(-days);
        foreach (var file in Directory.GetFiles(Root, "experience-*.jsonl").Order())
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    var value = JsonSerializer.Deserialize<ExperienceEvent>(line);
                    if (value != null && !string.IsNullOrWhiteSpace(value.Id) && value.HardwareId == hardwareId && value.Time >= cutoff
                        && value.Clues != null && value.Clues.All(c => c != null)) found[value.Id] = value;
                }
                catch (JsonException) { }
            }
        }
        return found.Values.OrderByDescending(e => e.Time).ToList();
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
