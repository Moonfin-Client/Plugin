using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Moonfin.LiveTv;

namespace Moonfin.Upgrades;

/// <summary>
/// A Movie or Episode observed in the library, with the provider IDs and metadata needed to
/// recognize the same logical title when an upgrade replaces its file and item ID.
/// </summary>
public sealed class MediaUpgradeCandidate
{
    public Guid ItemId { get; set; }

    public bool IsEpisode { get; set; }

    public string? TvdbId { get; set; }

    public string? TmdbId { get; set; }

    public string? ImdbId { get; set; }

    public Guid? SeriesId { get; set; }

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }

    public string? Name { get; set; }

    public int? ProductionYear { get; set; }

    public DateTime DateCreatedUtc { get; set; }
}

/// <summary>
/// Persistent ledger entry for a single logical Movie or Episode, tracking both its original
/// import timestamp (<see cref="FirstAddedUtc"/>) and its latest upgrade timestamp
/// (<see cref="LatestAddedUtc"/>) so "Hide Upgrades" can be toggled on or off reversibly.
/// </summary>
public sealed class MediaUpgradeRecord
{
    [JsonPropertyName("keys")]
    public List<string> Keys { get; set; } = new List<string>();

    [JsonPropertyName("itemId")]
    public Guid ItemId { get; set; }

    [JsonPropertyName("isEpisode")]
    public bool IsEpisode { get; set; }

    [JsonPropertyName("firstAddedUtc")]
    public DateTime FirstAddedUtc { get; set; }

    [JsonPropertyName("latestAddedUtc")]
    public DateTime LatestAddedUtc { get; set; }

    [JsonPropertyName("upgraded")]
    public bool Upgraded { get; set; }

    [JsonPropertyName("removedAtUtc")]
    public DateTime? RemovedAtUtc { get; set; }
}

/// <summary>
/// Tracks initial import dates and upgrade replacement dates for Movies and Episodes across
/// Jellyfin and Emby library events.
/// </summary>
public sealed class MediaUpgradeLedger
{
    public const string StoreFileName = "upgrade-dates.json";

