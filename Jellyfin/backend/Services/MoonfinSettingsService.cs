using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Models;

namespace Moonfin.Server.Services;

/// <summary>
/// Service for managing Moonfin user settings storage with device profile support.
/// </summary>
public class MoonfinSettingsService
{
    private readonly string _dataPath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<MoonfinSettingsService> _logger;
    private static readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Channel<string>, byte>> _sseChannels = new();

    // Resolve and merge reflect over every property on each call, and the profile carries a
    // couple of hundred of them, so the lookup is cached rather than repeated per request.
    private static readonly System.Reflection.PropertyInfo[] ProfileProps =
        typeof(MoonfinSettingsProfile).GetProperties();

    private static readonly System.Reflection.PropertyInfo[] UserSettingsProps =
        typeof(MoonfinUserSettings).GetProperties();

    public MoonfinSettingsService(ILogger<MoonfinSettingsService> logger)
        : this(logger, MoonfinPlugin.ResolveDataFolderPath())
    {
    }

    /// <summary>Lets tests point the store at a temp directory.</summary>
    internal MoonfinSettingsService(ILogger<MoonfinSettingsService> logger, string dataPath)
    {
        _logger = logger;
        _dataPath = dataPath;

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        EnsureDataDirectory();
    }

    private void EnsureDataDirectory()
    {
        if (!Directory.Exists(_dataPath))
        {
            Directory.CreateDirectory(_dataPath);
        }
    }

    private string GetUserSettingsPath(Guid userId)
    {
        return Path.Combine(_dataPath, $"{userId}.json");
    }

    /// <summary>
    /// Loads settings, falling back to the backup copy when the file is missing or unreadable.
    /// </summary>
    private MoonfinUserSettings? ReadSettingsWithRecovery(string filePath)
    {
        return AtomicFile.ReadWithRecovery(
            filePath,
            json => JsonSerializer.Deserialize<MoonfinUserSettings>(json, _jsonOptions));
    }

