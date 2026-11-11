using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moonfin.LiveTv;
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
}

