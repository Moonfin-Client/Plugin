using System.Text.Json;
using Moonfin.LiveTv;
using Moonfin.Server.Models;
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
}