    public async Task<MoonfinUserSettings?> GetUserSettingsAsync(Guid userId)
    {
        var filePath = GetUserSettingsPath(userId);

        await _lock.WaitAsync();
        try
        {
            var settings = ReadSettingsWithRecovery(filePath);
            if (settings == null)
            {
                return null;
            }

            if (settings.NeedsMigration)
            {
                _logger.LogInformation("Migrating v1 settings to v2 for user {UserId}", userId);
                settings = MigrateV1ToV2(settings);

                // Persist the migrated version
                var migratedJson = JsonSerializer.Serialize(settings, _jsonOptions);
                AtomicFile.WriteAllText(filePath, migratedJson);
            }

            return settings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading settings for user {UserId}", userId);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<MoonfinSettingsProfile?> GetResolvedProfileAsync(Guid userId, string profileName)
    {
        var settings = await GetUserSettingsAsync(userId);
        if (settings == null) return null;

        return ResolveProfile(settings, profileName);
    }

    /// <summary>
    /// Resolves a flat profile: device override → global → admin defaults.
    /// </summary>
    public MoonfinSettingsProfile ResolveProfile(MoonfinUserSettings settings, string profileName)
    {
        var global = settings.Global;
        var deviceProfile = !string.IsNullOrEmpty(profileName) && profileName.ToLowerInvariant() != "global" ? settings.GetProfile(profileName) : null;
        var adminDefaults = MoonfinPlugin.Instance?.Configuration?.DefaultUserSettings;

        var resolved = new MoonfinSettingsProfile();
        var properties = ProfileProps;

        foreach (var prop in properties)
        {
            // Resolution chain: device → global → admin defaults
            var value = deviceProfile != null ? prop.GetValue(deviceProfile) : null;
            value ??= global != null ? prop.GetValue(global) : null;
            value ??= adminDefaults != null ? prop.GetValue(adminDefaults) : null;

            if (value != null)
            {
                prop.SetValue(resolved, value);
            }
        }

        var homeLayout = ResolveHomeLayout(deviceProfile, global, adminDefaults);
        resolved.HomeSections = homeLayout.HomeSections;
        resolved.HomeRowOrder = homeLayout.HomeRowOrder;

        if (resolved.MdblistRatingSources != null)
        {
            for (var i = 0; i < resolved.MdblistRatingSources.Count; i++)
            {
                if (string.Equals(resolved.MdblistRatingSources[i], "rtAudience", StringComparison.OrdinalIgnoreCase))
                {
                    resolved.MdblistRatingSources[i] = "tomatoes_audience";
                }
            }
        }

        if (string.IsNullOrWhiteSpace(resolved.TmdbApiKey))
        {
            resolved.TmdbApiKey = MoonfinPlugin.Instance?.Configuration?.TmdbApiKey;
        }

        if (string.IsNullOrWhiteSpace(resolved.MdblistApiKey))
        {
            resolved.MdblistApiKey = MoonfinPlugin.Instance?.Configuration?.MdblistApiKey;
        }

        return resolved;
    }

    private static (List<MoonfinHomeSectionConfig>? HomeSections, List<string>? HomeRowOrder) ResolveHomeLayout(
        MoonfinSettingsProfile? deviceProfile,
        MoonfinSettingsProfile? global,
        MoonfinSettingsProfile? adminDefaults)
    {
        foreach (var profile in new[] { deviceProfile, global, adminDefaults })
        {
            if (profile == null)
            {
                continue;
            }

            if (profile.HomeSections == null && profile.HomeRowOrder == null)
            {
                continue;
            }

            return (profile.HomeSections, ResolveHomeRowOrder(profile));
        }

        return (null, null);
    }

    private static List<string>? ResolveHomeRowOrder(MoonfinSettingsProfile profile)
    {
        if (profile.HomeRowOrder != null)
        {
            return profile.HomeRowOrder;
        }

        if (profile.HomeSections == null)
        {
            return null;
        }

        var homeRowOrder = profile.HomeSections
            .Where(section => !string.Equals(section.Kind, "pluginDynamic", StringComparison.OrdinalIgnoreCase))
            .Where(section => section.Enabled != false)
            .Where(section => !string.IsNullOrWhiteSpace(section.Type) &&
                !string.Equals(section.Type, "none", StringComparison.OrdinalIgnoreCase))
            .OrderBy(section => section.Order ?? int.MaxValue)
            .Select(section => section.Type!)
            .ToList();

        return homeRowOrder.Count > 0 ? homeRowOrder : null;
    }

    public async Task SaveUserSettingsAsync(Guid userId, MoonfinUserSettings settings, string? clientId = null, string mergeMode = "merge")
    {
        var filePath = GetUserSettingsPath(userId);
        bool settingsChanged;

        await _lock.WaitAsync();
        try
        {
            MoonfinUserSettings finalSettings;
            var customSectionsBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? beforeComparisonJson = null;

            if (mergeMode == "merge")
            {
                var existingSettings = ReadSettingsWithRecovery(filePath);

                // Migrate v1 if needed
                if (existingSettings != null && existingSettings.NeedsMigration)
                {
                    existingSettings = MigrateV1ToV2(existingSettings);
                }

                // MergeSettings writes into the stored object, so note which custom rows
                // were on file first or there's nothing left to compare against.
                customSectionsBefore = CustomHomeSectionIds(existingSettings);
                beforeComparisonJson = SerializeForComparison(existingSettings ?? new MoonfinUserSettings());
                finalSettings = MergeSettings(existingSettings, settings);
            }
            else
            {
                finalSettings = settings;
            }

            // Update metadata
            StripServerWideKeys(finalSettings);
            finalSettings.LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            finalSettings.LastUpdatedBy = clientId ?? "unknown";
            finalSettings.SchemaVersion = 2;

            MoveContentHidingToGlobal(finalSettings);
            PropagateCustomHomeSectionsAcrossProfiles(
                finalSettings,
                removed: RemovedCustomHomeSections(
                    customSectionsBefore,
                    CustomHomeSectionIds(finalSettings)));

            settingsChanged = SettingsDiffer(beforeComparisonJson, finalSettings);

            var json = JsonSerializer.Serialize(finalSettings, _jsonOptions);
            AtomicFile.WriteAllText(filePath, json);
        }
        finally
        {
            _lock.Release();
        }

        // A merge that changed nothing is the client echoing state it already
        // holds. Broadcasting it anyway sends the echo straight back and keeps
        // a push-apply loop alive on the other end.
        if (settingsChanged)
        {
            NotifySettingsChanged(userId);
        }
    }

      public async Task SaveProfileAsync(
          Guid userId,
          string profileName,
          MoonfinSettingsProfile profile,
          string? clientId = null,
          bool notifySettingsChanged = true)
    {
        var filePath = GetUserSettingsPath(userId);
        bool settingsChanged;

        await _lock.WaitAsync();
        try
        {
            var settings = ReadSettingsWithRecovery(filePath) ?? new MoonfinUserSettings();

            if (settings.NeedsMigration)
            {
                settings = MigrateV1ToV2(settings);
            }

            var beforeComparisonJson = SerializeForComparison(settings);

            // Merge profile properties
            var existingProfile = profileName.ToLowerInvariant() == "global" 
                ? settings.Global 
                : settings.GetProfile(profileName);

            // Note the custom rows this profile held before the merge overwrites them. Only a
            // push carrying a layout can express a deletion, since a partial push leaves
            // HomeSections null and mustn't read as the user clearing every custom row.
            var customSectionsBefore = profile.HomeSections == null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : CustomHomeSectionIds(existingProfile);

            if (existingProfile != null)
            {
                MergeProfile(existingProfile, profile);
            }
            else
            {
                settings.SetProfile(profileName, profile);
            }

            var savedProfile = existingProfile ?? profile;

            // Update metadata
            StripServerWideKeys(settings);
            settings.LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            settings.LastUpdatedBy = clientId ?? "unknown";
            settings.SchemaVersion = 2;

            MoveContentHidingToGlobal(settings);
            PropagateCustomHomeSectionsAcrossProfiles(
                settings,
                savedProfile,
                RemovedCustomHomeSections(
                    customSectionsBefore,
                    CustomHomeSectionIds(savedProfile)));

            settingsChanged = SettingsDiffer(beforeComparisonJson, settings);

            var serialized = JsonSerializer.Serialize(settings, _jsonOptions);
            AtomicFile.WriteAllText(filePath, serialized);
        }
        finally
        {
            _lock.Release();
        }

        // Same echo guard as SaveUserSettingsAsync.
        if (notifySettingsChanged && settingsChanged)
        {
            NotifySettingsChanged(userId);
        }
    }

    /// <summary>
    /// Whether the save altered anything a client would act on. A snapshot that
    /// could not be taken counts as changed, so an unknown answer still reaches
    /// the clients rather than silently holding the broadcast back.
    /// </summary>
    private bool SettingsDiffer(string? before, MoonfinUserSettings after)
    {
        var afterJson = SerializeForComparison(after);
        return before == null || afterJson == null || before != afterJson;
    }

    /// <summary>
    /// Serializes settings for change detection, leaving out the metadata
    /// stamped on every save since it would mask a merge that changed nothing
    /// else. Null when the settings cannot be read back as an object.
    /// </summary>
    private string? SerializeForComparison(MoonfinUserSettings settings)
    {
        if (JsonSerializer.SerializeToNode(settings, _jsonOptions) is not JsonObject node)
        {
            return null;
        }

        node.Remove("schemaVersion");
        node.Remove("lastUpdated");
        node.Remove("lastUpdatedBy");
        return node.ToJsonString();
    }

    public Channel<string> RegisterSseChannel(Guid userId)
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        var channels = _sseChannels.GetOrAdd(userId, _ => new ConcurrentDictionary<Channel<string>, byte>());
        channels[channel] = 0;
        return channel;
    }

    public void UnregisterSseChannel(Guid userId, Channel<string> channel)
    {
        if (_sseChannels.TryGetValue(userId, out var channels))
        {
            channels.TryRemove(channel, out _);
            if (channels.IsEmpty)
            {
                _sseChannels.TryRemove(userId, out _);
            }
        }

        channel.Writer.TryComplete();
    }

    public void NotifySettingsChanged(Guid userId)
    {
        NotifyUser(userId, JsonSerializer.Serialize(new { type = "settingsUpdated" }));
    }

    /// <summary>
    /// Writes a raw JSON payload to a single user's registered SSE channels.
    /// Returns the number of channels the payload was written to.
    /// </summary>
    public int NotifyUser(Guid userId, string jsonPayload)
    {
        if (string.IsNullOrEmpty(jsonPayload) || !_sseChannels.TryGetValue(userId, out var channels))
        {
            return 0;
        }

        var sent = 0;
        foreach (var channel in channels.Keys)
        {
            if (channel.Writer.TryWrite(jsonPayload))
            {
                sent++;
            }
        }

        return sent;
    }

    public int BroadcastMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return 0;
        }

