using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunDataProtectionTests(Action<string, Action> check, Action<bool, string> assert)
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "data-protection-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(root);
        LocalStore Store(string name) => new(Path.Combine(root, name));
        string SettingsPath(LocalStore s) => Path.Combine(s.Root, "settings.json");
        void Reject(Action operation)
        {
            try { operation(); } catch (Exception e) when (e is IOException or InvalidDataException or JsonException or OperationCanceledException or ArgumentException) { return; }
            throw new Exception("Invalid operation accepted");
        }
        string Archive(string name, Dictionary<string, byte[]> data, Func<BackupManifest, BackupManifest>? modify = null)
        {
            string path = Path.Combine(root, name + ".pcbackup");
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            var manifest = new BackupManifest(DataBackup.Version, DateTimeOffset.UtcNow, "fixture", data.Select(p => new BackupFile(p.Key, p.Value.Length, Convert.ToHexString(SHA256.HashData(p.Value)))).ToList());
            foreach (var p in data) { using var target = zip.CreateEntry(p.Key).Open(); target.Write(p.Value); }
            using var json = zip.CreateEntry("manifest.json").Open(); json.Write(JsonSerializer.SerializeToUtf8Bytes(modify?.Invoke(manifest) ?? manifest));
            return path;
        }
        check("Corrupt settings preserve exact bytes recover previous and suspend retention", () =>
        {
            var s = Store("recover"); s.SaveSettings(new(FutureNeeds: "需要虚拟机", Representative: true)); s.SaveSettings(new(FutureNeeds: "新需求"));
            byte[] broken = Encoding.UTF8.GetBytes("{truncated\n"); File.WriteAllBytes(SettingsPath(s), broken);
            string old = Path.Combine(s.Root, $"samples-{DateTime.Today.AddDays(-100):yyyy-MM-dd}.jsonl"); File.WriteAllText(old, "old");
            var reopened = new LocalStore(s.Root);
            assert(reopened.Settings.FutureNeeds == "需要虚拟机" && reopened.SettingsRecoveryPending && reopened.RetentionProtected && File.Exists(old), "fallback or retention protection failed");
            assert(File.ReadAllBytes(SettingsPath(s)).SequenceEqual(broken) && Directory.GetFiles(Path.Combine(s.Root, "recovery")).Any(f => File.ReadAllBytes(f).SequenceEqual(broken)), "original destroyed");
            reopened.SaveSettings(reopened.Settings);
            assert(reopened.SettingsRecoveryPending && new LocalStore(s.Root).SettingsRecoveryPending && File.Exists(old), "ordinary edits bypassed recovery review");
            reopened.SaveSettings(reopened.Settings, acknowledgeRecovery: true);
            assert(!reopened.SettingsRecoveryPending && new LocalStore(s.Root).Settings.FutureNeeds == "需要虚拟机" && SettingsFile.Read(Path.Combine(s.Root, "settings.previous.json")).FutureNeeds == "需要虚拟机", "recovery save replaced last good copy with corruption");
        });
        check("Both corrupt settings require review and never imply representative workload", () =>
        {
            var s = Store("both-broken"); File.WriteAllText(SettingsPath(s), "null"); File.WriteAllText(Path.Combine(s.Root, "settings.previous.json"), "[]");
            var reopened = new LocalStore(s.Root);
            assert(!reopened.Settings.Representative && reopened.SettingsRecoveryPending && Directory.GetFiles(Path.Combine(s.Root, "recovery")).Length == 2, "unsafe default or originals lost");
        });
        check("Missing current settings recover last valid copy", () =>
        {
            var s = Store("missing"); s.SaveSettings(new(Scene: "设计剪辑")); s.SaveSettings(new()); File.Delete(SettingsPath(s));
            var reopened = new LocalStore(s.Root); assert(reopened.Settings.Scene == "设计剪辑" && reopened.SettingsRecoveryPending, "previous ignored");
        });
        check("Unknown settings fields survive older-client edits including UTF8 BOM", () =>
        {
            var s = Store("unknown"); File.WriteAllText(SettingsPath(s), "{\"Scene\":\"日常办公\",\"FutureFeature\":{\"Count\":12}}", new UTF8Encoding(true));
            s = new LocalStore(s.Root); s.SaveSettings(s.Settings with { RetentionDays = 30 });
            using var json = JsonDocument.Parse(File.ReadAllText(SettingsPath(s)));
            assert(json.RootElement.GetProperty("FutureFeature").GetProperty("Count").GetInt32() == 12, "future field dropped");
        });
        check("Malformed nested null and duplicate settings are preserved as invalid", () =>
        {
            string[] inputs = ["{\"RetentionDays\":0}", "{\"FutureNeeds\":null}", "{\"Scene\":\"a\",\"scene\":\"b\"}", "{\"Requirements\":{\"Source\":\"x\",\"Facts\":null}}"];
            foreach (string text in inputs)
            {
                var s = Store(Guid.NewGuid().ToString("N")); File.WriteAllText(SettingsPath(s), text);
                s = new LocalStore(s.Root); assert(s.SettingsRecoveryPending && File.ReadAllText(SettingsPath(s)) == text, "malformed settings silently accepted");
            }
        });
        check("Case-varied settings names do not become duplicate future fields after saving", () =>
        {
            var s = Store("case-settings"); File.WriteAllText(SettingsPath(s), "{\"scene\":\"设计剪辑\",\"retentiondays\":60}");
            s = new LocalStore(s.Root); assert(s.Settings.Scene == "设计剪辑" && s.Settings.RetentionDays == 60, "known fields treated as unknown");
            s.SaveSettings(s.Settings with { CloseToTray = false });
            assert(!new LocalStore(s.Root).SettingsRecoveryPending && SettingsFile.Read(SettingsPath(s)).Scene == "设计剪辑", "save produced conflicting property names");
        });
        check("Settings save failure keeps current data and permits a later retry", () =>
        {
            var s = Store("locked"); s.SaveSettings(new(FutureNeeds: "keep"));
            using (var held = new FileStream(SettingsPath(s), FileMode.Open, FileAccess.Read, FileShare.None)) Reject(() => s.SaveSettings(new(FutureNeeds: "replace")));
            assert(s.Settings.FutureNeeds == "keep" && SettingsFile.Read(SettingsPath(s)).FutureNeeds == "keep", "failed save changed settings");
            s.SaveSettings(new(FutureNeeds: "retry")); assert(new LocalStore(s.Root).Settings.FutureNeeds == "retry", "retry failed");
        });
        var source = Store("source"); source.SaveSettings(new(FutureNeeds: "中文需求\n--input private")); source.SaveSettings(source.Settings with { Scene = "开发编译" });
        source.Append(new Sample { HardwareId = "backup-fixture", Seconds = 10 });
        source.AppendExperience(ExperienceAnalyzer.Create(DateTimeOffset.Now, "backup-fixture", "开发编译", "操作卡顿", "保留原文", []));
        string oldName = $"samples-{DateTime.Today.AddDays(-120):yyyy-MM-dd}.jsonl";
        File.WriteAllText(Path.Combine(source.Root, oldName), "old\n{broken-tail");
        File.WriteAllText(Path.Combine(source.Root, "samples-not-a-date.jsonl"), "readable-name\n{broken");
        File.WriteAllText(Path.Combine(source.Root, "comparison.json"), "{malformed-comparison");
        Directory.CreateDirectory(Path.Combine(source.Root, "measurements")); TaskMeasurements.Save(Path.Combine(source.Root, "measurements", "measurement.json"), MeasurementFixture(1, 2, 3));
        File.WriteAllText(Path.Combine(source.Root, "errors.log"), "private diagnostic"); File.WriteAllText(Path.Combine(source.Root, "run.cmd"), "never execute");
        string backup = Path.Combine(root, "roundtrip.pcbackup"); BackupPreview? good = null;
        check("Backup roundtrip preserves every recognized raw byte and excludes programs and logs", () =>
        {
            good = DataBackup.Create(source, backup); var preview = DataBackup.Inspect(backup);
            assert(good.ManifestHash == preview.ManifestHash && !preview.Manifest.Files.Any(f => f.Path.EndsWith(".cmd") || f.Path.EndsWith(".log")), "excluded files or checksum wrong");
            string restored = DataBackup.Restore(backup, root, preview.ManifestHash);
            foreach (var entry in preview.Manifest.Files) assert(File.ReadAllBytes(Path.Combine(restored, entry.Path)).SequenceEqual(File.ReadAllBytes(Path.Combine(source.Root, entry.Path))), "bytes changed " + entry.Path);
            var reopened = new LocalStore(restored); reopened.Cleanup();
            assert(reopened.RetentionProtected && File.Exists(Path.Combine(restored, oldName)) && reopened.Read(7, "backup-fixture").Count() == 1, "restore deleted old data or lost current evidence");
            assert(reopened.Settings.FutureNeeds.Contains("中文需求") && reopened.ReadExperiences(7, "backup-fixture").Count == 1, "requirements or experience lost");
            reopened.EnableRetention(reopened.PreviewRetention()); assert(!File.Exists(Path.Combine(restored, oldName)) && File.Exists(Path.Combine(source.Root, oldName)), "explicit cleanup crossed source boundary");
        });
        check("Restoring twice always creates separate directories", () =>
        {
            var p = DataBackup.Inspect(backup); string a = DataBackup.Restore(backup, root, p.ManifestHash), b = DataBackup.Restore(backup, root, p.ManifestHash);
            assert(a != b && Directory.Exists(a) && Directory.Exists(b), "restore overwrote previous directory");
        });
        check("Backup output cannot overwrite app data and failed backup preserves existing destination", () =>
        {
            Reject(() => DataBackup.Create(source, Path.Combine(source.Root, "nested.pcbackup")));
            string output = Path.Combine(root, "keep.pcbackup"); File.WriteAllText(output, "existing backup");
            using (var held = new FileStream(SettingsPath(source), FileMode.Open, FileAccess.Read, FileShare.None)) Reject(() => DataBackup.Create(source, output));
            assert(File.ReadAllText(output) == "existing backup" && !Directory.GetFiles(root, "*.tmp").Any(), "failed backup clobbered destination or leaked temporary file");
        });
        check("Untrusted archive paths devices executables and alternate streams are rejected", () =>
        {
            string[] bad = ["../settings.json", "/settings.json", "C:/settings.json", "measurements/../../settings.json", "measurements\\file.json", "measurements/file.json:stream", "measurements/CON.json", "measurements/file.json.", "run.exe"];
            foreach (string path in bad) { string a = Archive(Guid.NewGuid().ToString("N"), new() { [path] = [1, 2, 3] }); Reject(() => DataBackup.Inspect(a)); }
        });
        check("Archive checksums lengths versions duplicates and unlisted data fail validation", () =>
        {
            var data = new Dictionary<string, byte[]> { ["settings.json"] = Encoding.UTF8.GetBytes("{}") };
            var mutations = new Func<BackupManifest, BackupManifest>[]
            {
                m => m with { Version = "future" }, m => m with { Files = [m.Files[0] with { Sha256 = new string('A', 64) }] },
                m => m with { Files = [m.Files[0] with { Length = 1 }] }, m => m with { Files = [] },
                m => m with { Files = [m.Files[0] with { Length = DataBackup.MaximumBytes + 1 }] }
            };
            foreach (var mutate in mutations) Reject(() => DataBackup.Inspect(Archive(Guid.NewGuid().ToString("N"), data, mutate)));
            string dup = Archive("dup", data); using (var z = ZipFile.Open(dup, ZipArchiveMode.Update)) { using var t = z.CreateEntry("SETTINGS.JSON").Open(); t.WriteByte(1); }
            Reject(() => DataBackup.Inspect(dup));
        });
        check("ZIP links and directories are never extracted", () =>
        {
            foreach (int attribute in new[] { unchecked((int)0xA1FF0000), (int)FileAttributes.ReparsePoint, (int)FileAttributes.Directory })
            {
                string a = Archive(Guid.NewGuid().ToString("N"), new() { ["settings.json"] = [1] });
                using (var z = ZipFile.Open(a, ZipArchiveMode.Update)) z.GetEntry("settings.json")!.ExternalAttributes = attribute;
                Reject(() => DataBackup.Inspect(a));
            }
        });
        check("Changed preview and cancelled restore leave no usable partial directory", () =>
        {
            int before = Directory.GetDirectories(root).Length;
            Reject(() => DataBackup.Restore(backup, root, new string('0', 64)));
            using var cancel = new CancellationTokenSource(); cancel.Cancel(); Reject(() => DataBackup.Restore(backup, root, good!.ManifestHash, cancel.Token));
            assert(Directory.GetDirectories(root).Length == before && !Directory.GetDirectories(root, ".PerfCompass-restore-*").Any(), "failed restore leaked a partial directory");
        });
        check("Cancelled backup preserves old backup and settings", () =>
        {
            using var cancel = new CancellationTokenSource(); cancel.Cancel(); byte[] original = File.ReadAllBytes(backup);
            Reject(() => DataBackup.Create(source, backup, cancel.Token)); assert(File.ReadAllBytes(backup).SequenceEqual(original), "cancel changed old backup");
        });
    }
}
