using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace PerfCompass;

public record RetentionFile(string Name, long Length, string Sha256);
public record RetentionPreview(string Root, string Date, int Days, string ProtectionStamp, RetentionFile[] Files);

/// <summary>The validated file handle stays open until disposition is set; paths are not reopened for deletion.</summary>
internal sealed class RetentionFileLease : IDisposable
{
    private readonly SafeFileHandle handle;
    private readonly FileStream stream;
    internal RetentionFileLease(string path)
    {
        DataBackup.NoLinks(path);
        if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) throw new IOException("到期文件为只读，未开始清理：" + Path.GetFileName(path));
        handle = CreateFileW(path, 0x80010000, 1, IntPtr.Zero, 3, 0x80, IntPtr.Zero); // GENERIC_READ | DELETE; FILE_SHARE_READ; OPEN_EXISTING
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException("无法锁定到期文件：" + Path.GetFileName(path), new Win32Exception(error)); }
        try { stream = new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }
    internal RetentionFile Describe(string name, CancellationToken cancellation)
    {
        stream.Position = 0; using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536]; int read;
        while ((read = stream.Read(buffer)) > 0) { cancellation.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
        return new(name, stream.Length, Convert.ToHexString(hash.GetHashAndReset()));
    }
    internal void Delete()
    {
        byte disposition = 1; // FILE_DISPOSITION_INFO.DeleteFile is BOOLEAN, one byte.
        if (!SetFileInformationByHandle(handle, 4, ref disposition, 1)) throw new IOException("未能清理已核对文件。", new Win32Exception(Marshal.GetLastWin32Error()));
    }
    public void Dispose() { stream.Dispose(); handle.Dispose(); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref byte info, uint size);
}