        var payload = JsonSerializer.Serialize(new
        {
            type = "adminMessage",
            text = message
        });

        var sent = 0;
        foreach (var channels in _sseChannels.Values)
        {
            foreach (var channel in channels.Keys)
            {
                if (channel.Writer.TryWrite(payload))
                {
                    sent++;
                }
            }
        }

        return sent;
    }

    public int BroadcastSystemEvent(string eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            return 0;
        }

        var payload = JsonSerializer.Serialize(new { type = eventType.Trim() });
        var sent = 0;

        foreach (var channels in _sseChannels.Values)
        {
            foreach (var channel in channels.Keys)
            {
                if (channel.Writer.TryWrite(payload))
                {
                    sent++;
                }
            }
        }

        return sent;
    }

    private MoonfinUserSettings MigrateV1ToV2(MoonfinUserSettings v1)
    {
        var global = new MoonfinSettingsProfile();
        var profileProps = ProfileProps;
        var v1Props = UserSettingsProps;

        // Map matching property names from v1 flat fields into the global profile
        foreach (var profileProp in profileProps)
        {
            var v1Prop = Array.Find(v1Props, p => p.Name == profileProp.Name && p.DeclaringType == typeof(MoonfinUserSettings));
            if (v1Prop != null)
            {
                var value = v1Prop.GetValue(v1);
                if (value != null)
                {
                    profileProp.SetValue(global, value);
                }
            }
        }

        var v2 = new MoonfinUserSettings
        {
            SchemaVersion = 2,
            LastUpdated = v1.LastUpdated,
            LastUpdatedBy = v1.LastUpdatedBy,
            SyncEnabled = true,
            Global = global
        };

        // Clear legacy fields
        ClearLegacyFields(v2);

        return v2;
    }

    private void ClearLegacyFields(MoonfinUserSettings settings)
    {
        settings.SeerrEnabled = null;
        settings.SeerrApiKey = null;
        settings.SeerrRows = null;
        settings.MdblistEnabled = null;
        settings.MdblistApiKey = null;
        settings.MdblistRatingSources = null;
        settings.TmdbApiKey = null;
        settings.TmdbEpisodeRatingsEnabled = null;
        settings.NavbarEnabled = null;
        settings.DetailsPageEnabled = null;
        settings.DetailsBackdropOpacity = null;
        settings.DetailsBackdropBlur = null;
        settings.NavbarPosition = null;
        settings.ShowClock = null;
        settings.Use24HourClock = null;
        settings.ShowShuffleButton = null;
        settings.ShowGenresButton = null;
        settings.ShowFavoritesButton = null;
        settings.ShowCastButton = null;
        settings.ShowSyncPlayButton = null;
        settings.ShowLibrariesInToolbar = null;
        settings.ShuffleContentType = null;
        settings.MergeContinueWatchingNextUp = null;
        settings.NextUpMaxDays = null;
        settings.EnableMultiServerLibraries = null;
        settings.EnableFolderView = null;
        settings.ConfirmExit = null;
        settings.MediaBarEnabled = null;

        settings.MediaBarItemCount = null;
        settings.MediaBarOpacity = null;
        settings.MediaBarOverlayColor = null;
        settings.MediaBarAutoAdvance = null;
        settings.MediaBarIntervalMs = null;
        settings.MediaBarTrailerPreview = null;
        settings.MediaBarSourceType = null;
        settings.MediaBarCollectionIds = null;
        settings.MediaBarLibraryIds = null;
        settings.MediaBarExcludedGenres = null;
        settings.SeasonalSurprise = null;
        settings.BackdropEnabled = null;
        settings.HomeRowsImageTypeOverride = null;
        settings.HomeRowsImageType = null;
        settings.DetailsScreenBlur = null;
        settings.BrowsingBlur = null;
        settings.ThemeMusicEnabled = null;
        settings.ThemeMusicOnHomeRows = null;
        settings.ThemeMusicVolume = null;
        settings.BlockedRatings = null;
        settings.ClientSpecific = null;
    }

    private void MoveContentHidingToGlobal(MoonfinUserSettings settings)
    {
        if (settings.Global == null)
        {
            settings.Global = new MoonfinSettingsProfile();
        }

        // Content hiding is a global preference, so lift each device's hidden lists into the
        // global profile. Union across global and all devices so items hidden on any device
        // persist instead of subsequent device pushes overwriting the global list.
        var allProfiles = new[] { settings.Global, settings.Desktop, settings.Mobile, settings.Tv };

        var hiddenContinueWatching = UnionHiddenEntries(allProfiles, p => p.HiddenContinueWatchingItems);
        if (hiddenContinueWatching != null)
        {
            settings.Global.HiddenContinueWatchingItems = hiddenContinueWatching;
        }

        var hiddenNextUp = UnionHiddenEntries(allProfiles, p => p.HiddenNextUpSeries);
        if (hiddenNextUp != null)
        {
            settings.Global.HiddenNextUpSeries = hiddenNextUp;
        }

        var devices = new[] { settings.Desktop, settings.Mobile, settings.Tv };
        foreach (var device in devices)
        {
            if (device == null) continue;
            device.HiddenContinueWatchingItems = null;
            device.HiddenNextUpSeries = null;
        }
    }

    private static string? UnionHiddenEntries(
        MoonfinSettingsProfile?[] profiles,
        Func<MoonfinSettingsProfile, string?> selector)
    {
        Dictionary<string, string>? merged = null;
        foreach (var profile in profiles)
        {
            if (profile == null) continue;
            var value = selector(profile);
            if (value == null) continue;

            if (merged == null)
            {
                merged = new Dictionary<string, string>();
            }
            foreach (var pair in ParseHiddenEntries(value))
            {
                if (!merged.TryGetValue(pair.Key, out var existingDate) ||
                    string.Compare(pair.Value, existingDate, StringComparison.Ordinal) > 0)
                {
                    merged[pair.Key] = pair.Value;
                }
            }
        }

        return merged == null ? null : JsonSerializer.Serialize(merged);
    }

    private static Dictionary<string, string> ParseHiddenEntries(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(value)
                ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    private void MergeProfile(MoonfinSettingsProfile existing, MoonfinSettingsProfile incoming)
    {
        if (incoming.HomeSections == null && incoming.HomeRowOrder != null)
        {
            existing.HomeSections = null;
        }

        var properties = ProfileProps;
        foreach (var prop in properties)
        {
            var incomingValue = prop.GetValue(incoming);
            if (incomingValue == null)
            {
                continue;
            }

            // Older clients push empty API keys, which should not wipe a stored key.
            if (incomingValue is string text
                && prop.Name.Contains("ApiKey", StringComparison.Ordinal)
                && (string.IsNullOrWhiteSpace(text) || text == "null"))
            {
                continue;
            }

            prop.SetValue(existing, incomingValue);
        }
    }

    private MoonfinUserSettings MergeSettings(MoonfinUserSettings? existing, MoonfinUserSettings incoming)
    {
        if (existing == null)
        {
            return incoming;
        }

        // Merge metadata
        if (incoming.SyncEnabled != existing.SyncEnabled)
        {
            existing.SyncEnabled = incoming.SyncEnabled;
        }

        // Merge each profile
        if (incoming.Global != null)
        {
            if (existing.Global == null) existing.Global = incoming.Global;
            else MergeProfile(existing.Global, incoming.Global);
        }

        if (incoming.Desktop != null)
        {
            if (existing.Desktop == null) existing.Desktop = incoming.Desktop;
            else MergeProfile(existing.Desktop, incoming.Desktop);
        }

        if (incoming.Mobile != null)
        {
            if (existing.Mobile == null) existing.Mobile = incoming.Mobile;
            else MergeProfile(existing.Mobile, incoming.Mobile);
        }

        if (incoming.Tv != null)
        {
            if (existing.Tv == null) existing.Tv = incoming.Tv;
            else MergeProfile(existing.Tv, incoming.Tv);
        }

        // Also merge any legacy flat fields (from older clients)
        var props = UserSettingsProps;
        foreach (var prop in props)
        {
            if (prop.Name is "LastUpdated" or "LastUpdatedBy" or "SchemaVersion" or "SyncEnabled"
                or "Global" or "Desktop" or "Mobile" or "Tv" or "NeedsMigration")
            {
                continue;
            }

            var incomingValue = prop.GetValue(incoming);
            if (incomingValue != null)
            {
                prop.SetValue(existing, incomingValue);
            }
        }

        return existing;
    }

    private static bool IsCustomHomeSection(MoonfinHomeSectionConfig section) =>
        string.Equals(section.Kind, "pluginDynamic", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(section.PluginSource, "custom", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(section.PluginSection);

    // Custom rows belong to no server, and the client keys them on this, so stand in for
    // an empty value rather than letting the same row end up with two identities.
    private static string CustomHomeSectionServerId(MoonfinHomeSectionConfig section) =>
        string.IsNullOrWhiteSpace(section.ServerId) ? "custom" : section.ServerId!;

    private static Dictionary<string, MoonfinHomeSectionConfig> CustomHomeSections(
        MoonfinSettingsProfile? profile)
    {
        var sections = new Dictionary<string, MoonfinHomeSectionConfig>(StringComparer.OrdinalIgnoreCase);
        if (profile?.HomeSections == null) return sections;

        foreach (var section in profile.HomeSections)
        {
            if (IsCustomHomeSection(section)) sections.TryAdd(section.PluginSection!, section);
        }

        return sections;
    }

    private static HashSet<string> CustomHomeSectionIds(MoonfinSettingsProfile? profile) =>
        new(CustomHomeSections(profile).Keys, StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> CustomHomeSectionIds(MoonfinUserSettings? settings)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (settings == null) return ids;

        foreach (var profile in new[] { settings.Global, settings.Desktop, settings.Mobile, settings.Tv })
        {
            ids.UnionWith(CustomHomeSectionIds(profile));
        }

        return ids;
    }

    private static HashSet<string> RemovedCustomHomeSections(HashSet<string> before, HashSet<string> after)
    {
        var removed = new HashSet<string>(before, StringComparer.OrdinalIgnoreCase);
        removed.ExceptWith(after);
        return removed;
    }

    /// <summary>
    /// Mirrors custom rows onto the other device profiles so a row created on one device can
    /// be switched on from another. Rows listed in <c>removed</c> are taken out everywhere,
    /// because the copies sitting on the other profiles would otherwise put a row the user
    /// just deleted straight back. The <c>authoritative</c> profile is the one that was just
    /// written and its copy of a row wins, so editing a row on one device updates the copies
    /// the other devices hold.
    /// </summary>
    private static void PropagateCustomHomeSectionsAcrossProfiles(
        MoonfinUserSettings settings,
        MoonfinSettingsProfile? authoritative = null,
        HashSet<string>? removed = null)
    {
        var profiles = new[] { settings.Global, settings.Desktop, settings.Mobile, settings.Tv };

        if (removed is { Count: > 0 })
        {
            foreach (var profile in profiles)
            {
                profile?.HomeSections?.RemoveAll(
                    section => IsCustomHomeSection(section) && removed.Contains(section.PluginSection!));
            }
        }

        // The profile that was just written goes first so its copy of a row wins.
        var known = new Dictionary<string, MoonfinHomeSectionConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles.Prepend(authoritative))
        {
            foreach (var section in CustomHomeSections(profile))
            {
                known.TryAdd(section.Key, section.Value);
            }
        }

        if (known.Count == 0) return;

        foreach (var profile in profiles)
        {
            // A profile with no layout of its own falls back to the global or admin one, and
            // a list holding nothing but custom rows would end that fallback and leave the
            // device with no rows at all. Wait until it saves a layout of its own.
            if (profile?.HomeSections == null) continue;

            var present = CustomHomeSections(profile);

            foreach (var custom in known.Values)
            {
                if (present.TryGetValue(custom.PluginSection!, out var match))
                {
                    // Take the newer definition but leave on/off state and position alone,
                    // since those belong to the device.
                    match.ServerId = CustomHomeSectionServerId(custom);
                    match.PluginAdditionalData = custom.PluginAdditionalData;
                    match.PluginDisplayText = custom.PluginDisplayText;
                    continue;
                }

                profile.HomeSections.Add(new MoonfinHomeSectionConfig
                {
                    Kind = "pluginDynamic",
                    Type = "none",
                    Enabled = false,
                    Order = profile.HomeSections.Count,
                    ServerId = CustomHomeSectionServerId(custom),
                    PluginSource = "custom",
                    PluginSection = custom.PluginSection,
                    PluginAdditionalData = custom.PluginAdditionalData,
                    PluginDisplayText = custom.PluginDisplayText
                });
            }
        }
    }

    /// <summary>
    /// Resets every server user to a clean settings file containing only a global profile
    /// equal to the supplied admin defaults. Existing device profiles and personal
    /// customizations are discarded, and users without a settings file get one created.
    /// When <paramref name="deleteOrphans"/> is true, settings files belonging to users
    /// that no longer exist on the server are removed.
    /// </summary>
    public async Task<(int usersReset, int orphansDeleted)> ResetAllUsersToDefaultsAsync(
        MoonfinSettingsProfile defaults,
        IReadOnlyCollection<Guid> serverUserIds,
        bool deleteOrphans)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(serverUserIds);

        EnsureDataDirectory();

        var globalProfile = CloneProfile(defaults);
        var usersReset = 0;
        foreach (var userId in serverUserIds)
        {
            try
            {
                await WriteGlobalOnlyAsync(userId, globalProfile, "admin-reset-all");
                usersReset++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reset settings for user {UserId}", userId);
            }
        }

        var orphansDeleted = 0;
        if (deleteOrphans)
        {
            var keep = serverUserIds as HashSet<Guid> ?? new HashSet<Guid>(serverUserIds);
            foreach (var filePath in Directory.EnumerateFiles(_dataPath, "*.json", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileNameWithoutExtension(filePath);
                if (!Guid.TryParse(fileName, out var fileUserId) || keep.Contains(fileUserId))
                {
                    continue;
                }

                await _lock.WaitAsync();
                try
                {
                    if (File.Exists(filePath))
                    {
                        AtomicFile.DeleteWithSidecars(filePath);
                        orphansDeleted++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete orphan settings file {Path}", filePath);
                }
                finally
                {
                    _lock.Release();
                }
            }
        }

        return (usersReset, orphansDeleted);
    }

    /// <summary>
    /// Resets a single user to a clean settings file containing only a global profile
    /// equal to the supplied admin defaults, discarding their existing profiles.
    /// </summary>
    public async Task ResetUserToDefaultsAsync(Guid userId, MoonfinSettingsProfile defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);

        await WriteGlobalOnlyAsync(userId, CloneProfile(defaults), "admin-reset-user");
        NotifySettingsChanged(userId);
    }

    private async Task WriteGlobalOnlyAsync(Guid userId, MoonfinSettingsProfile globalProfile, string clientId)
    {
        var settings = new MoonfinUserSettings
        {
            SchemaVersion = 2,
            SyncEnabled = true,
            Global = globalProfile,
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LastUpdatedBy = clientId,
        };

        var filePath = GetUserSettingsPath(userId);

        await _lock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(settings, _jsonOptions);
            AtomicFile.WriteAllText(filePath, json);
        }
        finally
        {
            _lock.Release();
        }
    }

    private MoonfinSettingsProfile CloneProfile(MoonfinSettingsProfile source)
    {
        var json = JsonSerializer.Serialize(source, _jsonOptions);
        return JsonSerializer.Deserialize<MoonfinSettingsProfile>(json, _jsonOptions)
            ?? new MoonfinSettingsProfile();
    }

    /// <summary>
    /// Merges the supplied admin defaults into the global profile of every user that
    /// already has a settings file. Only fields set on the defaults are applied; each
    /// user's other global values and device profile overrides are preserved. Users
    /// without a settings file are not touched (they already resolve to admin defaults).
    /// </summary>
    public async Task<int> MergeDefaultsToAllUsersAsync(MoonfinSettingsProfile defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);

        if (!HasAnyProfileValues(defaults))
        {
            return 0;
        }

        EnsureDataDirectory();

        var usersUpdated = 0;
        foreach (var filePath in Directory.EnumerateFiles(_dataPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            if (!Guid.TryParse(fileName, out var userId))
            {
                continue;
            }

            try
            {
                // SaveProfileAsync mutates what it is handed, and the caller passes the live
                // admin defaults, so sharing it leaves one user's hidden items in those defaults.
                await SaveProfileAsync(
                    userId,
                    "global",
                    CloneProfile(defaults),
                    "admin-default-merge",
                    notifySettingsChanged: false);
                usersUpdated++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to merge admin defaults for user {UserId}", userId);
            }
        }

        return usersUpdated;
    }

    /// <summary>
    /// Merges the supplied admin defaults into a single user's global profile, keeping
    /// their other values and device profile overrides.
    /// </summary>
    public async Task MergeDefaultsToUserAsync(Guid userId, MoonfinSettingsProfile defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);

        if (!HasAnyProfileValues(defaults))
        {
            return;
        }

        // Cloned for the same reason as the all-users path.
        await SaveProfileAsync(userId, "global", CloneProfile(defaults), "admin-default-merge");
    }

    private static bool HasAnyProfileValues(MoonfinSettingsProfile profile)
    {
        foreach (var prop in ProfileProps)
        {
            if (prop.GetValue(profile) != null)
            {
                return true;
            }
        }

        return false;
    }

    public async Task DeleteUserSettingsAsync(Guid userId)
    {
        var filePath = GetUserSettingsPath(userId);

        await _lock.WaitAsync();
        try
        {
            // Takes the backup with it, otherwise a deleted user's settings could come back
            // through recovery.
            AtomicFile.DeleteWithSidecars(filePath);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteProfileAsync(Guid userId, string profileName)
    {
        if (profileName.ToLowerInvariant() == "global")
        {
            // Can't delete global profile - use DeleteUserSettingsAsync instead
            return;
        }

        var settings = await GetUserSettingsAsync(userId);
        if (settings == null) return;

        settings.SetProfile(profileName, null);

        await _lock.WaitAsync();
        try
        {
            var filePath = GetUserSettingsPath(userId);
            settings.LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var json = JsonSerializer.Serialize(settings, _jsonOptions);
            AtomicFile.WriteAllText(filePath, json);
        }
        finally
        {
            _lock.Release();
        }
    }

    public bool UserSettingsExist(Guid userId)
    {
        // Settings that only survive as a backup still count as existing.
        var path = GetUserSettingsPath(userId);
        return File.Exists(path) || File.Exists(AtomicFile.BackupPath(path));
    }

    /// <summary>
    /// One-time healing sweep over every stored settings file. Files corrupted by the
    /// in-place writes that shipped before AtomicFile are repaired through JsonSalvage when a
    /// usable prefix survives, and quarantined otherwise. Healthy files are left untouched.
    /// </summary>
    public async Task<HealSummary> HealDataFilesAsync(CancellationToken cancellationToken)
    {
        (bool Ok, string Healed) Salvage(string raw) =>
            (JsonSalvage.TrySalvage(raw, ValidateSalvagedEnvelope, out var healed), healed);

        var summary = await FileHealer.HealDirectoryAsync(
            _dataPath,
            Path.Combine(_dataPath, "quarantine", "settings"),
            _lock,
            ValidateSalvagedEnvelope,
            Salvage,
            cancellationToken).ConfigureAwait(false);

        foreach (var note in summary.Notes)
        {
            _logger.LogWarning("Settings heal: {Note}", note);
        }

        return summary;
    }

    // The floor a salvaged envelope must clear before it replaces the corrupt file: it has to
    // deserialize, carry a known schema version, and hold either a device profile or the v1
    // flat fields the lazy migration understands. Anything reduced to bare metadata is the
    // same as data loss, so it goes to quarantine where the bytes stay recoverable by hand.
    private bool ValidateSalvagedEnvelope(string text)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<MoonfinUserSettings>(text, _jsonOptions);
            if (envelope == null || envelope.SchemaVersion is < 1 or > 2)
            {
                return false;
            }

            return envelope.Global != null ||
                envelope.Desktop != null ||
                envelope.Mobile != null ||
                envelope.Tv != null ||
                envelope.NeedsMigration;
        }
        catch
        {
            return false;
        }
    }

    private void StripServerWideKeys(MoonfinSettingsProfile? profile)
    {
        if (profile == null) return;
        var config = MoonfinPlugin.Instance?.Configuration;
        if (config == null) return;

        if (profile.TmdbApiKey == config.TmdbApiKey)
        {
            profile.TmdbApiKey = null;
        }
        if (profile.MdblistApiKey == config.MdblistApiKey)
        {
            profile.MdblistApiKey = null;
        }
    }

    private void StripServerWideKeys(MoonfinUserSettings? settings)
    {
        if (settings == null) return;
        var config = MoonfinPlugin.Instance?.Configuration;
        if (config == null) return;

        if (settings.TmdbApiKey == config.TmdbApiKey)
        {
            settings.TmdbApiKey = null;
        }
        if (settings.MdblistApiKey == config.MdblistApiKey)
        {
            settings.MdblistApiKey = null;
        }

        StripServerWideKeys(settings.Global);
        StripServerWideKeys(settings.Desktop);
        StripServerWideKeys(settings.Mobile);
        StripServerWideKeys(settings.Tv);
    }
}
