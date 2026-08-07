using System.Text;

namespace Moonfin.Server.Services;

/// <summary>
/// Crash-safe replacements for File.ReadAllText and File.WriteAllText.
///
/// Writing in place leaves a truncated file if the process dies partway through, which is what
/// corrupts stored user data. These helpers write to a temp file, flush it to disk, then rename it
/// over the target, keeping the previous contents as a .bak that reads can fall back to.
///
/// Callers already hold their service's lock, so nothing here takes one.
/// </summary>
public static class AtomicFile
{
    // Matches what File.WriteAllText produces, so files written before this change stay readable.
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Path of the backup copy kept alongside a target file.</summary>
    public static string BackupPath(string path) => path + ".bak";

    private static string TempPath(string path) => path + ".tmp";

    /// <summary>
    /// Writes contents to path through a temp file and an atomic rename, so the target is never
    /// left partially written. The previous contents are kept as a .bak.
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        var tmp = TempPath(path);

        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(fs, Utf8NoBom))
        {
            writer.Write(contents);
            writer.Flush();
            fs.Flush(flushToDisk: true);
        }

        ReplaceTarget(tmp, path);
    }

    /// <summary>
    /// Same tmp/flush/rename discipline as <see cref="WriteAllText"/>, but the calling thread is
    /// returned to the pool while the I/O runs. Callers that write large documents while holding a
    /// lock need this: blocking here holds their lock on a thread parked in the kernel.
    /// </summary>
    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default)
    {
        var tmp = TempPath(path);
        var bytes = Utf8NoBom.GetBytes(contents);

        var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, useAsync: true);
        await using (fs.ConfigureAwait(false))
        {
            await fs.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);

            // FlushAsync only drains the managed buffer; the crash safety this type exists for
            // needs the real fsync, which has no async form. Push it off the calling thread so the
            // durability guarantee costs an I/O completion rather than a parked thread.
            await Task.Run(() => fs.Flush(flushToDisk: true), cancellationToken).ConfigureAwait(false);
        }

        await Task.Run(() => ReplaceTarget(tmp, path), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves an already-flushed temp file over its target, keeping the previous copy as a .bak.</summary>
    private static void ReplaceTarget(string tmp, string path)
    {
        if (!File.Exists(path))
        {
            File.Move(tmp, path);
            return;
        }

        try
        {
            File.Replace(tmp, path, BackupPath(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Some filesystems reject File.Replace, mainly SMB shares and a few container mounts.
            // The temp file is already flushed, so the worst case here is a briefly missing file
            // rather than a truncated one, and reads recover from the backup.
            try
            {
                File.Copy(path, BackupPath(path), overwrite: true);
            }
            catch
            {
                // Losing the backup only weakens recovery, it doesn't corrupt anything.
            }

            File.Delete(path);
            File.Move(tmp, path);
        }
    }

    /// <summary>
    /// Reads and parses path, falling back to its .bak when the file is missing, unreadable or
    /// invalid. Returns null when neither copy is usable. Parse should return null for content it
    /// rejects, which is how a half-written file gets caught.
    /// </summary>
    public static T? ReadWithRecovery<T>(string path, Func<string, T?> parse)
        where T : class
    {
        if (TryReadAndParse(path, parse, out var fromMain))
        {
            return fromMain;
        }

        var backup = BackupPath(path);
        if (TryReadAndParse(backup, parse, out var fromBackup))
        {
            TryPromoteBackup(path, backup);
            return fromBackup;
        }

        return null;
    }

    /// <summary>Deletes a file along with its .bak and .tmp copies.</summary>
    public static void DeleteWithSidecars(string path)
    {
        TryDelete(path);
        TryDelete(BackupPath(path));
        TryDelete(TempPath(path));
    }

    private static bool TryReadAndParse<T>(string path, Func<string, T?> parse, out T? result)
        where T : class
    {
        result = null;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            result = parse(File.ReadAllText(path));
            return result != null;
        }
        catch
        {
            return false;
        }
    }

    // Restores the good copy so later reads hit the main file. Copying rather than renaming is
    // deliberate, it leaves the backup in place and a crash partway through is caught by the
    // next read.
    private static void TryPromoteBackup(string path, string backup)
    {
        try
        {
            File.Copy(backup, path, overwrite: true);
        }
        catch
        {
            // The recovered value is still returned even if the file can't be restored.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup only.
        }
    }
}