    /// <summary>
    /// Minimum age difference after <see cref="MediaUpgradeRecord.FirstAddedUtc"/> for a new file
    /// or replacement item to be treated as an upgrade rather than part of the initial import.
    /// </summary>
    public static readonly TimeSpan UpgradeThreshold = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Regex NonAlphanumeric = new Regex(@"[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly string _filePath;
    private readonly IStateFile _stateFile;
    private readonly IRecordingLog? _logger;
    private readonly object _gate = new object();

    private List<MediaUpgradeRecord>? _records;
    private Dictionary<string, MediaUpgradeRecord>? _byKey;
    private Dictionary<Guid, MediaUpgradeRecord>? _byItemId;

    public MediaUpgradeLedger(string filePath, IStateFile stateFile, IRecordingLog? logger = null)
    {
        _filePath = filePath;
        _stateFile = stateFile;
        _logger = logger;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _records!.Count;
            }
        }
    }

    /// <summary>
    /// Computes canonical identity keys for a Movie or Episode. Any matching key links an upgraded
    /// replacement item back to its original import record.
    /// </summary>
    public static List<string> KeysFor(MediaUpgradeCandidate candidate)
    {
        var keys = new List<string>();
        if (candidate == null)
        {
            return keys;
        }

        if (candidate.IsEpisode)
        {
            if (candidate.SeasonNumber == null || candidate.EpisodeNumber == null)
            {
                return keys;
            }

            var suffix = FormattableString.Invariant($":s{candidate.SeasonNumber.Value}e{candidate.EpisodeNumber.Value}");
            if (!string.IsNullOrWhiteSpace(candidate.TvdbId))
            {
                keys.Add("episode:tvdb:" + candidate.TvdbId!.Trim().ToLowerInvariant() + suffix);
            }

            if (!string.IsNullOrWhiteSpace(candidate.TmdbId))
            {
                keys.Add("episode:tmdb:" + candidate.TmdbId!.Trim().ToLowerInvariant() + suffix);
            }

            if (!string.IsNullOrWhiteSpace(candidate.ImdbId))
            {
                keys.Add("episode:imdb:" + candidate.ImdbId!.Trim().ToLowerInvariant() + suffix);
            }

            if (candidate.SeriesId.HasValue && candidate.SeriesId.Value != Guid.Empty)
            {
                keys.Add("episode:series:" + candidate.SeriesId.Value.ToString("N") + suffix);
            }

            return keys;
        }

        if (!string.IsNullOrWhiteSpace(candidate.TmdbId))
        {
            keys.Add("movie:tmdb:" + candidate.TmdbId!.Trim().ToLowerInvariant());
        }

        if (!string.IsNullOrWhiteSpace(candidate.ImdbId))
        {
            keys.Add("movie:imdb:" + candidate.ImdbId!.Trim().ToLowerInvariant());
        }

        if (!string.IsNullOrWhiteSpace(candidate.TvdbId))
        {
            keys.Add("movie:tvdb:" + candidate.TvdbId!.Trim().ToLowerInvariant());
        }

        if (keys.Count == 0 && !string.IsNullOrWhiteSpace(candidate.Name) && candidate.ProductionYear > 0)
        {
            var normalized = NonAlphanumeric.Replace(candidate.Name!.Trim().ToLowerInvariant(), "-").Trim('-');
            if (!string.IsNullOrEmpty(normalized))
            {
                keys.Add(FormattableString.Invariant($"movie:title:{normalized}:{candidate.ProductionYear.Value}"));
            }
        }

        return keys;
    }

    /// <summary>
    /// Seeds baseline <c>FirstAddedUtc</c> timestamps from existing library items in memory on
    /// startup without writing un-upgraded library items to disk.
    /// </summary>
    public void Seed(IEnumerable<MediaUpgradeCandidate> candidates)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var dirtyPersisted = false;

            foreach (var candidate in candidates)
            {
                if (candidate.ItemId == Guid.Empty)
                {
                    continue;
                }

                var keys = KeysFor(candidate);
                if (keys.Count == 0)
                {
                    continue;
                }

                var dateUtc = NormalizeUtc(candidate.DateCreatedUtc);
                var existing = FindRecord(candidate.ItemId, keys);
                if (existing == null)
                {
                    var record = new MediaUpgradeRecord
                    {
                        Keys = keys,
                        ItemId = candidate.ItemId,
                        IsEpisode = candidate.IsEpisode,
                        FirstAddedUtc = dateUtc,
                        LatestAddedUtc = dateUtc,
                        Upgraded = false
                    };
                    AddRecord(record);
                }
                else
                {
                    var isPersisted = existing.Upgraded || existing.RemovedAtUtc.HasValue;
                    if (MergeKeys(existing, keys) && isPersisted)
                    {
                        dirtyPersisted = true;
                    }

                    if (dateUtc < existing.FirstAddedUtc)
                    {
                        existing.FirstAddedUtc = dateUtc;
                        if (isPersisted)
                        {
                            dirtyPersisted = true;
                        }
                    }
                }
            }

            if (dirtyPersisted)
            {
                Save();
            }
        }
    }

    /// <summary>
    /// Records an item removal so that if the item was not yet seeded or is being replaced by an
    /// upgrade, its original <c>DateCreated</c> is preserved before the item leaves the library.
    /// </summary>
    public void RecordRemoved(MediaUpgradeCandidate candidate, DateTime? removedAtUtc = null)
    {
        if (candidate == null || candidate.ItemId == Guid.Empty)
        {
            return;
        }

        lock (_gate)
        {
            EnsureLoaded();
            var keys = KeysFor(candidate);
            var existing = FindRecord(candidate.ItemId, keys);
            var dateUtc = NormalizeUtc(candidate.DateCreatedUtc);
            var nowUtc = NormalizeUtc(removedAtUtc ?? DateTime.UtcNow);

            if (existing == null)
            {
                if (keys.Count == 0)
                {
                    return;
                }

                var record = new MediaUpgradeRecord
                {
                    Keys = keys,
                    ItemId = candidate.ItemId,
                    IsEpisode = candidate.IsEpisode,
                    FirstAddedUtc = dateUtc,
                    LatestAddedUtc = dateUtc,
                    Upgraded = false,
                    RemovedAtUtc = nowUtc
                };
                AddRecord(record);
                Save();
                return;
            }

            var dirty = MergeKeys(existing, keys);
            if (dateUtc < existing.FirstAddedUtc)
            {
                existing.FirstAddedUtc = dateUtc;
                dirty = true;
            }

            if (existing.RemovedAtUtc != nowUtc)
            {
                existing.RemovedAtUtc = nowUtc;
                dirty = true;
            }

            if (dirty)
            {
                Save();
            }
        }
    }

    /// <summary>
    /// Observes an added or updated library item. If the item is an upgrade of a previously
    /// tracked title, marks it as upgraded and outputs <paramref name="desiredDateCreatedUtc"/>
    /// when its current <c>DateCreatedUtc</c> differs from the date dictated by
    /// <paramref name="hideUpgradesEnabled"/>.
    /// </summary>
    public bool ObserveArrival(
        MediaUpgradeCandidate candidate,
        bool hideUpgradesEnabled,
        out DateTime? desiredDateCreatedUtc)
    {
        desiredDateCreatedUtc = null;
        if (candidate == null || candidate.ItemId == Guid.Empty)
        {
            return false;
        }

        lock (_gate)
        {
            EnsureLoaded();
            var keys = KeysFor(candidate);
            if (keys.Count == 0)
            {
                return false;
            }

            var dateUtc = NormalizeUtc(candidate.DateCreatedUtc);
            var existing = FindRecord(candidate.ItemId, keys);

            if (existing == null)
            {
                var record = new MediaUpgradeRecord
                {
                    Keys = keys,
                    ItemId = candidate.ItemId,
                    IsEpisode = candidate.IsEpisode,
                    FirstAddedUtc = dateUtc,
                    LatestAddedUtc = dateUtc,
                    Upgraded = false
                };
                AddRecord(record);
                return false;
            }

            var wasPersisted = existing.Upgraded || existing.RemovedAtUtc.HasValue;
            var dirty = MergeKeys(existing, keys);

            var isReplacementItem = existing.ItemId != candidate.ItemId || existing.RemovedAtUtc.HasValue;
            var isNewerThanBaseline = dateUtc > existing.FirstAddedUtc + UpgradeThreshold
                || (existing.RemovedAtUtc.HasValue && dateUtc > existing.FirstAddedUtc + TimeSpan.FromSeconds(1));

            if ((isReplacementItem && isNewerThanBaseline) || dateUtc > existing.FirstAddedUtc + UpgradeThreshold)
            {
                if (!existing.Upgraded)
                {
                    existing.Upgraded = true;
                    dirty = true;
                }

                if (dateUtc > existing.LatestAddedUtc)
                {
                    existing.LatestAddedUtc = dateUtc;
                    dirty = true;
                }
            }

            if (existing.ItemId != candidate.ItemId)
            {
                _byItemId!.Remove(existing.ItemId);
                existing.ItemId = candidate.ItemId;
                _byItemId[candidate.ItemId] = existing;
                dirty = true;
            }

            if (existing.RemovedAtUtc.HasValue)
            {
                existing.RemovedAtUtc = null;
                dirty = true;
            }

            var isNowPersisted = existing.Upgraded || existing.RemovedAtUtc.HasValue;
            if (dirty && (wasPersisted || isNowPersisted))
            {
                Save();
            }

            if (!existing.Upgraded)
            {
                return false;
            }

            var targetUtc = hideUpgradesEnabled ? existing.FirstAddedUtc : existing.LatestAddedUtc;
            if (Math.Abs((dateUtc - targetUtc).TotalSeconds) > 1)
            {
                desiredDateCreatedUtc = targetUtc;
            }

            return true;
        }
    }

    /// <summary>
    /// Returns the desired <c>DateCreated</c> timestamp for every upgraded item in the ledger
    /// according to <paramref name="hideUpgradesEnabled"/>.
    /// </summary>
    public List<(Guid ItemId, DateTime DesiredDateUtc)> GetDesiredDatesForUpgradedItems(bool hideUpgradesEnabled)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var result = new List<(Guid ItemId, DateTime DesiredDateUtc)>();
            foreach (var record in _records!)
            {
                if (!record.Upgraded || record.ItemId == Guid.Empty)
                {
                    continue;
                }

                var targetUtc = hideUpgradesEnabled ? record.FirstAddedUtc : record.LatestAddedUtc;
                result.Add((record.ItemId, targetUtc));
            }

            return result;
        }
    }

    /// <summary>
    /// Returns the effective <c>DateCreated</c> for an item given a user's preference, used by
    /// per-user surfaces such as the Media Bar.
    /// </summary>
    public DateTime EffectiveDateCreated(Guid itemId, DateTime fallbackUtc, bool hideUpgradesEnabled)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_byItemId!.TryGetValue(itemId, out var record) && record.Upgraded)
            {
                return hideUpgradesEnabled ? record.FirstAddedUtc : record.LatestAddedUtc;
            }

            return fallbackUtc;
        }
    }

    private MediaUpgradeRecord? FindRecord(Guid itemId, List<string> keys)
    {
        foreach (var key in keys)
        {
            if (_byKey!.TryGetValue(key, out var byKeyRecord))
            {
                return byKeyRecord;
            }
        }

        if (itemId != Guid.Empty && _byItemId!.TryGetValue(itemId, out var byIdRecord))
        {
            return byIdRecord;
        }

        return null;
    }

    private void AddRecord(MediaUpgradeRecord record)
    {
        _records!.Add(record);
        if (record.ItemId != Guid.Empty)
        {
            _byItemId![record.ItemId] = record;
        }

        foreach (var key in record.Keys)
        {
            _byKey![key] = record;
        }
    }

    private bool MergeKeys(MediaUpgradeRecord record, List<string> keys)
    {
        var added = false;
        foreach (var key in keys)
        {
            if (!record.Keys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                record.Keys.Add(key);
                added = true;
            }

            _byKey![key] = record;
        }

        return added;
    }

    private void EnsureLoaded()
    {
        if (_records != null)
        {
            return;
        }

        List<MediaUpgradeRecord>? loaded = null;
        var parseInvoked = false;
        Exception? loadError = null;
        try
        {
            loaded = _stateFile.Read(
                _filePath,
                json =>
                {
                    parseInvoked = true;
                    return JsonSerializer.Deserialize<List<MediaUpgradeRecord>>(json, JsonOptions);
                });
        }
        catch (Exception ex)
        {
            loadError = ex;
            loaded = null;
        }

        if (loaded == null && (parseInvoked || loadError != null || File.Exists(_filePath) || File.Exists(_filePath + ".bak")))
        {
            _logger?.Warn(loadError, "Could not load media upgrade ledger from {Path}; starting with an empty ledger", _filePath);
        }

        _records = new List<MediaUpgradeRecord>();
        _byKey = new Dictionary<string, MediaUpgradeRecord>(StringComparer.OrdinalIgnoreCase);
        _byItemId = new Dictionary<Guid, MediaUpgradeRecord>();

        if (loaded == null)
        {
            return;
        }

        foreach (var record in loaded)
        {
            if (record == null || record.Keys == null || record.Keys.Count == 0)
            {
                continue;
            }

            record.FirstAddedUtc = NormalizeUtc(record.FirstAddedUtc);
            record.LatestAddedUtc = NormalizeUtc(record.LatestAddedUtc);
            AddRecord(record);
        }
    }

    private void Save()
    {
        try
        {
            var cutoffUtc = DateTime.UtcNow.AddDays(-7);
            var persisted = _records!
                .Where(r => r.Upgraded || (r.RemovedAtUtc.HasValue && r.RemovedAtUtc.Value >= cutoffUtc))
                .ToList();

            if (persisted.Count == 0 && !File.Exists(_filePath))
            {
                return;
            }

            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(persisted, JsonOptions);
            _stateFile.Write(_filePath, json);
        }
        catch
        {
            // Never let disk errors crash library scan event handlers.
        }
    }

    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);
}

