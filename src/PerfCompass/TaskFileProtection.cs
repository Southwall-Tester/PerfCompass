using System.IO;
using System.Security.Cryptography;

namespace PerfCompass;

internal sealed class TaskFileProtectionException(Exception cause) : IOException(
    "程序或所选输入无法保持只读。请关闭正在写入它们的程序，将任务输出写入其他文件后重试；尚未启动任务。", cause);

internal static class TaskFileProtection
{
    // Keep both handles until all rounds and final hashes complete. This uses
    // Windows file-sharing checks, not file attributes or directory permissions.
    // It protects the selected files through ordinary file opens, not the files
    // merely named by an input manifest or writable mappings held elsewhere.
    internal static FileStream Open(string path)
    {
        try { return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new TaskFileProtectionException(error); }
    }

    internal static async Task<string> Hash(FileStream stream, CancellationToken cancellation)
    {
        stream.Position = 0;
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation).ConfigureAwait(false));
    }
}
