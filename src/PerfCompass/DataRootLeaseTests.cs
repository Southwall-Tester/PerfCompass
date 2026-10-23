using System.Diagnostics;
using System.IO;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunDataRootLeaseTests(Action<string, Action> check, Action<bool, string> assert)
    {
        string Root() => Path.Combine(Path.GetTempPath(), "PerfCompass lease 路径 " + Guid.NewGuid().ToString("N"));
        int Probe(string root)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--check-instance"); start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(root);
            using var process = Process.Start(start)!;
            if (!process.WaitForExit(15000)) { process.Kill(); throw new TimeoutException("Instance probe did not exit."); }
            return process.ExitCode;
        }
        check("Actual child instances cannot share a data root through equivalent path spellings", () =>
        {
            string root = Root(); using var lease = DataRootLease.Acquire(root); assert(lease != null, "first lease failed");
            string marker = Path.Combine(root, "marker.txt"); File.WriteAllText(marker, "preserve exact directory contents");
            foreach (string variant in new[] { root, root + Path.DirectorySeparatorChar, Path.Combine(root, "."), Path.Combine(root, "absent", "..") })
                assert(Probe(variant) == 10, "equivalent path opened another writer");
            if (File.Exists(marker.ToUpperInvariant())) assert(Probe(root.ToUpperInvariant()) == 10, "case alias opened another writer");
            assert(File.ReadAllText(marker) == "preserve exact directory contents", "instance check changed business data");
        });
        check("Physical lock file blocks a child even without a named mutex", () =>
        {
            string root = Root(); Directory.CreateDirectory(root);
            using (var held = new FileStream(Path.Combine(root, DataRootLease.LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                assert(Probe(root) == 10, "exclusive file sharing was bypassed");
            assert(Probe(root) == 0, "released file handle stayed occupied");
        });
        check("Separate directories remain independent and a released lease can be acquired again", () =>
        {
            string a = Root(), b = Root(); using (var first = DataRootLease.Acquire(a))
            using (var second = DataRootLease.Acquire(b))
                assert(first != null && second != null && Probe(a) == 10 && Probe(b) == 10, "independent roots blocked each other or were unprotected");
            assert(Probe(a) == 0 && Probe(b) == 0, "lease handles leaked after disposal");
        });
        check("An existing legacy mutex still prevents ordinary-path upgrades from opening the data", () =>
        {
            string root = Root(); Directory.CreateDirectory(root);
            using var legacy = new Mutex(false, DataRootLease.LegacyName(root), out bool created);
            assert(created && Probe(root) == 10, "legacy root mutex ignored");
            assert(!File.Exists(Path.Combine(root, DataRootLease.LockFileName)), "busy legacy probe opened the lock file");
        });
    }
}
