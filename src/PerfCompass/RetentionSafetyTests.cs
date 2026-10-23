using System.Diagnostics;
using System.IO;
using System.Text;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunRetentionSafetyTests(Action<string, Action> check, Action<bool, string> assert)
    {
        var origin = DateTimeOffset.Now;
        SamplingInstant At(double wall, double elapsed) => new(origin.AddSeconds(wall), (long)Math.Round(elapsed * Stopwatch.Frequency));
        (LocalStore Store, Action<double, double> Set) Fixture()
        {
            var current = At(0, 0);
            return (new LocalStore(Path.Combine(Path.GetTempPath(), "PerfCompass-retention-" + Guid.NewGuid().ToString("N")), () => current),
                (wall, elapsed) => current = At(wall, elapsed));
        }
        string FileAt(LocalStore s, int days, string prefix = "samples")
        { string path = Path.Combine(s.Root, $"{prefix}-{origin.AddDays(days):yyyy-MM-dd}.jsonl"); File.WriteAllText(path, "preserve source bytes 中文"); return path; }
        void Reject(Action action) { bool failed = false; try { action(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException) { failed = true; } assert(failed, "unsafe operation accepted"); }
        void Protect(LocalStore s) => File.WriteAllText(Path.Combine(s.Root, "restore-protection.json"), "{\"Reason\":\"isolated fixture protection\"}");
        check("Normal retention advances by observed calendar days and preserves the boundary and unrelated files", () =>
        {
            var (s, set) = Fixture(); string old = FileAt(s, -120), boundary = FileAt(s, -90), eventFile = FileAt(s, -120, "experience");
            string note = Path.Combine(s.Root, "notes.txt"); File.WriteAllText(note, "keep"); s.Cleanup();
            assert(!File.Exists(old) && !File.Exists(eventFile) && File.Exists(boundary) && File.Exists(note) && !s.RetentionProtected, "retention crossed its namespace or date boundary");
            set(86400, 86400); s.Cleanup(); assert(!File.Exists(boundary) && !s.RetentionProtected, "normal next-day cleanup was blocked");
        });
        check("A forward calendar jump preserves both old and recent histories before deletion", () =>
        {
            var (s, set) = Fixture(); string old = FileAt(s, -120), recent = FileAt(s, -1); set(200 * 86400, 2); s.Cleanup();
            assert(s.RetentionProtected && File.Exists(old) && File.Exists(recent) && s.RetentionNotice.Contains("日期"), "clock jump deleted evidence");
            set(0, 3); var review = s.PreviewRetention(); s.EnableRetention(review);
            assert(!File.Exists(old) && File.Exists(recent) && !s.RetentionProtected, "review after correcting the date did not preserve recent evidence");
        });
        check("Long absence backward dates and corrupt checkpoint state suspend startup cleanup", () =>
        {
            foreach (int days in new[] { -1, 2, 365 })
            {
                var (s, _) = Fixture(); string old = FileAt(s, -120); var reopened = new LocalStore(s.Root, () => At(days * 86400, days * 86400));
                assert(reopened.RetentionProtected && File.Exists(old), "ambiguous startup date cleaned history");
            }
            foreach (string payload in new[] { "{broken", "{\"Version\":\"future\",\"Date\":\"2026-01-01\"}", "{\"Version\":\"retention-1.0\",\"Date\":null}", "{\"Version\":\"retention-1.0\",\"Version\":\"retention-1.0\",\"Date\":\"2026-01-01\"}" })
            {
                var (s, _) = Fixture(); string old = FileAt(s, -120), state = Path.Combine(s.Root, "retention-state.json"); File.WriteAllText(state, payload);
                var reopened = new LocalStore(s.Root); assert(reopened.RetentionProtected && File.Exists(old) && File.ReadAllText(state) == payload, "bad checkpoint was trusted or overwritten");
            }
        });
        check("Legacy directories with already expired records require one explicit cleanup review", () =>
        {
            var (s, _) = Fixture(); string old = FileAt(s, -120); File.Delete(Path.Combine(s.Root, "retention-state.json"));
            var reopened = new LocalStore(s.Root); assert(reopened.RetentionProtected && File.Exists(old), "upgrade silently deleted unreviewed legacy history");
            reopened.EnableRetention(reopened.PreviewRetention()); assert(!File.Exists(old) && !reopened.RetentionProtected, "legacy review did not establish a checkpoint");
        });
        check("Changed file content even at the same length invalidates a reviewed cleanup before any deletion", () =>
        {
            var (s, _) = Fixture(); string a = FileAt(s, -121), b = FileAt(s, -120); Protect(s); var preview = s.PreviewRetention();
            var bytes = File.ReadAllBytes(b); bytes[0] ^= 1; File.WriteAllBytes(b, bytes);
            Reject(() => s.EnableRetention(preview)); assert(File.Exists(a) && File.ReadAllBytes(b).SequenceEqual(bytes) && s.RetentionProtected, "changed content or an earlier valid file was deleted");
        });
        check("Added files changed policy changed date and changed protection all require a fresh preview", () =>
        {
            foreach (string change in new[] { "file", "policy", "date", "protection" })
            {
                var (s, set) = Fixture(); string old = FileAt(s, -120); Protect(s); var preview = s.PreviewRetention();
                if (change == "file") FileAt(s, -122);
                if (change == "policy") s.SaveSettings(s.Settings with { RetentionDays = 30 });
                if (change == "date") set(86400, 86400);
                if (change == "protection") File.WriteAllText(Path.Combine(s.Root, "restore-protection.json"), "new protection");
                Reject(() => s.EnableRetention(preview)); assert(File.Exists(old) && s.RetentionProtected, "stale confirmation removed history or protection: " + change);
            }
        });
        check("A real locked later file prevents deletion of earlier reviewed files and permits retry", () =>
        {
            var (s, _) = Fixture(); string a = FileAt(s, -121), b = FileAt(s, -120); Protect(s); var preview = s.PreviewRetention();
            using (var locked = new FileStream(b, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Reject(() => s.EnableRetention(preview));
            assert(File.Exists(a) && File.Exists(b) && s.RetentionProtected, "partial deletion preceded all-file lock validation");
            s.EnableRetention(s.PreviewRetention()); assert(!File.Exists(a) && !File.Exists(b) && !s.RetentionProtected, "retry failed after releasing file lock");
        });
        check("A failed checkpoint write cannot start deletion or release protection", () =>
        {
            var (s, _) = Fixture(); string old = FileAt(s, -120); Protect(s); var preview = s.PreviewRetention();
            using (var locked = new FileStream(Path.Combine(s.Root, "retention-state.json"), FileMode.Open, FileAccess.Read, FileShare.Read)) Reject(() => s.EnableRetention(preview));
            assert(File.Exists(old) && s.RetentionProtected, "checkpoint write failure deleted data");
        });
        check("Cancellation before deletion retains all files and cleanup protection", () =>
        {
            var (s, _) = Fixture(); string old = FileAt(s, -120); Protect(s); var preview = s.PreviewRetention(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Reject(() => s.EnableRetention(preview, cancel.Token)); assert(File.Exists(old) && s.RetentionProtected, "cancelled validation deleted history");
        });
        check("Reviewed native file handles block writes and renames and delete the exact opened file", () =>
        {
            var (s, _) = Fixture(); string old = FileAt(s, -120);
            using (var lease = new RetentionFileLease(old))
            {
                Reject(() => File.AppendAllText(old, "changed")); Reject(() => File.Move(old, old + ".moved"));
                assert(lease.Describe(Path.GetFileName(old), default).Length > 0, "locked original could not be verified"); lease.Delete();
            }
            assert(!File.Exists(old) && !File.Exists(old + ".moved"), "handle disposition did not delete exactly the opened file");
        });
        check("A cleanup preview cannot be used for a different data directory", () =>
        {
            var (a, _) = Fixture(); var (b, _) = Fixture(); string oldA = FileAt(a, -120), oldB = FileAt(b, -120); Protect(a); Protect(b);
            Reject(() => b.EnableRetention(a.PreviewRetention())); assert(File.Exists(oldA) && File.Exists(oldB) && b.RetentionProtected, "review crossed directory boundaries");
        });
        check("Clock protection and its reason survive reopening without forcing a recording pause", () =>
        {
            var (s, set) = Fixture(); string old = FileAt(s, -120); set(10, 2);
            assert(s.ObserveRetentionTime(), "clock transition was not observed before scheduled cleanup");
            var reopened = new LocalStore(s.Root, () => At(0, 0));
            assert(reopened.RetentionProtected && reopened.RetentionNotice.Contains("日期") && !reopened.RequiresPausedStart && File.Exists(old), "protection was lost or incorrectly paused monitoring");
        });
        check("Corrupt retention checkpoint bytes are preserved before an explicit review repairs the checkpoint", () =>
        {
            var (s, _) = Fixture(); string old = FileAt(s, -120); byte[] original = [255, 0, 123]; File.WriteAllBytes(Path.Combine(s.Root, "retention-state.json"), original);
            var reopened = new LocalStore(s.Root, () => At(0, 0)); reopened.EnableRetention(reopened.PreviewRetention());
            string saved = Directory.GetFiles(Path.Combine(s.Root, "recovery"), "*-retention-state.json").Single();
            assert(File.ReadAllBytes(saved).SequenceEqual(original) && !File.Exists(old) && !reopened.RetentionProtected, "review lost original corrupt metadata or failed to resume");
        });
        check("Time-zone changes and a later read-only file cannot silently advance automatic deletion", () =>
        {
            var instant = At(0, 0); var s = new LocalStore(Path.Combine(Path.GetTempPath(), "PerfCompass-retention-zone-" + Guid.NewGuid().ToString("N")), () => instant);
            string a = FileAt(s, -121), b = FileAt(s, -120); instant = At(2, 2) with { Time = origin.AddSeconds(2).ToOffset(TimeSpan.FromHours(-7)) }; s.Cleanup();
            assert(s.RetentionProtected && File.Exists(a) && File.Exists(b), "offset change was silently trusted");
            instant = At(3, 3); var preview = s.PreviewRetention(); File.SetAttributes(b, FileAttributes.ReadOnly);
            try { Reject(() => s.EnableRetention(preview)); assert(File.Exists(a) && File.Exists(b) && s.RetentionProtected, "read-only later file caused partial deletion"); }
            finally { File.SetAttributes(b, FileAttributes.Normal); }
        });
        check("Automatic cleanup failure preserves files and monitoring can still open its data store", () =>
        {
            var (s, _) = Fixture(); string old = FileAt(s, -120); File.SetAttributes(old, FileAttributes.ReadOnly);
            try
            {
                var reopened = new LocalStore(s.Root, () => At(0, 0));
                assert(reopened.RetentionProtected && !reopened.RequiresPausedStart && File.Exists(old), "maintenance failure blocked monitoring or deleted history");
            }
            finally { File.SetAttributes(old, FileAttributes.Normal); }
        });
        check("A sub-tolerance clock step across midnight still requires a date review", () =>
        {
            var late = new DateTimeOffset(origin.Date.AddHours(23).AddMinutes(59).AddSeconds(59), origin.Offset);
            var instant = new SamplingInstant(late, 0); var s = new LocalStore(Path.Combine(Path.GetTempPath(), "PerfCompass-retention-midnight-" + Guid.NewGuid().ToString("N")), () => instant);
            string old = FileAt(s, -120); instant = new(late.AddSeconds(2), Stopwatch.Frequency / 10); s.Cleanup();
            assert(s.RetentionProtected && File.Exists(old), "a short forward step advanced the deletion date across midnight");
        });
    }
}
