using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Cryptography;

namespace PerfCompass;

internal static class ValidationIdentity
{
    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "Bundled assemblies have an empty location; their executable hash is recorded instead.")]
    internal static string? ManagedAssemblyHash()
    {
        string path = typeof(App).Assembly.Location;
        if (path.Length == 0) return null;
        using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream));
    }
}
