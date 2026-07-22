// Mirrored from Jellyfin/backend/Services/FileHealer.cs; keep in sync.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Plugins.Moonfin.Services
{

    /// <summary>
    /// Outcome counters for one healing sweep. Notes carry file names and outcomes only, never
    /// file contents, since settings files can hold API keys.
    /// </summary>
    public sealed class HealSummary
    {
        public int Scanned;
        public int Healthy;
        public int RecoveredFromBackup;
        public int Salvaged;
        public int Quarantined;
        public int TmpPromoted;
        public int Errors;
        public List<string> Notes { get; } = new();
    }

    /// <summary>
    /// One-time healing sweep over a directory of per-user JSON files damaged by the in-place
    /// writes that shipped before AtomicFile. Healthy files are left untouched. Corrupt files are
    /// recovered from their backup, repaired through JsonSalvage, or moved to a quarantine folder,
    /// never deleted, so support can still recover them by hand.
    ///
    /// Logger-agnostic on purpose: the Jellyfin and Emby hosts log through different interfaces,
    /// so callers read the returned summary and log it themselves.
    /// </summary>
    public static class FileHealer
    {
        // Settings files are a few KB. Anything this large is not a settings file and goes
        // straight to quarantine without being read into memory.
        private const long MaxReadBytes = 16 * 1024 * 1024;

        private static int _running;

        /// <summary>Guards against the startup run and a manual dashboard run overlapping.</summary>
        public static bool TryBeginRun() => Interlocked.CompareExchange(ref _running, 1, 0) == 0;

        public static void EndRun() => Interlocked.Exchange(ref _running, 0);

        /// <summary>
        /// Heals every per-user JSON file in directory. gate is the owning service's lock and is
        /// held per file, so live traffic is never starved. isValidJson parses one file's text and
        /// reports whether it deserializes into an acceptable model. salvage is null for stores
        /// whose files are small and re-creatable, which quarantines instead of repairing.
        /// </summary>
        public static async Task<HealSummary> HealDirectoryAsync(
            string directory,
            string quarantineDir,
            SemaphoreSlim gate,
            Func<string, bool> isValidJson,
            Func<string, (bool Ok, string Healed)>? salvage,
            CancellationToken cancellationToken)
        {
            var summary = new HealSummary();
            if (!Directory.Exists(directory))
            {
                return summary;
            }

            // Snapshot the candidate main paths up front: every {guid}.json, plus mains implied
            // by orphaned sidecars whose main file is already gone.
            var mains = new SortedSet<string>(StringComparer.Ordinal);
            CollectMains(directory, "*.json", string.Empty, mains);
            CollectMains(directory, "*.json.bak", ".bak", mains);
            CollectMains(directory, "*.json.tmp", ".tmp", mains);

            foreach (var path in mains)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    summary.Scanned++;
                    HealOne(path, quarantineDir, isValidJson, salvage, summary);
                }
                catch (Exception ex)
                {
                    summary.Errors++;
                    summary.Notes.Add($"{Path.GetFileName(path)}: error {ex.GetType().Name}");
                }
                finally
                {
                    gate.Release();
                }
            }

            return summary;
        }

        // The decision ladder for one user's file and its sidecars. Runs under the store's lock,
        // and re-checks the disk state itself because the file may have changed between the
        // snapshot and the lock.
        private static void HealOne(
            string path,
            string quarantineDir,
            Func<string, bool> isValidJson,
            Func<string, (bool Ok, string Healed)>? salvage,
            HealSummary summary)
        {
            var name = Path.GetFileName(path);
            var backup = path + ".bak";
            var tmp = path + ".tmp";

            var mainText = TryRead(path);
            if (mainText != null && isValidJson(mainText))
            {
                summary.Healthy++;

                // A tmp beside a healthy main is a dead leftover from a crashed write. Its
                // vintage is unknowable, so park it rather than promote it over good data.
                if (File.Exists(tmp))
                {
                    TryQuarantine(tmp, quarantineDir, summary, $"{name}: stray tmp quarantined");
                }

                return;
            }

            var backupText = TryRead(backup);
            if (backupText != null && isValidJson(backupText))
            {
                // Keep the corrupt bytes reachable for support before overwriting anything.
                if (mainText != null)
                {
                    TryQuarantineCopy(path, quarantineDir, "pre-recovery", summary);
                }

                AtomicFile.WriteAllText(path, backupText);
                summary.RecoveredFromBackup++;
                summary.Notes.Add($"{name}: recovered from backup");
                return;
            }

            if (salvage != null)
            {
                foreach (var source in new[] { mainText, backupText })
                {
                    if (source == null)
                    {
                        continue;
                    }

                    var (ok, healed) = salvage(source);
                    if (!ok)
                    {
                        continue;
                    }

                    TryQuarantineCopy(path, quarantineDir, "pre-salvage", summary);
                    AtomicFile.WriteAllText(path, healed);
                    summary.Salvaged++;
                    summary.Notes.Add($"{name}: salvaged");
                    return;
                }
            }

            // The write path deletes the main before renaming the tmp on filesystems that reject
            // File.Replace, so a crash there leaves only a complete tmp behind.
            if (mainText == null && !File.Exists(path))
            {
                var tmpText = TryRead(tmp);
                if (tmpText != null && isValidJson(tmpText))
                {
                    File.Move(tmp, path);
                    summary.TmpPromoted++;
                    summary.Notes.Add($"{name}: promoted tmp");
                    return;
                }
            }

            // Nothing on disk for this user is usable. Move the whole set aside so the next save
            // starts clean and a corrupt backup can't resurrect through ReadWithRecovery later.
            var moved = false;
            foreach (var sidecar in new[] { path, backup, tmp })
            {
                if (File.Exists(sidecar))
                {
                    TryQuarantine(sidecar, quarantineDir, summary, null);
                    moved = true;
                }
            }

            if (moved)
            {
                summary.Quarantined++;
                summary.Notes.Add($"{name}: quarantined");
            }
            else
            {
                // Snapshot raced a delete, so nothing is left to heal.
                summary.Scanned--;
            }
        }

        private static void CollectMains(string directory, string pattern, string suffix, SortedSet<string> mains)
        {
            foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
            {
                var main = suffix.Length > 0 && file.EndsWith(suffix, StringComparison.Ordinal)
                    ? file.Substring(0, file.Length - suffix.Length)
                    : file;

                // Only per-user files: the stem must be a user id. This also skips whatever else
                // lives in the folder and anything a broad glob matched by accident.
                if (!main.EndsWith(".json", StringComparison.Ordinal))
                {
                    continue;
                }

                var stem = Path.GetFileNameWithoutExtension(main);
                if (Guid.TryParse(stem, out _))
                {
                    mains.Add(main);
                }
            }
        }

        private static string? TryRead(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxReadBytes)
                {
                    return null;
                }

                return File.ReadAllText(path);
            }
            catch
            {
                return null;
            }
        }

        private static void TryQuarantine(string path, string quarantineDir, HealSummary summary, string? note)
        {
            try
            {
                File.Move(path, QuarantineTarget(path, quarantineDir));
                if (note != null)
                {
                    summary.Notes.Add(note);
                }
            }
            catch (Exception ex)
            {
                summary.Errors++;
                summary.Notes.Add($"{Path.GetFileName(path)}: quarantine failed {ex.GetType().Name}");
            }
        }

        // Copies instead of moving, for the salvage and backup-recovery paths where the original
        // slot is about to be rewritten with repaired contents.
        private static void TryQuarantineCopy(string path, string quarantineDir, string tag, HealSummary summary)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Copy(path, QuarantineTarget($"{path}.{tag}", quarantineDir));
                }
            }
            catch (Exception ex)
            {
                summary.Errors++;
                summary.Notes.Add($"{Path.GetFileName(path)}: quarantine copy failed {ex.GetType().Name}");
            }
        }

        private static string QuarantineTarget(string path, string quarantineDir)
        {
            Directory.CreateDirectory(quarantineDir);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
            var target = Path.Combine(quarantineDir, $"{Path.GetFileName(path)}.{stamp}");
            while (File.Exists(target))
            {
                target = Path.Combine(
                    quarantineDir,
                    $"{Path.GetFileName(path)}.{stamp}.{Guid.NewGuid().ToString("N").Substring(0, 4)}");
            }

            return target;
        }
    }
}
