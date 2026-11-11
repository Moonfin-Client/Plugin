using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moonfin.LiveTv;
using Moonfin.Server.Api;
using Moonfin.Server.Models;
using Moonfin.Server.Services;
using Moonfin.Upgrades;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class RecentRowsSettingsAndUpgradesTests
{
    private sealed class MemoryStateFile : IStateFile
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, string> Files => _files;

        public T? Read<T>(string path, Func<string, T?> parse) where T : class =>
            _files.TryGetValue(path, out var text) ? parse(text) : null;

        public void Write(string path, string content) => _files[path] = content;
    }

    private sealed class CapturingRecordingLog : IRecordingLog
    {
        public List<string> Warnings { get; } = new();

        public void Info(string template, params object?[] args)
        {
        }

        public void Warn(Exception? exception, string template, params object?[] args) =>
            Warnings.Add(template);

        public void Error(Exception? exception, string template, params object?[] args)
        {
        }
    }

    [Fact]
    public void ProfileSerializesHideWatchedAndHideUpgradesUnderClientWireKeys()
    {
        var profile = new MoonfinSettingsProfile
        {
            HideWatchedInRecentRows = true,
            HideUpgradesInRecentRows = true
        };

        var json = JsonSerializer.Serialize(profile);
        Assert.Contains("\"hideWatchedInRecentRows\":true", json);
        Assert.Contains("\"hideUpgradesInRecentRows\":true", json);

        var roundTripped = JsonSerializer.Deserialize<MoonfinSettingsProfile>(json)!;
        Assert.True(roundTripped.HideWatchedInRecentRows);
        Assert.True(roundTripped.HideUpgradesInRecentRows);
    }

    [Fact]
    public void MovieUpgradePreservesFirstAddedWhenEnabledAndRestoresLatestWhenDisabled()
    {
        var stateFile = new MemoryStateFile();
        var ledger = new MediaUpgradeLedger("upgrade-dates.json", stateFile);
        var originalId = Guid.NewGuid();
        var originalDate = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc);

        ledger.Seed([
            new MediaUpgradeCandidate
            {
                ItemId = originalId,
                IsEpisode = false,
                TmdbId = "27205",
                ImdbId = "tt1375666",
                Name = "Inception",
                ProductionYear = 2010,
                DateCreatedUtc = originalDate
            },
            new MediaUpgradeCandidate
            {
                ItemId = Guid.NewGuid(),
                IsEpisode = false,
                TmdbId = "603",
                Name = "The Matrix",
                ProductionYear = 1999,
                DateCreatedUtc = originalDate
            }
        ]);

        // Baseline seeding stays in RAM only — nothing written to disk until an upgrade/removal occurs.
        Assert.Empty(stateFile.Files);

        // Simulate Radarr deleting the old 1080p file and importing a 2160p replacement with a new ItemId.
        ledger.RecordRemoved(new MediaUpgradeCandidate
        {
            ItemId = originalId,
            IsEpisode = false,
            TmdbId = "27205",
            ImdbId = "tt1375666",
            DateCreatedUtc = originalDate
        });

        var replacementId = Guid.NewGuid();
        var upgradeDate = new DateTime(2026, 5, 20, 18, 30, 0, DateTimeKind.Utc);
        var isUpgrade = ledger.ObserveArrival(
            new MediaUpgradeCandidate
            {
                ItemId = replacementId,
                IsEpisode = false,
                TmdbId = "27205",
                ImdbId = "tt1375666",
                Name = "Inception",
                ProductionYear = 2010,
                DateCreatedUtc = upgradeDate
            },
            hideUpgradesEnabled: true,
            out var desiredDateUtc);

        Assert.True(isUpgrade);
        Assert.Equal(originalDate, desiredDateUtc);

        // Toggling Hide Upgrades OFF restores LatestAddedUtc; toggling ON restores FirstAddedUtc.
        var whenDisabled = Assert.Single(ledger.GetDesiredDatesForUpgradedItems(hideUpgradesEnabled: false));
        Assert.Equal(replacementId, whenDisabled.ItemId);
        Assert.Equal(upgradeDate, whenDisabled.DesiredDateUtc);

        var whenEnabled = Assert.Single(ledger.GetDesiredDatesForUpgradedItems(hideUpgradesEnabled: true));
        Assert.Equal(replacementId, whenEnabled.ItemId);
        Assert.Equal(originalDate, whenEnabled.DesiredDateUtc);
    }

    [Fact]
    public void EpisodeUpgradeMatchesBySeriesAndSeasonEpisodeNumbers()
    {
        var ledger = new MediaUpgradeLedger("upgrade-dates.json", new MemoryStateFile());
        var seriesId = Guid.NewGuid();
        var oldEpisodeId = Guid.NewGuid();
        var firstAdded = new DateTime(2023, 8, 1, 12, 0, 0, DateTimeKind.Utc);

        ledger.Seed([
            new MediaUpgradeCandidate
            {
                ItemId = oldEpisodeId,
                IsEpisode = true,
                TvdbId = "371980",
                SeriesId = seriesId,
                SeasonNumber = 1,
                EpisodeNumber = 1,
                DateCreatedUtc = firstAdded
            }
        ]);

        var newEpisodeId = Guid.NewGuid();
        var upgradeDate = new DateTime(2026, 6, 10, 9, 15, 0, DateTimeKind.Utc);

        // Even when Hide Upgrades is false at import time, the upgrade is recorded so the user can toggle it on later.
        var isUpgrade = ledger.ObserveArrival(
            new MediaUpgradeCandidate
            {
                ItemId = newEpisodeId,
                IsEpisode = true,
                SeriesId = seriesId,
                SeasonNumber = 1,
                EpisodeNumber = 1,
                DateCreatedUtc = upgradeDate
            },
            hideUpgradesEnabled: false,
            out var desiredWhenOff);

        Assert.True(isUpgrade);
        Assert.Null(desiredWhenOff);

        var desiredWhenTurnedOn = Assert.Single(ledger.GetDesiredDatesForUpgradedItems(hideUpgradesEnabled: true));
        Assert.Equal(newEpisodeId, desiredWhenTurnedOn.ItemId);
        Assert.Equal(firstAdded, desiredWhenTurnedOn.DesiredDateUtc);
    }

    [Fact]
    public void ReloadedLedgerRestoresPersistedUpgradeRecordsFromStateFile()
    {
        var stateFile = new MemoryStateFile();
        var ledger = new MediaUpgradeLedger("upgrade-dates.json", stateFile);
        var originalId = Guid.NewGuid();
        var replacementId = Guid.NewGuid();
        var originalDate = new DateTime(2024, 2, 10, 8, 0, 0, DateTimeKind.Utc);
        var upgradeDate = new DateTime(2026, 7, 1, 14, 0, 0, DateTimeKind.Utc);

        ledger.Seed([
            new MediaUpgradeCandidate
            {
                ItemId = originalId,
                IsEpisode = false,
                TmdbId = "157336",
                Name = "Interstellar",
                ProductionYear = 2014,
                DateCreatedUtc = originalDate
            }
        ]);

        ledger.ObserveArrival(
            new MediaUpgradeCandidate
            {
                ItemId = replacementId,
                IsEpisode = false,
                TmdbId = "157336",
                Name = "Interstellar",
                ProductionYear = 2014,
                DateCreatedUtc = upgradeDate
            },
            hideUpgradesEnabled: true,
            out _);

        var reloaded = new MediaUpgradeLedger("upgrade-dates.json", stateFile);
        var target = Assert.Single(reloaded.GetDesiredDatesForUpgradedItems(hideUpgradesEnabled: true));
        Assert.Equal(replacementId, target.ItemId);
        Assert.Equal(originalDate, target.DesiredDateUtc);
    }

    [Fact]
    public void ReloadedLedgerRecoversFromBackupWhenPrimaryFileIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "moonfin-ledger-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var stateFile = new JellyfinStateFile();
            var filePath = Path.Combine(tempRoot, "upgrade-dates.json");
            var ledger = new MediaUpgradeLedger(filePath, stateFile);
            var originalId = Guid.NewGuid();
            var replacementId = Guid.NewGuid();
            var originalDate = new DateTime(2024, 2, 10, 8, 0, 0, DateTimeKind.Utc);
            var upgradeDate = new DateTime(2026, 7, 1, 14, 0, 0, DateTimeKind.Utc);

            ledger.RecordRemoved(new MediaUpgradeCandidate
            {
                ItemId = originalId,
                IsEpisode = false,
                TmdbId = "157336",
                Name = "Interstellar",
                ProductionYear = 2014,
                DateCreatedUtc = originalDate
            });

            ledger.ObserveArrival(
                new MediaUpgradeCandidate
                {
                    ItemId = replacementId,
                    IsEpisode = false,
                    TmdbId = "157336",
                    Name = "Interstellar",
                    ProductionYear = 2014,
                    DateCreatedUtc = upgradeDate
                },
                hideUpgradesEnabled: true,
                out _);

            Assert.True(File.Exists(filePath));
            var backupPath = filePath + ".bak";
            File.Copy(filePath, backupPath, overwrite: true);
            Assert.True(File.Exists(backupPath));

            File.Delete(filePath);
            Assert.False(File.Exists(filePath));

            var reloaded = new MediaUpgradeLedger(filePath, stateFile);
            var target = Assert.Single(reloaded.GetDesiredDatesForUpgradedItems(hideUpgradesEnabled: true));
            Assert.Equal(replacementId, target.ItemId);
            Assert.Equal(originalDate, target.DesiredDateUtc);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void SeparateLiveCopiesSharingProviderIdsRemainDistinctAndNotMarkedAsUpgrades()
    {
        var ledger = new MediaUpgradeLedger("upgrade-dates.json", new MemoryStateFile());
        var movie1Id = Guid.NewGuid();
        var movie2Id = Guid.NewGuid();
        var date1 = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var date2 = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        var candidate1 = new MediaUpgradeCandidate
        {
            ItemId = movie1Id,
            IsEpisode = false,
            TmdbId = "550",
            Name = "Fight Club",
            ProductionYear = 1999,
            DateCreatedUtc = date1
        };

        var candidate2 = new MediaUpgradeCandidate
        {
            ItemId = movie2Id,
            IsEpisode = false,
            TmdbId = "550",
            Name = "Fight Club",
            ProductionYear = 1999,
            DateCreatedUtc = date2
        };

        // 1. First live copy arrives.
        var upgraded1 = ledger.ObserveArrival(candidate1, hideUpgradesEnabled: true, out var desiredUtc1, isItemLive: id => id == movie1Id);
        Assert.False(upgraded1);
        Assert.Null(desiredUtc1);

        // 2. Second live copy arrives (different ItemId, both active in library).
        var upgraded2 = ledger.ObserveArrival(candidate2, hideUpgradesEnabled: true, out var desiredUtc2, isItemLive: id => id == movie1Id);
        Assert.False(upgraded2);
        Assert.Null(desiredUtc2);

        // Neither copy should be marked upgraded.
        Assert.Empty(ledger.GetDesiredDatesForUpgradedItems(hideUpgradesEnabled: true));
        Assert.Equal(date1, ledger.EffectiveDateCreated(movie1Id, date1, hideUpgradesEnabled: true));
        Assert.Equal(date2, ledger.EffectiveDateCreated(movie2Id, date2, hideUpgradesEnabled: true));
    }

    [Fact]
    public void EnsureLoadedStaysSilentOnMissingFirstRunAndLogsOnceOnFailedRecovery()
    {
        var missingState = new MemoryStateFile();
        var missingLog = new CapturingRecordingLog();
        var firstRunLedger = new MediaUpgradeLedger("nonexistent-upgrade-dates.json", missingState, missingLog);

        Assert.Equal(0, firstRunLedger.Count);
        Assert.Empty(missingLog.Warnings);

        var corruptState = new MemoryStateFile();
        corruptState.Write("corrupt-upgrade-dates.json", "{not-valid-json");
        var corruptLog = new CapturingRecordingLog();
        var corruptLedger = new MediaUpgradeLedger("corrupt-upgrade-dates.json", corruptState, corruptLog);

        Assert.Equal(0, corruptLedger.Count);
        Assert.Equal(0, corruptLedger.Count);
        Assert.Single(corruptLog.Warnings);
    }

    [Fact]
    public void EnsureLoadedLogsOnceWhenStateFileAdapterEncountersAccessError()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "moonfin-ledger-access-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var stateFile = new JellyfinStateFile();

            // 1. Missing first-run path with real JellyfinStateFile stays silent.
            var missingPath = Path.Combine(tempRoot, "missing", "upgrade-dates.json");
            var missingLog = new CapturingRecordingLog();
            var missingLedger = new MediaUpgradeLedger(missingPath, stateFile, missingLog);
            Assert.Equal(0, missingLedger.Count);
            Assert.Empty(missingLog.Warnings);

            // 2. Inaccessible parent path where File.Exists returns false for both main and .bak logs once.
            var blockedParent = Path.Combine(tempRoot, "blocked-parent");
            File.WriteAllText(blockedParent, "not-a-directory");
            var blockedPath = Path.Combine(blockedParent, "upgrade-dates.json");
            Assert.False(File.Exists(blockedPath));
            Assert.False(File.Exists(blockedPath + ".bak"));

            var blockedLog = new CapturingRecordingLog();
            var blockedLedger = new MediaUpgradeLedger(blockedPath, stateFile, blockedLog);
            Assert.Equal(0, blockedLedger.Count);
            Assert.Equal(0, blockedLedger.Count);
            Assert.Single(blockedLog.Warnings);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ReadWithRecoveryAndStateFilePreserveAccessErrorWhenPrimaryFailsEvenIfBackupSucceeds()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "moonfin-atomic-access-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var path = Path.Combine(tempRoot, "upgrade-dates.json");
            var backupPath = AtomicFile.BackupPath(path);
            File.WriteAllText(path, "[{\"ItemType\":\"Movie\",\"Name\":\"New Movie\"}]");
            File.WriteAllText(backupPath, "[{\"ItemType\":\"Movie\",\"Name\":\"Old Movie\"}]");

            using (var lockStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                // Primary has access error, but backup is readable
                var recovered = AtomicFile.ReadWithRecovery<string>(path, s => s, out var accessError);
                Assert.Equal("[{\"ItemType\":\"Movie\",\"Name\":\"Old Movie\"}]", recovered);
                Assert.NotNull(accessError);
                Assert.IsAssignableFrom<IOException>(accessError);

                // Two-argument overload returns the backup
                Assert.Equal("[{\"ItemType\":\"Movie\",\"Name\":\"Old Movie\"}]", AtomicFile.ReadWithRecovery<string>(path, s => s));

                // State file adapter must reject that access error and throw
                var stateFile = new JellyfinStateFile();
                Assert.ThrowsAny<IOException>(() => stateFile.Read<List<MediaUpgradeRecord>>(path, json => new List<MediaUpgradeRecord>()));

                // MediaUpgradeLedger keeps ledger unavailable (Count is 0, warning logged)
                var log = new CapturingRecordingLog();
                var ledger = new MediaUpgradeLedger(path, stateFile, log);
                Assert.Equal(0, ledger.Count);
                Assert.Single(log.Warnings);
            }

            // Primary file was not overwritten by backup
            Assert.Contains("New Movie", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task OverlappingSavesReapplyFinalPreferenceAndStopCancelsWorker()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "moonfin-upgrade-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var settings = new MoonfinSettingsService(NullLogger<MoonfinSettingsService>.Instance, tempRoot);
            var userId = Guid.NewGuid();
            var appliedStates = new List<bool>();
            var falseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFalse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            settings.UpgradePreferenceChanged = async (enabled, cancellationToken) =>
            {
                if (!enabled && !falseStarted.Task.IsCompleted)
                {
                    falseStarted.TrySetResult();
                    await releaseFalse.Task.WaitAsync(cancellationToken);
                }

                lock (appliedStates)
                {
                    appliedStates.Add(enabled);
                }
            };

            // 1. Initial true save applies true.
            await settings.SaveProfileAsync(
                userId,
                "global",
                new MoonfinSettingsProfile { HideUpgradesInRecentRows = true });
            await settings.ScheduleUpgradePreferenceSyncIfNeeded(userId);

            // 2. Save false; wait until worker has read false and entered the callback (before updating _lastAppliedUpgradeState).
            await settings.SaveProfileAsync(
                userId,
                "global",
                new MoonfinSettingsProfile { HideUpgradesInRecentRows = false });
            await falseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // 3. While the false worker is still in flight, save true again.
            await settings.SaveProfileAsync(
                userId,
                "global",
                new MoonfinSettingsProfile { HideUpgradesInRecentRows = true });

            // 4. Release the false worker and wait for the drain loop to finish.
            releaseFalse.TrySetResult();
            await settings.ScheduleUpgradePreferenceSyncIfNeeded(userId).WaitAsync(TimeSpan.FromSeconds(5));

            lock (appliedStates)
            {
                Assert.Equal([true, false, true], appliedStates);
            }

            // 5. Verify StopUpgradePreferenceSyncAsync cancels and drains an in-flight worker.
            var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observedCancellation = false;
            settings.UpgradePreferenceChanged = async (_, cancellationToken) =>
            {
                stopStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    observedCancellation = true;
                    throw;
                }
            };

            await settings.SaveProfileAsync(
                userId,
                "global",
                new MoonfinSettingsProfile { HideUpgradesInRecentRows = false });
            await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await settings.StopUpgradePreferenceSyncAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(observedCancellation);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ScanPausedAcrossSaveDoesNotPublishStaleCacheValue()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "moonfin-scan-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var releaseScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var settings = new MoonfinSettingsService(NullLogger<MoonfinSettingsService>.Instance, tempRoot);
            var userId = Guid.NewGuid();
            var appliedStates = new List<bool>();
            settings.UpgradePreferenceChanged = (enabled, _) =>
            {
                lock (appliedStates)
                {
                    appliedStates.Add(enabled);
                }

                return Task.CompletedTask;
            };

            // Seed user file with HideUpgradesInRecentRows = false and invalidate the cache so the next read scans files.
            await settings.SaveProfileAsync(
                userId,
                "global",
                new MoonfinSettingsProfile { HideUpgradesInRecentRows = false });
            await settings.ScheduleUpgradePreferenceSyncIfNeeded(userId);
            settings.InvalidateHideUpgradesCache();

            var scanPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pauseOnce = 0;
            settings.BeforePublishHideUpgradesScanForTesting = () =>
            {
                if (Interlocked.CompareExchange(ref pauseOnce, 1, 0) == 0)
                {
                    scanPaused.TrySetResult();
                    releaseScan.Task.GetAwaiter().GetResult();
                }
            };

            // Start a background scan that reads the old false value and pauses before publishing to _userHideUpgradesCache.
            var scanTask = Task.Run(() => settings.IsAnyHideUpgradesEnabled());
            await scanPaused.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Save HideUpgradesInRecentRows = true while the stale scan is paused, then release the paused scan.
            var saveTask = settings.SaveProfileAsync(
                userId,
                "global",
                new MoonfinSettingsProfile { HideUpgradesInRecentRows = true });
            await saveTask.WaitAsync(TimeSpan.FromSeconds(5));
            releaseScan.TrySetResult();

            var scanResult = await scanTask.WaitAsync(TimeSpan.FromSeconds(5));
            await settings.ScheduleUpgradePreferenceSyncIfNeeded(userId).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(scanResult);
            Assert.True(settings.IsAnyHideUpgradesEnabled());
            lock (appliedStates)
            {
                Assert.Equal([false, true], appliedStates);
            }
        }
        finally
        {
            releaseScan.TrySetResult();
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void SeedAndObserveArrivalKeepDistinctLiveCopiesIndexedAndReplaceRemovedCopy()
    {
        var stateFile = new MemoryStateFile();
        var ledger = new MediaUpgradeLedger("upgrade-dates.json", stateFile);
        var copy1Id = Guid.NewGuid();
        var copy2Id = Guid.NewGuid();
        var replacementId = Guid.NewGuid();
        var copy1Date = new DateTime(2023, 1, 10, 8, 0, 0, DateTimeKind.Utc);
        var copy2Date = new DateTime(2024, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var upgradeDate = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

        ledger.Seed([
            new MediaUpgradeCandidate
            {
                ItemId = copy1Id,
                IsEpisode = false,
                TmdbId = "27205",
                DateCreatedUtc = copy1Date
            },
            new MediaUpgradeCandidate
            {
                ItemId = copy2Id,
                IsEpisode = false,
                TmdbId = "27205",
                DateCreatedUtc = copy2Date
            }
        ]);

        // Both live copies remain tracked distinctly.
        Assert.Equal(2, ledger.Count);

        ledger.RecordRemoved(new MediaUpgradeCandidate
        {
            ItemId = copy1Id,
            IsEpisode = false,
            TmdbId = "27205",
            DateCreatedUtc = copy1Date
        });

        var isUpgrade = ledger.ObserveArrival(
            new MediaUpgradeCandidate
            {
                ItemId = replacementId,
                IsEpisode = false,
                TmdbId = "27205",
                DateCreatedUtc = upgradeDate
            },
            hideUpgradesEnabled: true,
            out var desiredDateUtc,
            isItemLive: id => id == copy2Id || id == replacementId);

        Assert.True(isUpgrade);
        Assert.Equal(copy1Date, desiredDateUtc);
        Assert.Equal(2, ledger.Count);
        Assert.Equal(copy2Date, ledger.EffectiveDateCreated(copy2Id, copy2Date, hideUpgradesEnabled: true));
    }

    [Fact]
    public void StartupSeedReconcilesRemovedRecordToReplacementAndClassifiesUpgrade()
    {
        var stateFile = new MemoryStateFile();
        var ledger = new MediaUpgradeLedger("upgrade-dates.json", stateFile);
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        var originalDate = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var upgradeDate = new DateTime(2026, 4, 15, 10, 0, 0, DateTimeKind.Utc);

        ledger.RecordRemoved(new MediaUpgradeCandidate
        {
            ItemId = oldId,
            IsEpisode = false,
            TmdbId = "603",
            DateCreatedUtc = originalDate
        });

        var reloaded = new MediaUpgradeLedger("upgrade-dates.json", stateFile);
        reloaded.Seed([
            new MediaUpgradeCandidate
            {
                ItemId = newId,
                IsEpisode = false,
                TmdbId = "603",
                DateCreatedUtc = upgradeDate
            }
        ]);

        var target = Assert.Single(reloaded.GetDesiredDatesForUpgradedItems(hideUpgradesEnabled: true));
        Assert.Equal(newId, target.ItemId);
        Assert.Equal(originalDate, target.DesiredDateUtc);

        var restore = Assert.Single(reloaded.GetDesiredDatesForUpgradedItems(hideUpgradesEnabled: false));
        Assert.Equal(newId, restore.ItemId);
        Assert.Equal(upgradeDate, restore.DesiredDateUtc);
    }

    [Fact]
    public void ResolveHideWatchedWithDefaultsHandlesDeviceOnlyAndConflictingDefaults()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "moonfin-device-defaults-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var settings = new MoonfinSettingsService(NullLogger<MoonfinSettingsService>.Instance, tempRoot);
            var tvDefaults = new MoonfinSettingsProfile();
            var desktopDefaults = new MoonfinSettingsProfile();
            var mobileDefaults = new MoonfinSettingsProfile();
            var globalDefaults = new MoonfinSettingsProfile();
            settings.AdminDefaultsOverrideForTesting = profile => profile.ToLowerInvariant() switch
            {
                "tv" => tvDefaults,
                "desktop" => desktopDefaults,
                "mobile" => mobileDefaults,
                _ => globalDefaults,
            };

            var unsetUser = new MoonfinUserSettings();

            // TV-only true when global/user unset -> resolves true.
            tvDefaults.HideWatchedInRecentRows = true;
            Assert.True(settings.ResolveHideWatchedWithDefaults(unsetUser, "global"));

            // Conflicting device defaults (TV = true, Desktop = false) -> true wins.
            desktopDefaults.HideWatchedInRecentRows = false;
            Assert.True(settings.ResolveHideWatchedWithDefaults(unsetUser, "global"));

            // Desktop-only false when others unset -> resolves false.
            tvDefaults.HideWatchedInRecentRows = null;
            Assert.False(settings.ResolveHideWatchedWithDefaults(unsetUser, "global"));

            // Explicit user override takes precedence over conflicting admin defaults.
            tvDefaults.HideWatchedInRecentRows = true;
            var userFalse = new MoonfinUserSettings
            {
                Global = new MoonfinSettingsProfile { HideWatchedInRecentRows = false }
            };
            Assert.False(settings.ResolveHideWatchedWithDefaults(userFalse, "global"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StaleBackupWithPrimaryAccessErrorDoesNotCacheFalseAndRetriesToRecover()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "moonfin-stale-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var settings = new MoonfinSettingsService(NullLogger<MoonfinSettingsService>.Instance, tempRoot);
            settings.UpgradeScanRetryDelayForTesting = TimeSpan.FromMilliseconds(30);

            var userId = Guid.NewGuid();
            var primaryPath = Path.Combine(tempRoot, $"{userId}.json");
            var backupPath = primaryPath + ".bak";

            // Backup has stale false, primary has true.
            File.WriteAllText(backupPath, JsonSerializer.Serialize(new MoonfinUserSettings
            {
                SchemaVersion = 2,
                Global = new MoonfinSettingsProfile { HideUpgradesInRecentRows = false }
            }));
            File.WriteAllText(primaryPath, JsonSerializer.Serialize(new MoonfinUserSettings
            {
                SchemaVersion = 2,
                Global = new MoonfinSettingsProfile { HideUpgradesInRecentRows = true }
            }));

            var appliedStates = new List<bool>();
            var recoveredTrue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            settings.UpgradePreferenceChanged = (enabled, _) =>
            {
                lock (appliedStates)
                {
                    appliedStates.Add(enabled);
                }

                if (enabled)
                {
                    recoveredTrue.TrySetResult();
                }

                return Task.CompletedTask;
            };

            FileStream? lockStream = new FileStream(primaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            try
            {
                // Even though backup is readable and returns false, primary access error must keep scanIncomplete = true.
                var initial = settings.IsAnyHideUpgradesEnabled(out var incomplete);
                Assert.False(initial);
                Assert.True(incomplete);

                // Kick off worker while primary file is locked; worker must preserve uncached state and arrange a retry.
                await settings.ScheduleUpgradePreferenceSyncIfNeeded(userId).WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                lockStream.Dispose();
                lockStream = null;
            }

            // Without any new settings write, the scheduled retry recovers and applies true.
            await recoveredTrue.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(settings.IsAnyHideUpgradesEnabled(out var incompleteAfterRecovery));
            Assert.False(incompleteAfterRecovery);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MediaUpgradeServiceConvergesToLatestPreferenceAcrossBothInterleavings()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "moonfin-upgrade-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var settings = new MoonfinSettingsService(NullLogger<MoonfinSettingsService>.Instance, tempRoot);
            var ledger = new MediaUpgradeLedger(Path.Combine(tempRoot, MediaUpgradeLedger.StoreFileName), new JellyfinStateFile());

            var movieId = Guid.NewGuid();
            var originalDate = new DateTime(2023, 3, 1, 10, 0, 0, DateTimeKind.Utc);
            var upgradeDate = new DateTime(2026, 8, 15, 14, 0, 0, DateTimeKind.Utc);

            var movie = new Movie
            {
                Id = movieId,
                Name = "Upgrade Race Movie",
                ProductionYear = 2023,
                DateCreated = upgradeDate,
            };
            movie.SetProviderId(MetadataProvider.Tmdb, "998877");

            ledger.RecordRemoved(new MediaUpgradeCandidate
            {
                ItemId = Guid.NewGuid(),
                IsEpisode = false,
                TmdbId = "998877",
                DateCreatedUtc = originalDate,
            });

            var fakeLibrary = RecordingHostProxy.Create<ILibraryManager>((method, args) =>
            {
                if (method.Name == nameof(ILibraryManager.GetItemById))
                {
                    return (Guid)args![0]! == movieId ? movie : null;
                }

                return null;
            });

            var upgradeService = new MediaUpgradeService(
                fakeLibrary,
                settings,
                ledger,
                NullLogger<MediaUpgradeService>.Instance);

            var userId = Guid.NewGuid();
            await settings.SaveProfileAsync(userId, "global", new MoonfinSettingsProfile { HideUpgradesInRecentRows = false });
            await settings.ScheduleUpgradePreferenceSyncIfNeeded(userId).WaitAsync(TimeSpan.FromSeconds(5));

            // Observe initial upgrade arrival with HideUpgrades = false (so EffectiveDateCreated should be upgradeDate).
            ledger.ObserveArrival(
                new MediaUpgradeCandidate
                {
                    ItemId = movieId,
                    IsEpisode = false,
                    TmdbId = "998877",
                    DateCreatedUtc = upgradeDate,
                },
                hideUpgradesEnabled: false,
                out _);

            // Opposite per-user preferences resolve distinct effective dates for Media Bar callers.
            Assert.Equal(originalDate, upgradeService.EffectiveDateCreated(movieId, movie.DateCreated, hideUpgradesEnabled: true));
            Assert.Equal(upgradeDate, upgradeService.EffectiveDateCreated(movieId, movie.DateCreated, hideUpgradesEnabled: false));

            // Interleaving 1: queued arrival task pauses BEFORE entering per-item gate while preference toggles to true and completes.
            movie.DateCreated = originalDate; // Simulate stale DateCreated before arrival task runs while pref is false.
            var arrivalPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseArrival = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            upgradeService.BeforeQueuedArrivalStartForTesting = async () =>
            {
                arrivalPaused.TrySetResult();
                await releaseArrival.Task.ConfigureAwait(false);
            };
            upgradeService.PersistItemDateForTesting = (_, _, _) => Task.CompletedTask;

            // Reset ledger item to re-trigger ObserveArrival desire cleanly.
            movie.DateCreated = originalDate;
            upgradeService.HandleItemChangedForTesting(movie);
            await arrivalPaused.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Preference changes to true and completes BEFORE queued arrival task starts executing inside gate.
            await upgradeService.ApplyUpgradeDatesAsync(hideUpgradesEnabled: true);
            Assert.Equal(originalDate, movie.DateCreated);

            releaseArrival.TrySetResult();
            await upgradeService.WaitForPendingArrivalUpdatesForTestingAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(originalDate, movie.DateCreated);

            // Interleaving 2: an active write (for false -> upgradeDate) is paused inside gate when true arrives.
            upgradeService.BeforeQueuedArrivalStartForTesting = null;
            var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writeCount = 0;
            upgradeService.PersistItemDateForTesting = async (_, _, _) =>
            {
                if (Interlocked.Increment(ref writeCount) == 1)
                {
                    writeStarted.TrySetResult();
                    await releaseFirstWrite.Task.ConfigureAwait(false);
                }
            };

            // Start write expecting upgradeDate (false).
            var firstWriteTask = upgradeService.ApplyUpgradeDatesAsync(hideUpgradesEnabled: false);
            await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Concurrent toggle back to true waits on per-item gate while first write is still in progress.
            var secondWriteTask = upgradeService.ApplyUpgradeDatesAsync(hideUpgradesEnabled: true);
            releaseFirstWrite.TrySetResult();

            await Task.WhenAll(firstWriteTask, secondWriteTask).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(originalDate, movie.DateCreated);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SeedDoesNotReassignRemovedRecordWhenBothCopiesAreLiveOnReload(bool seedBFirst)
    {
        var stateFile = new MemoryStateFile();
        var storePath = "ledger.json";
        var ledger1 = new MediaUpgradeLedger(storePath, stateFile);

        var copyA = Guid.NewGuid();
        var copyB = Guid.NewGuid();
        var dateA = new DateTime(2023, 1, 10, 8, 0, 0, DateTimeKind.Utc);
        var dateB = new DateTime(2024, 5, 20, 12, 0, 0, DateTimeKind.Utc);

        ledger1.Seed(
            [
                new MediaUpgradeCandidate { ItemId = copyA, TmdbId = "555111", DateCreatedUtc = dateA },
                new MediaUpgradeCandidate { ItemId = copyB, TmdbId = "555111", DateCreatedUtc = dateB }
            ]);

        // Persist a removed record for copyA, then reload from disk when both copies are live again.
        ledger1.RecordRemoved(new MediaUpgradeCandidate { ItemId = copyA, TmdbId = "555111", DateCreatedUtc = dateA });

        var ledger2 = new MediaUpgradeLedger(storePath, stateFile);
        var seedOrder = seedBFirst
            ? new[]
            {
                new MediaUpgradeCandidate { ItemId = copyB, TmdbId = "555111", DateCreatedUtc = dateB },
                new MediaUpgradeCandidate { ItemId = copyA, TmdbId = "555111", DateCreatedUtc = dateA }
            }
            : new[]
            {
                new MediaUpgradeCandidate { ItemId = copyA, TmdbId = "555111", DateCreatedUtc = dateA },
                new MediaUpgradeCandidate { ItemId = copyB, TmdbId = "555111", DateCreatedUtc = dateB }
            };

        ledger2.Seed(seedOrder);

        Assert.Null(ledger2.GetDesiredDateForItem(copyA, hideUpgradesEnabled: true));
        Assert.Null(ledger2.GetDesiredDateForItem(copyB, hideUpgradesEnabled: true));
        Assert.Equal(dateA, ledger2.EffectiveDateCreated(copyA, dateA, hideUpgradesEnabled: true));
        Assert.Equal(dateB, ledger2.EffectiveDateCreated(copyB, dateB, hideUpgradesEnabled: true));
        Assert.Equal(dateB, ledger2.EffectiveDateCreated(copyB, dateB, hideUpgradesEnabled: false));
    }

    [Fact]
    public async Task StaleTrueBackupWithPrimaryAccessErrorDoesNotCacheTrueAndRetriesPastFiveAttempts()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "moonfin-stale-true-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var settings = new MoonfinSettingsService(NullLogger<MoonfinSettingsService>.Instance, tempRoot);
            settings.UpgradeScanRetryDelayForTesting = TimeSpan.FromMilliseconds(15);

            var userId = Guid.NewGuid();
            var primaryPath = Path.Combine(tempRoot, $"{userId}.json");
            var backupPath = primaryPath + ".bak";

            // Backup has stale true while primary has false.
            File.WriteAllText(backupPath, JsonSerializer.Serialize(new MoonfinUserSettings
            {
                SchemaVersion = 2,
                Global = new MoonfinSettingsProfile { HideUpgradesInRecentRows = true }
            }));
            File.WriteAllText(primaryPath, JsonSerializer.Serialize(new MoonfinUserSettings
            {
                SchemaVersion = 2,
                Global = new MoonfinSettingsProfile { HideUpgradesInRecentRows = false }
            }));

            var scanAttempts = 0;
            var reachedSevenAttempts = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recoveredFalse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            settings.BeforePublishHideUpgradesScanForTesting = () =>
            {
                if (Interlocked.Increment(ref scanAttempts) >= 7)
                {
                    reachedSevenAttempts.TrySetResult();
                }
            };

            settings.UpgradePreferenceChanged = (enabled, _) =>
            {
                if (!enabled && Volatile.Read(ref scanAttempts) >= 7)
                {
                    recoveredFalse.TrySetResult();
                }

                return Task.CompletedTask;
            };

            FileStream? lockStream = new FileStream(primaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            try
            {
                var initial = settings.IsAnyHideUpgradesEnabled(out var incomplete);
                Assert.True(initial);
                Assert.True(incomplete);

                await settings.ScheduleUpgradePreferenceSyncIfNeeded(userId).WaitAsync(TimeSpan.FromSeconds(5));
                await reachedSevenAttempts.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                lockStream.Dispose();
                lockStream = null;
            }

            await recoveredFalse.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(settings.IsAnyHideUpgradesEnabled(out var incompleteAfterRecovery));
            Assert.False(incompleteAfterRecovery);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PendingSyncDuringIncompleteScanWithAppliedStateStillDrainsAndClearsWorkerFlag()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "moonfin-pending-incomplete-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var settings = new MoonfinSettingsService(NullLogger<MoonfinSettingsService>.Instance, tempRoot);
            settings.UpgradeScanRetryDelayForTesting = TimeSpan.FromMilliseconds(30);

            var userId = Guid.NewGuid();
            var primaryPath = Path.Combine(tempRoot, $"{userId}.json");
            File.WriteAllText(primaryPath, JsonSerializer.Serialize(new MoonfinUserSettings
            {
                SchemaVersion = 2,
                Global = new MoonfinSettingsProfile { HideUpgradesInRecentRows = false }
            }));

            var appliedTrue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            settings.UpgradePreferenceChanged = (enabled, _) =>
            {
                if (enabled)
                {
                    appliedTrue.TrySetResult();
                }

                return Task.CompletedTask;
            };

            // Establish _lastAppliedUpgradeState = 0.
            await settings.ScheduleUpgradePreferenceSyncIfNeeded(userId).WaitAsync(TimeSpan.FromSeconds(5));

            // Invalidate cache so next scan is incomplete while primary is locked with state still 0.
            settings.InvalidateHideUpgradesCache();
            var injectedPending = 0;
            FileStream? lockStream = new FileStream(primaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            try
            {
                settings.BeforePublishHideUpgradesScanForTesting = () =>
                {
                    if (Interlocked.CompareExchange(ref injectedPending, 1, 0) == 0)
                    {
                        // Queue a pending sync while scan is incomplete and targetState == _lastAppliedUpgradeState.
                        lockStream?.Dispose();
                        lockStream = null;
                        File.WriteAllText(primaryPath, JsonSerializer.Serialize(new MoonfinUserSettings
                        {
                            SchemaVersion = 2,
                            Global = new MoonfinSettingsProfile { HideUpgradesInRecentRows = true }
                        }));
                        settings.InvalidateHideUpgradesCache();
                        _ = settings.ScheduleUpgradePreferenceSyncIfNeeded(userId);
                    }
                };

                await settings.ScheduleUpgradePreferenceSyncIfNeeded(userId).WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                lockStream?.Dispose();
            }

            await appliedTrue.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(settings.IsUpgradeWorkerRunningForTesting);

            // Subsequent scheduling continues to work.
            settings.UpgradePreferenceChanged = (_, _) => Task.CompletedTask;
            await settings.ScheduleUpgradePreferenceSyncIfNeeded(userId, force: true).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(settings.IsUpgradeWorkerRunningForTesting);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ArrivalPausedBeforeObserveConvergesWhenHideUpgradesTurnsOnBeforeObserve()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "moonfin-observe-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var settings = new MoonfinSettingsService(NullLogger<MoonfinSettingsService>.Instance, tempRoot);
            var ledger = new MediaUpgradeLedger(Path.Combine(tempRoot, MediaUpgradeLedger.StoreFileName), new JellyfinStateFile());

            var movieId = Guid.NewGuid();
            var originalDate = new DateTime(2023, 2, 1, 9, 0, 0, DateTimeKind.Utc);
            var upgradeDate = new DateTime(2026, 9, 10, 18, 0, 0, DateTimeKind.Utc);

            var movie = new Movie
            {
                Id = movieId,
                Name = "Pre-Observe Race Movie",
                ProductionYear = 2023,
                DateCreated = upgradeDate,
            };
            movie.SetProviderId(MetadataProvider.Tmdb, "445566");

            ledger.RecordRemoved(new MediaUpgradeCandidate
            {
                ItemId = Guid.NewGuid(),
                IsEpisode = false,
                TmdbId = "445566",
                DateCreatedUtc = originalDate,
            });

            var fakeLibrary = RecordingHostProxy.Create<ILibraryManager>((method, args) =>
                method.Name == nameof(ILibraryManager.GetItemById)
                    ? ((Guid)args![0]! == movieId ? movie : null)
                    : null);

            var upgradeService = new MediaUpgradeService(
                fakeLibrary,
                settings,
                ledger,
                NullLogger<MediaUpgradeService>.Instance);

            var userId = Guid.NewGuid();
            await settings.SaveProfileAsync(userId, "global", new MoonfinSettingsProfile { HideUpgradesInRecentRows = false });
            await settings.ScheduleUpgradePreferenceSyncIfNeeded(userId).WaitAsync(TimeSpan.FromSeconds(5));

            using var enteredBeforeObserve = new ManualResetEventSlim(false);
            using var allowObserveContinue = new ManualResetEventSlim(false);
            upgradeService.PersistItemDateForTesting = (_, _, _) => Task.CompletedTask;
            upgradeService.BeforeObserveArrivalForTesting = () =>
            {
                enteredBeforeObserve.Set();
                allowObserveContinue.Wait(TimeSpan.FromSeconds(5));
            };

            var arrivalTask = Task.Run(() => upgradeService.HandleItemChangedForTesting(movie));
            Assert.True(enteredBeforeObserve.Wait(TimeSpan.FromSeconds(5)));

            // Preference changes to true and its scan finishes BEFORE ObserveArrival records the upgrade.
            await upgradeService.ApplyUpgradeDatesAsync(hideUpgradesEnabled: true);

            allowObserveContinue.Set();
            await arrivalTask.WaitAsync(TimeSpan.FromSeconds(5));
            await upgradeService.WaitForPendingArrivalUpdatesForTestingAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(originalDate, movie.DateCreated);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static class VisibleMediaItems
    {
        public sealed class Movie : MediaBrowser.Controller.Entities.Movies.Movie
        {
            public override bool IsVisible(User user) => true;
        }
    }

    [Fact]
    public async Task TwoUserMediaBarEndpointOrdersUpgradesIndependently()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "moonfin-twouser-mediabar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var settings = new MoonfinSettingsService(NullLogger<MoonfinSettingsService>.Instance, tempRoot);
            var ledger = new MediaUpgradeLedger(Path.Combine(tempRoot, MediaUpgradeLedger.StoreFileName), new JellyfinStateFile());

            var userHide = new User("hide-upgrades-user", "auth", "reset");
            var userShow = new User("show-upgrades-user", "auth", "reset");

            var upgradedMovieId = Guid.NewGuid();
            var newerMovieId = Guid.NewGuid();
            var originalDate = new DateTime(2023, 1, 15, 10, 0, 0, DateTimeKind.Utc);
            var middleDate = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);
            var upgradeDate = new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

            var upgradedMovie = new VisibleMediaItems.Movie
            {
                Id = upgradedMovieId,
                Name = "Upgraded Movie",
                ProductionYear = 2023,
                DateCreated = originalDate,
                ImageInfos = [new ItemImageInfo { Type = ImageType.Backdrop, Path = "/tmp/upgraded-backdrop.jpg" }],
            };
            upgradedMovie.SetProviderId(MetadataProvider.Tmdb, "778899");

            var newerMovie = new VisibleMediaItems.Movie
            {
                Id = newerMovieId,
                Name = "Newer Standard Movie",
                ProductionYear = 2025,
                DateCreated = middleDate,
                ImageInfos = [new ItemImageInfo { Type = ImageType.Backdrop, Path = "/tmp/newer-backdrop.jpg" }],
            };

            ledger.RecordRemoved(new MediaUpgradeCandidate
            {
                ItemId = Guid.NewGuid(),
                TmdbId = "778899",
                DateCreatedUtc = originalDate,
            });
            ledger.ObserveArrival(
                new MediaUpgradeCandidate
                {
                    ItemId = upgradedMovieId,
                    TmdbId = "778899",
                    DateCreatedUtc = upgradeDate,
                },
                hideUpgradesEnabled: true,
                out _);

            var items = new BaseItem[] { upgradedMovie, newerMovie };
            var fakeLibrary = RecordingHostProxy.Create<ILibraryManager>((method, args) =>
            {
                if (method.Name == nameof(ILibraryManager.GetItemById))
                {
                    var id = (Guid)args![0]!;
                    return items.FirstOrDefault(i => i.Id == id);
                }

                if (method.Name == nameof(ILibraryManager.GetItemsResult))
                {
                    var query = (InternalItemsQuery)args![0]!;
                    IEnumerable<BaseItem> seq = items;
                    if (query.ItemIds is { Length: > 0 } ids)
                    {
                        var idSet = ids.ToHashSet();
                        seq = seq.Where(i => idSet.Contains(i.Id));
                    }

                    if (query.OrderBy is { Count: > 0 })
                    {
                        seq = seq.OrderByDescending(i => i.DateCreated);
                    }

                    if (query.Limit.HasValue)
                    {
                        seq = seq.Take(query.Limit.Value);
                    }

                    return new QueryResult<BaseItem> { Items = seq.ToArray() };
                }

                return null;
            });

            var userMap = new Dictionary<Guid, User>
            {
                [userHide.Id] = userHide,
                [userShow.Id] = userShow,
            };
            var userManager = RecordingHostProxy.Create<IUserManager>((method, args) =>
                method.Name == nameof(IUserManager.GetUserById) && userMap.TryGetValue((Guid)args![0]!, out var u)
                    ? u
                    : null);
            var serviceProvider = RecordingHostProxy.Create<IServiceProvider>((_, args) =>
                (Type)args![0]! == typeof(IUserManager) ? userManager : null);

            await settings.SaveProfileAsync(userHide.Id, "global", new MoonfinSettingsProfile
            {
                MediaBarSourceType = "recentlyAdded",
                MediaBarItemCount = 10,
                HideUpgradesInRecentRows = true,
            }, notifySettingsChanged: false);

            await settings.SaveProfileAsync(userShow.Id, "global", new MoonfinSettingsProfile
            {
                MediaBarSourceType = "recentlyAdded",
                MediaBarItemCount = 10,
                HideUpgradesInRecentRows = false,
            }, notifySettingsChanged: false);

            var upgradeService = new MediaUpgradeService(
                fakeLibrary,
                settings,
                ledger,
                NullLogger<MediaUpgradeService>.Instance);

            var controller = new MoonfinController(
                settings,
                fakeLibrary,
                null!,
                null!,
                null!,
                null!,
                new ItemCardDtoMapper(null!, new NoOpLogger<ItemCardDtoMapper>()),
                null!,
                NullLogger<MoonfinController>.Instance,
                upgradeService);

            static Guid[] ExtractIds(ActionResult result)
            {
                var ok = Assert.IsType<OkObjectResult>(result);
                var itemsProp = ok.Value!.GetType().GetProperty("Items")!;
                var dtos = ((System.Collections.IEnumerable)itemsProp.GetValue(ok.Value)!).Cast<object>().ToList();
                return dtos.Select(d => (Guid)d.GetType().GetProperty("Id")!.GetValue(d)!).ToArray();
            }

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = serviceProvider,
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", userHide.Id.ToString("N"))], "test")),
                }
            };
            var hideResultIds = ExtractIds(await controller.GetMediaBarItems("global"));

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = serviceProvider,
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", userShow.Id.ToString("N"))], "test")),
                }
            };
            var showResultIds = ExtractIds(await controller.GetMediaBarItems("global"));

            Assert.Equal([newerMovieId, upgradedMovieId], hideResultIds);
            Assert.Equal([upgradedMovieId, newerMovieId], showResultIds);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void SeedWithSameNonRemovedItemIdAndNewerImportDateAdvancesPersistedLatestDate()
    {
        var stateFile = new MemoryStateFile();
        var storePath = "ledger-seed-sameid.json";
        var ledger1 = new MediaUpgradeLedger(storePath, stateFile);

        var itemId = Guid.NewGuid();
        var originalDate = new DateTime(2023, 1, 10, 8, 0, 0, DateTimeKind.Utc);
        var upgrade1Date = new DateTime(2025, 4, 1, 12, 0, 0, DateTimeKind.Utc);
        var upgrade2Date = new DateTime(2026, 7, 15, 18, 30, 0, DateTimeKind.Utc);

        ledger1.Seed([new MediaUpgradeCandidate { ItemId = itemId, TmdbId = "321654", DateCreatedUtc = originalDate }]);
        Assert.True(ledger1.ObserveArrival(
            new MediaUpgradeCandidate { ItemId = itemId, TmdbId = "321654", DateCreatedUtc = upgrade1Date },
            hideUpgradesEnabled: false,
            out _));

        // Reload and Seed with the SAME non-removed ItemId and a newer import date.
        var ledger2 = new MediaUpgradeLedger(storePath, stateFile);
        ledger2.Seed([new MediaUpgradeCandidate { ItemId = itemId, TmdbId = "321654", DateCreatedUtc = upgrade2Date }]);

        // Reload again to confirm the newer LatestAddedUtc was persisted to disk.
        var ledger3 = new MediaUpgradeLedger(storePath, stateFile);
        Assert.Equal(originalDate, ledger3.GetDesiredDateForItem(itemId, hideUpgradesEnabled: true));
        Assert.Equal(upgrade2Date, ledger3.GetDesiredDateForItem(itemId, hideUpgradesEnabled: false));
    }

    [Fact]
    public async Task UpdateItemDateCancellationWhileWaitingOnGateDoesNotFaultOrOverRelease()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "moonfin-gate-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var settings = new MoonfinSettingsService(NullLogger<MoonfinSettingsService>.Instance, tempRoot);
            var ledger = new MediaUpgradeLedger(Path.Combine(tempRoot, MediaUpgradeLedger.StoreFileName), new JellyfinStateFile());

            var movieId = Guid.NewGuid();
            var originalDate = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var upgradeDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

            var movie = new Movie { Id = movieId, Name = "Cancel Gate Movie", ProductionYear = 2023, DateCreated = originalDate };
            movie.SetProviderId(MetadataProvider.Tmdb, "654321");
            ledger.RecordRemoved(new MediaUpgradeCandidate { ItemId = Guid.NewGuid(), TmdbId = "654321", DateCreatedUtc = originalDate });
            ledger.ObserveArrival(new MediaUpgradeCandidate { ItemId = movieId, TmdbId = "654321", DateCreatedUtc = upgradeDate }, false, out _);

            var userId = Guid.NewGuid();
            await settings.SaveProfileAsync(userId, "global", new MoonfinSettingsProfile { HideUpgradesInRecentRows = false });
            await settings.ScheduleUpgradePreferenceSyncIfNeeded(userId).WaitAsync(TimeSpan.FromSeconds(5));

            var fakeLibrary = RecordingHostProxy.Create<ILibraryManager>((_, _) => null);
            var service = new MediaUpgradeService(fakeLibrary, settings, ledger, NullLogger<MediaUpgradeService>.Instance);

            var enteredPersist = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowPersistFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.PersistItemDateForTesting = async (_, _, _) =>
            {
                enteredPersist.TrySetResult();
                await allowPersistFinish.Task.ConfigureAwait(false);
            };

            var firstTask = service.UpdateItemDateAsync(movie, CancellationToken.None);
            await enteredPersist.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using var secondCts = new CancellationTokenSource();
            var secondTask = service.UpdateItemDateAsync(movie, secondCts.Token);
            secondCts.Cancel();

            // Must finish without throwing TaskCanceledException / SemaphoreFullException.
            Assert.False(await secondTask.WaitAsync(TimeSpan.FromSeconds(5)));

            allowPersistFinish.TrySetResult();
            Assert.True(await firstTask.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FailedPersistenceRestoresDateCreatedAndRetriesUpgradeBatch()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "moonfin-persist-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var settings = new MoonfinSettingsService(NullLogger<MoonfinSettingsService>.Instance, tempRoot);
            settings.UpgradeScanRetryDelayForTesting = TimeSpan.FromMilliseconds(20);
            var ledger = new MediaUpgradeLedger(Path.Combine(tempRoot, MediaUpgradeLedger.StoreFileName), new JellyfinStateFile());

            var movieId = Guid.NewGuid();
            var originalDate = new DateTime(2023, 5, 1, 10, 0, 0, DateTimeKind.Utc);
            var upgradeDate = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

            var movie = new Movie { Id = movieId, Name = "Retry Persist Movie", ProductionYear = 2023, DateCreated = originalDate };
            movie.SetProviderId(MetadataProvider.Tmdb, "112299");
            ledger.RecordRemoved(new MediaUpgradeCandidate { ItemId = Guid.NewGuid(), TmdbId = "112299", DateCreatedUtc = originalDate });
            ledger.ObserveArrival(new MediaUpgradeCandidate { ItemId = movieId, TmdbId = "112299", DateCreatedUtc = upgradeDate }, true, out _);

            var fakeLibrary = RecordingHostProxy.Create<ILibraryManager>((method, args) =>
                method.Name == nameof(ILibraryManager.GetItemById) && (Guid)args![0]! == movieId ? movie : null);

            var service = new MediaUpgradeService(fakeLibrary, settings, ledger, NullLogger<MediaUpgradeService>.Instance);

            var failWrites = 1;
            var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sawRestoredDateAfterFailure = false;
            service.PersistItemDateForTesting = (_, _, _) =>
            {
                if (Interlocked.Exchange(ref failWrites, 0) == 1)
                {
                    throw new IOException("Transient DB write failure");
                }

                recovered.TrySetResult();
                return Task.CompletedTask;
            };

            // Direct call preserves originalDate on failure and throws for batch retry tracking.
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyUpgradeDatesAsync(hideUpgradesEnabled: false));
            sawRestoredDateAfterFailure = movie.DateCreated == originalDate;
            Assert.True(sawRestoredDateAfterFailure);

            // Via worker: first failure keeps batch pending, retry runs automatically and converges to upgradeDate.
            Volatile.Write(ref failWrites, 1);
            var userId = Guid.NewGuid();
            await settings.SaveProfileAsync(userId, "global", new MoonfinSettingsProfile { HideUpgradesInRecentRows = false });
            await settings.ScheduleUpgradePreferenceSyncIfNeeded(userId, force: true).WaitAsync(TimeSpan.FromSeconds(5));
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(upgradeDate, movie.DateCreated);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MediaBarBatchesLargeUpgradeQueriesAndExcludesEpisodes()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "moonfin-mediabar-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var settings = new MoonfinSettingsService(NullLogger<MoonfinSettingsService>.Instance, tempRoot);
            var ledger = new MediaUpgradeLedger(Path.Combine(tempRoot, MediaUpgradeLedger.StoreFileName), new JellyfinStateFile());

            var baseDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var movieIds = new List<Guid>();
            var episodeIds = new HashSet<Guid>();
            var movies = new Dictionary<Guid, BaseItem>();

            for (var i = 0; i < 250; i++)
            {
                var id = Guid.NewGuid();
                movieIds.Add(id);
                var m = new VisibleMediaItems.Movie
                {
                    Id = id,
                    Name = $"Movie {i}",
                    ProductionYear = 2024,
                    DateCreated = baseDate.AddMinutes(i),
                    ImageInfos = [new ItemImageInfo { Type = ImageType.Backdrop, Path = "/tmp/bd.jpg" }],
                };
                m.SetProviderId(MetadataProvider.Tmdb, (10000 + i).ToString());
                movies[id] = m;

                ledger.RecordRemoved(new MediaUpgradeCandidate { ItemId = Guid.NewGuid(), TmdbId = (10000 + i).ToString(), DateCreatedUtc = baseDate });
                ledger.ObserveArrival(new MediaUpgradeCandidate { ItemId = id, TmdbId = (10000 + i).ToString(), DateCreatedUtc = baseDate.AddDays(10) }, false, out _);
            }

            for (var i = 0; i < 30; i++)
            {
                var epId = Guid.NewGuid();
                episodeIds.Add(epId);
                ledger.RecordRemoved(new MediaUpgradeCandidate { ItemId = Guid.NewGuid(), IsEpisode = true, SeriesId = Guid.NewGuid(), SeasonNumber = 1, EpisodeNumber = i + 1, DateCreatedUtc = baseDate });
                ledger.ObserveArrival(new MediaUpgradeCandidate { ItemId = epId, IsEpisode = true, SeriesId = Guid.NewGuid(), SeasonNumber = 1, EpisodeNumber = i + 1, DateCreatedUtc = baseDate.AddDays(10) }, false, out _);
            }

            var supplementalBatchSizes = new List<int>();
            var supplementalSawEpisodeId = false;
            var libraryGuid = Guid.NewGuid();
            var libraryFolder = new VisibleLibraryFolder { Id = libraryGuid, Name = "Movies" };
            var user = new User("batch-user", "auth", "reset");

            var fakeLibrary = RecordingHostProxy.Create<ILibraryManager>((method, args) =>
            {
                if (method.Name == nameof(ILibraryManager.GetItemById) && args is { Length: 1 } && (Guid)args[0]! == libraryGuid)
                {
                    return libraryFolder;
                }

                if (method.Name == nameof(ILibraryManager.GetUserRootFolder))
                {
                    return libraryFolder;
                }

                if (method.Name == nameof(ILibraryManager.GetVirtualFolders))
                {
                    return new List<VirtualFolderInfo> { new() { ItemId = libraryGuid.ToString("N"), Name = "Movies" } };
                }

                if (method.Name == nameof(ILibraryManager.GetItemsResult))
                {
                    var q = (InternalItemsQuery)args![0]!;
                    if (q.ItemIds is { Length: > 0 } ids)
                    {
                        supplementalBatchSizes.Add(ids.Length);
                        if (ids.Any(id => episodeIds.Contains(id)))
                        {
                            supplementalSawEpisodeId = true;
                        }

                        return new QueryResult<BaseItem>
                        {
                            Items = ids.Where(id => movies.ContainsKey(id)).Select(id => movies[id]).ToArray()
                        };
                    }

                    // Initial page omits upgraded items so supplemental queries run for all 250 movies.
                    return new QueryResult<BaseItem> { Items = Array.Empty<BaseItem>() };
                }

                return null;
            });

            var userManager = RecordingHostProxy.Create<IUserManager>((method, _) =>
                method.Name == nameof(IUserManager.GetUserById) ? user : null);
            var serviceProvider = RecordingHostProxy.Create<IServiceProvider>((_, args) =>
                (Type)args![0]! == typeof(IUserManager) ? userManager : null);

            await settings.SaveProfileAsync(user.Id, "global", new MoonfinSettingsProfile
            {
                MediaBarSourceType = "recentlyAdded",
                MediaBarItemCount = 5,
                HideUpgradesInRecentRows = false,
            }, notifySettingsChanged: false);

            var controller = new MoonfinController(
                settings,
                fakeLibrary,
                null!,
                null!,
                null!,
                null!,
                new ItemCardDtoMapper(null!, new NoOpLogger<ItemCardDtoMapper>()),
                null!,
                NullLogger<MoonfinController>.Instance,
                new MediaUpgradeService(fakeLibrary, settings, ledger, NullLogger<MediaUpgradeService>.Instance))
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        RequestServices = serviceProvider,
                        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", user.Id.ToString("N"))], "test")),
                    }
                }
            };

            // No-libraries branch (line 1507)
            var resultAll = await controller.GetMediaBarItems("global");
            Assert.IsType<OkObjectResult>(resultAll);
            Assert.NotEmpty(supplementalBatchSizes);
            Assert.All(supplementalBatchSizes, size => Assert.InRange(size, 1, 200));
            Assert.False(supplementalSawEpisodeId);

            // Specific library branch (line 1596)
            supplementalBatchSizes.Clear();
            await settings.SaveProfileAsync(user.Id, "global", new MoonfinSettingsProfile
            {
                MediaBarSourceType = "recentlyAdded",
                MediaBarItemCount = 5,
                MediaBarLibraryIds = [libraryGuid.ToString("N")],
                HideUpgradesInRecentRows = false,
            }, notifySettingsChanged: false);

            var resultFiltered = await controller.GetMediaBarItems("global");
            Assert.IsType<OkObjectResult>(resultFiltered);
            Assert.NotEmpty(supplementalBatchSizes);
            Assert.All(supplementalBatchSizes, size => Assert.InRange(size, 1, 200));
            Assert.False(supplementalSawEpisodeId);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LiveTvControllerReturnsServiceUnavailableOnStoreOutage()
    {
        var program = new GuideProgram
        {
            Id = "prog-1",
            ItemId = Guid.NewGuid(),
            SeriesName = "Test Show",
            Season = 1,
            Episode = 1,
            StartDate = DateTime.UtcNow.AddHours(2),
            EndDate = DateTime.UtcNow.AddHours(3),
        };
        var failingHost = RecordingHostProxy.Create<ILiveTvRecordingHost>((method, args) =>
        {
            if (method.Name == nameof(ILiveTvRecordingHost.GetProgram))
            {
                return (string)args![0]! == program.Id ? program : null;
            }

            if (method.Name == nameof(ILiveTvRecordingHost.IsVisibleTo))
            {
                return true;
            }

            return null;
        });
        var failingState = new AlwaysFailingStateFile();
        using var liveTvService = new IncompleteRecordingService(
            failingHost,
            new CapturingRecordingLog(),
            failingState,
            () => DateTime.UtcNow,
            "outage.json",
            () => new IncompleteRecordingSettings());

        var userId = Guid.NewGuid();
        var controller = new LiveTvRecordingsController(liveTvService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", userId.ToString("N"))], "test")),
                }
            }
        };

        var dismissResult = Assert.IsType<StatusCodeResult>(await controller.Dismiss(Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, dismissResult.StatusCode);

        var requestResult = await controller.RequestCompleteShowing(new CompleteShowingBody { ProgramId = "prog-1" }, CancellationToken.None);
        var requestStatus = Assert.IsType<StatusCodeResult>(requestResult.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, requestStatus.StatusCode);

        var scheduleResult = Assert.IsType<StatusCodeResult>(await controller.ScheduleSuggestedShowing(
            new SuggestedShowingBody { ProgramId = "prog-1", Showing = SuggestedShowing.FromProgram(program) },
            CancellationToken.None));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, scheduleResult.StatusCode);
    }

    [Fact]
    public async Task SyncHideWatchedForAffectedUsersValidatesBaselineGenerationZero()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "moonfin-sweep-gen0-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var user = new User("unsaved-user", "auth", "reset");
            var config = new MediaBrowser.Model.Configuration.UserConfiguration { HidePlayedInLatest = true };
            var userDto = new MediaBrowser.Model.Dto.UserDto { Id = user.Id, Configuration = config };

            var userManager = RecordingHostProxy.Create<IUserManager>((method, args) =>
            {
                if (method.Name == "GetUsers" || method.Name == "get_Users")
                {
                    return new List<User> { user };
                }

                if (method.Name == "GetUserById")
                {
                    return user;
                }

                if (method.Name == "GetUserDto")
                {
                    return userDto;
                }

                if (method.Name == "UpdateConfigurationAsync" && args is { Length: 2 } && args[1] is MediaBrowser.Model.Configuration.UserConfiguration updated)
                {
                    config.HidePlayedInLatest = updated.HidePlayedInLatest;
                    return Task.CompletedTask;
                }

                return null;
            });

            var sp = RecordingHostProxy.Create<IServiceProvider>((_, args) =>
                (Type)args![0]! == typeof(IUserManager) ? userManager : null);

            using var settings = new MoonfinSettingsService(NullLogger<MoonfinSettingsService>.Instance, tempRoot, sp)
            {
                AdminDefaultsOverrideForTesting = profile =>
                    profile == "global" ? new MoonfinSettingsProfile { HideWatchedInRecentRows = true } : null
            };

            var concurrentSaveTriggered = 0;
            settings.BeforeNativeSyncWaitForTesting = id =>
            {
                if (id == user.Id && Interlocked.CompareExchange(ref concurrentSaveTriggered, 1, 0) == 0)
                {
                    // Simulate SaveProfileAsync finishing while sweep is waiting on expectedGen = 0.
                    settings.SaveProfileAsync(
                        user.Id,
                        "global",
                        new MoonfinSettingsProfile { HideWatchedInRecentRows = false },
                        notifySettingsChanged: false).GetAwaiter().GetResult();
                }
            };

            await settings.SyncHideWatchedForAffectedUsersAsync();

            // Sweep saw expectedGen = 0 != currentGen = 1 and did NOT overwrite the user's explicit false back to true.
            Assert.False(config.HidePlayedInLatest);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private sealed class VisibleLibraryFolder : Folder
    {
        public override bool IsVisible(User user) => true;
    }

    private sealed class AlwaysFailingStateFile : IStateFile
    {
        public T? Read<T>(string path, Func<string, T?> parse) where T : class =>
            throw new IOException("Store unavailable");

        public void Write(string path, string content) =>
            throw new IOException("Store unavailable");
    }
}

