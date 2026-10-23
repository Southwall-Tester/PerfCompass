using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PerfCompass;

public record BackupFile(string Path, long Length, string Sha256);
public record BackupManifest(string Version, DateTimeOffset Created, string AppVersion, List<BackupFile> Files);
public record BackupPreview(BackupManifest Manifest, string ManifestHash)
{
    public long TotalBytes => Manifest.Files.Sum(f => f.Length);
    public string Summary => $"备份时间：{Manifest.Created.LocalDateTime:yyyy-MM-dd HH:mm:ss}\n来源版本：{Manifest.AppVersion}\n{Manifest.Files.Count} 个数据文件 · {TotalBytes / 1048576d:0.00} MiB\n文件内容校验通过；校验值不证明来源身份。";
}

public static class DataBackup
{
    public const string Version = "perfcompass-backup-1.0";
    public const long MaximumBytes = 2L * 1024 * 1024 * 1024;
    public const int MaximumFiles = 10000;
    private const int MaximumManifestBytes = 4 * 1024 * 1024;
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value, string message) { if (!value) throw new InvalidDataException(message); }
    internal static bool Inside(string candidate, string parent) => Path.GetFullPath(candidate).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static void NoLinks(string path)
    {
        string? cursor = Path.GetFullPath(path);
        while (cursor != null)
        {
            // File.GetAttributes also detects an existing link whose target is unavailable.
            try { Require((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) == 0, "数据操作不支持链接或重解析路径，请选择普通本地目录。"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            cursor = Path.GetDirectoryName(cursor);
        }
    }
    private static bool Allowed(string path)
    {
        if (path.Length is < 1 or > 240 || path.Contains('\\') || path.Contains(':') || path.Contains('\0')) return false;
        string[] parts = path.Split('/');
        foreach (string part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            string device = part.Split('.')[0];
            if (Regex.IsMatch(device, @"^(CON|CONIN\$|CONOUT\$|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        }
        if (parts.Length == 1) return path.ToLowerInvariant() is "settings.json" or "settings.previous.json" or "settings-recovery-pending.json" or "comparison.json" or "retention-state.json"
            || Regex.IsMatch(path, @"^(samples-|experience-)[^/]+\.jsonl$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        return parts.Length == 2 && parts[0].ToLowerInvariant() is "measurements" or "recovery" && parts[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }
    private static IEnumerable<string> SourceFiles(string root)
    {
        NoLinks(root);
        foreach (var path in Directory.EnumerateFiles(root))
            if (Allowed(Path.GetFileName(path))) { NoLinks(path); yield return path; }
        foreach (string name in new[] { "measurements", "recovery" })
        {
            string folder = Path.Combine(root, name);
            NoLinks(folder);
            if (!Directory.Exists(folder)) continue;
            foreach (string path in Directory.EnumerateFiles(folder))
                if (Allowed(name + "/" + Path.GetFileName(path))) { NoLinks(path); yield return path; }
        }
    }
    private static (long Length, string Hash) Transfer(Stream source, Stream? destination, long maximum, CancellationToken cancellation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920]; long length = 0; int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancellation.ThrowIfCancellationRequested(); length = checked(length + read);
            Require(length <= maximum, "备份数据超过允许大小或文件长度与清单不一致。");
            hash.AppendData(buffer, 0, read); destination?.Write(buffer, 0, read);
        }
        cancellation.ThrowIfCancellationRequested(); return (length, Convert.ToHexString(hash.GetHashAndReset()));
    }
    public static BackupPreview Create(LocalStore store, string output, CancellationToken cancellation = default)
    {
        output = Path.GetFullPath(output); NoLinks(output);
        Require(output.EndsWith(".pcbackup", StringComparison.OrdinalIgnoreCase), "备份扩展名应为 .pcbackup。");
        Require(!Inside(output, store.Root), "备份应保存到当前数据目录以外。");
        string temp = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            store.WithDataLock(() =>
            {
                string[] files = SourceFiles(store.Root).Order(StringComparer.Ordinal).ToArray();
                Require(files.Length <= MaximumFiles, "数据文件数量超过 10000。");
                var entries = new List<BackupFile>(); long total = 0;
                using var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, true))
                {
                    foreach (string path in files)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        NoLinks(path);
                        string relative = Path.GetRelativePath(store.Root, path).Replace('\\', '/');
                        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
                        using var target = entry.Open();
                        var result = Transfer(source, target, MaximumBytes - total, cancellation); total += result.Length;
                        entries.Add(new(relative, result.Length, result.Hash));
                    }
                    var manifest = new BackupManifest(Version, DateTimeOffset.UtcNow, typeof(DataBackup).Assembly.GetName().Version?.ToString() ?? "unknown", entries);
                    byte[] json = JsonSerializer.SerializeToUtf8Bytes(manifest);
                    Require(json.Length <= MaximumManifestBytes, "备份清单过大。");
                    using var manifestStream = zip.CreateEntry("manifest.json").Open(); manifestStream.Write(json);
                }
                file.Flush(true); return true;
            });
            var preview = Inspect(temp, cancellation);
            cancellation.ThrowIfCancellationRequested(); NoLinks(output);
            File.Move(temp, output, true); return preview;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static BackupPreview ValidateArchive(ZipArchive zip, string? extractionRoot, CancellationToken cancellation)
    {
        Require(zip.Entries.Count is >= 1 and <= MaximumFiles + 1, "备份条目数量无效。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            Require(names.Add(entry.FullName), "备份有重复路径。");
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            Require(unixType is 0 or 0x8000 && (entry.ExternalAttributes & (int)(FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0, "备份包含目录、链接或特殊文件。");
            Require(entry.FullName == "manifest.json" || Allowed(entry.FullName), "备份包含不支持的路径或文件。");
            Require(entry.Length >= 0 && entry.Length <= MaximumBytes, "备份条目过大。");
        }
        var manifestEntry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("缺少备份清单。");
        Require(manifestEntry.Length <= MaximumManifestBytes, "备份清单过大。");
        byte[] json;
        using (var stream = manifestEntry.Open())
        using (var buffer = new MemoryStream())
        { Transfer(stream, buffer, MaximumManifestBytes, cancellation); json = buffer.ToArray(); }
        using (var document = JsonDocument.Parse(json))
        {
            void Unique(JsonElement value)
            {
                if (value.ValueKind == JsonValueKind.Object)
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in value.EnumerateObject()) { Require(seen.Add(p.Name), "备份清单存在重复字段。"); Unique(p.Value); }
                }
                else if (value.ValueKind == JsonValueKind.Array) foreach (var e in value.EnumerateArray()) Unique(e);
            }
            Unique(document.RootElement);
        }
        var manifest = JsonSerializer.Deserialize<BackupManifest>(json) ?? throw new InvalidDataException("备份清单为空。");
        Require(manifest.Version == Version, "备份版本不受支持。");
        Require(manifest.Files != null && manifest.Files.Count <= MaximumFiles && manifest.AppVersion != null && manifest.AppVersion.Length <= 100
            && manifest.Created != default, "备份清单字段无效。");
        Require(manifest.Files!.Count + 1 == zip.Entries.Count, "备份清单与文件数量不一致。");
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var item in manifest.Files)
        {
            cancellation.ThrowIfCancellationRequested();
            Require(item != null && item.Path != null && Allowed(item.Path) && listed.Add(item.Path) && item.Length >= 0
                && item.Length <= MaximumBytes && item.Sha256 != null && Regex.IsMatch(item.Sha256, "^[A-Fa-f0-9]{64}$"), "备份文件清单无效或重复。");
            total = checked(total + item!.Length); Require(total <= MaximumBytes, "备份解压总量超过 2 GiB。");
            var entry = zip.GetEntry(item.Path) ?? throw new InvalidDataException("备份缺少清单文件。");
            Require(entry.Length == item.Length, "备份文件长度不一致。");
            using var source = entry.Open();
            FileStream? target = null;
            try
            {
                if (extractionRoot != null)
                {
                    string path = Path.GetFullPath(Path.Combine(extractionRoot, item.Path.Replace('/', Path.DirectorySeparatorChar)));
                    Require(Inside(path, extractionRoot), "恢复路径越界。"); NoLinks(path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                }
                var actual = Transfer(source, target, item.Length, cancellation);
                Require(actual.Length == item.Length && string.Equals(actual.Hash, item.Sha256, StringComparison.OrdinalIgnoreCase), "备份文件内容校验失败。");
                target?.Flush(true);
            }
            finally { target?.Dispose(); }
        }
        return new(manifest, Hash(json));
    }
    public static BackupPreview Inspect(string path, CancellationToken cancellation = default)
    {
        NoLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(file.Length <= MaximumBytes + 64L * 1024 * 1024, "备份压缩文件过大。");
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        return ValidateArchive(zip, null, cancellation);
    }
    public static string Restore(string archive, string parent, string expectedManifestHash, CancellationToken cancellation = default)
    {
        parent = Path.GetFullPath(parent); NoLinks(parent); NoLinks(archive);
        Require(Directory.Exists(parent), "请选择已存在的恢复父目录。");
        string stage = Path.Combine(parent, ".PerfCompass-restore-" + Guid.NewGuid().ToString("N"));
        string final = Path.Combine(parent, $"PerfCompass-恢复-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}");
        Require(Inside(stage, parent) && Inside(final, parent), "恢复目录越界。");
        Directory.CreateDirectory(stage);
        try
        {
            BackupPreview preview;
            using (var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Require(file.Length <= MaximumBytes + 64L * 1024 * 1024, "备份压缩文件过大。");
                using var zip = new ZipArchive(file, ZipArchiveMode.Read);
                // Validate again on this open handle before creating any restored data files.
                preview = ValidateArchive(zip, null, cancellation);
                Require(preview.ManifestHash == expectedManifestHash, "备份在预览后改变，请重新选择并校验。");
                _ = ValidateArchive(zip, stage, cancellation);
            }
            SettingsFile.AtomicWrite(Path.Combine(stage, "restore-protection.json"), JsonSerializer.SerializeToUtf8Bytes(new { Version, Restored = DateTimeOffset.UtcNow, preview.ManifestHash, BackupCreated = preview.Manifest.Created }));
            cancellation.ThrowIfCancellationRequested(); NoLinks(stage); NoLinks(final);
            Require(Inside(stage, parent) && Inside(final, parent) && !Directory.Exists(final) && !File.Exists(final), "恢复目标已存在或越界。");
            Directory.Move(stage, final); return final;
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                // This directory was uniquely created above; never delete a computed user target or follow links.
                NoLinks(stage);
                void CheckTree(string directory)
                {
                    foreach (string p in Directory.EnumerateFileSystemEntries(directory))
                    { NoLinks(p); if (Directory.Exists(p)) CheckTree(p); }
                }
                CheckTree(stage);
                Require(Inside(stage, parent), "临时恢复目录越界。"); Directory.Delete(stage, true);
            }
        }
    }
}
