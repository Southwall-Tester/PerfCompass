using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PerfCompass;

/// <summary>Lifetime handles prevent two current app instances from writing one physical data directory.</summary>
internal sealed class DataRootLease : IDisposable
{
    internal const string LockFileName = ".perfcompass-instance.lock";
    private readonly List<Mutex> names = [];
    private FileStream? file;
    internal static string LegacyName(string root) => "Local\\PerfCompass-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..16];
    internal static DataRootLease? Acquire(string root)
    {
        string full = Path.GetFullPath(root), trimmed = Path.TrimEndingDirectorySeparator(full);
        var lease = new DataRootLease();
        try
        {
            Directory.CreateDirectory(trimmed);
            // Keep the former names for ordinary upgrades. The file handle, rather than
            // case folding a path, decides whether aliases refer to the same directory.
            foreach (string name in new[] { full, trimmed, Path.EndsInDirectorySeparator(trimmed) ? trimmed : trimmed + Path.DirectorySeparatorChar }.Select(LegacyName).Distinct())
            {
                var mutex = new Mutex(false, name, out bool first); lease.names.Add(mutex);
                if (!first) { lease.Dispose(); return null; }
            }
            try { lease.file = new FileStream(Path.Combine(trimmed, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { lease.Dispose(); return null; }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }
    public void Dispose()
    {
        file?.Dispose(); file = null;
        foreach (var mutex in names) mutex.Dispose(); names.Clear();
    }
}
