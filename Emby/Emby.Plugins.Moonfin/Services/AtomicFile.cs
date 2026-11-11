using System;
using System.IO;
using System.Text;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Crash-safe replacements for File.ReadAllText and File.WriteAllText.
    ///
    /// Writing in place leaves a truncated file if the process dies partway through, which is what
    /// corrupts stored user data. These helpers write to a temp file, flush it to disk, then rename
    /// it over the target, keeping the previous contents as a .bak that reads can fall back to.
    ///
    /// Callers already hold their service's lock, so nothing here takes one. File.Replace is used
    /// for the rename because the File.Move overwrite overload isn't available on netstandard2.1.
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

            if (!File.Exists(path))
            {
                File.Move(tmp, path);
                return;
            }

            try
            {
                File.Replace(tmp, path, BackupPath(path));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is PlatformNotSupportedException)
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
        /// invalid. Returns null when neither copy is usable. Parse should return null for content
        /// it rejects, which is how a half-written file gets caught.
        /// </summary>
        public static T? ReadWithRecovery<T>(string path, Func<string, T?> parse)
            where T : class
        {
            Exception? ignored;
            return ReadWithRecovery(path, parse, out ignored);
        }

        /// <summary>
        /// Reads and parses path with backup recovery, reporting any underlying file or directory
        /// access exception when neither copy can be read.
        /// </summary>
        public static T? ReadWithRecovery<T>(string path, Func<string, T?> parse, out Exception? accessError)
            where T : class
        {
            T? fromMain;
            Exception? mainError;
            if (TryReadAndParse(path, parse, out fromMain, out mainError))
            {
                accessError = null;
                return fromMain;
            }

            var backup = BackupPath(path);
            T? fromBackup;
            Exception? backupError;
            if (TryReadAndParse(backup, parse, out fromBackup, out backupError))
            {
                accessError = null;
                if (mainError == null)
                {
                    TryPromoteBackup(path, backup);
                }

                return fromBackup;
            }

            accessError = mainError ?? backupError;
            return null;
        }

        /// <summary>Deletes a file along with its .bak and .tmp copies.</summary>
        public static void DeleteWithSidecars(string path)
        {
            TryDelete(path);
            TryDelete(BackupPath(path));
            TryDelete(TempPath(path));
        }

        private static bool TryReadAndParse<T>(
            string path,
            Func<string, T?> parse,
            out T? result,
            out Exception? accessError)
            where T : class
        {
            result = null;
            accessError = null;

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException ex)
            {
                Exception? dirError;
                if (IsInaccessibleParentDirectory(path, ex, out dirError))
                {
                    accessError = dirError;
                }

                return false;
            }
            catch (Exception ex)
            {
                accessError = ex;
                return false;
            }

            try
            {
                result = parse(text);
                return result != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsInaccessibleParentDirectory(
            string path,
            DirectoryNotFoundException ex,
            out Exception? dirError)
        {
            dirError = null;
            try
            {
                var current = Path.GetDirectoryName(path);
                while (!string.IsNullOrEmpty(current))
                {
                    if (File.Exists(current))
                    {
                        dirError = ex;
                        return true;
                    }

                    if (Directory.Exists(current))
                    {
                        using (var enumerator = Directory.EnumerateFileSystemEntries(current).GetEnumerator())
                        {
                            _ = enumerator.MoveNext();
                        }

                        return false;
                    }

                    current = Path.GetDirectoryName(current);
                }
            }
            catch (Exception probeEx)
            {
                dirError = probeEx;
                return true;
            }

            return false;
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
}
