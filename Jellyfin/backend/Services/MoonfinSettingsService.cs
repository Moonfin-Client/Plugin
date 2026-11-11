using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Threading.Channels;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Helpers;
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
    private readonly IServiceProvider? _serviceProvider;
    private static readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Channel<string>, byte>> _sseChannels = new();

    private static readonly Type? UserManagerType = Type.GetType("MediaBrowser.Controller.Library.IUserManager, MediaBrowser.Controller");

    private static readonly MethodInfo? GetUserDtoMethod = UserManagerType?
        .GetMethods()
        .Where(m => m.Name == "GetUserDto" && typeof(UserDto).IsAssignableFrom(m.ReturnType))
        .OrderBy(m => m.GetParameters().Length)
        .FirstOrDefault();

    private static readonly MethodInfo? UpdateConfigurationMethod = UserManagerType?
        .GetMethod("UpdateConfigurationAsync", [typeof(Guid), typeof(UserConfiguration)]);

    internal Func<bool, Task>? UpgradePreferenceChanged { get; set; }
    private int _userHideUpgradesCache = -1;
    private int _lastAppliedUpgradeState = -1;

    public void InvalidateHideUpgradesCache() => Volatile.Write(ref _userHideUpgradesCache, -1);

    // Resolve and merge reflect over every property on each call, and the profile carries a
    // couple of hundred of them, so the lookup is cached rather than repeated per request.
    private static readonly System.Reflection.PropertyInfo[] ProfileProps =
        typeof(MoonfinSettingsProfile).GetProperties();

    private static readonly System.Reflection.PropertyInfo[] UserSettingsProps =
        typeof(MoonfinUserSettings).GetProperties();

    // The admin page names settings by their JSON keys, which every profile property pins.
    private static readonly Dictionary<string, string> ProfileJsonNames = ProfileProps.ToDictionary(
        prop => prop.Name,
        prop => prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(prop.Name));

    // A user's own keys and hidden items rather than preferences, so a copy never hands them to someone else.
    private static readonly HashSet<string> PersonalProps =
    [
        nameof(MoonfinSettingsProfile.SeerrApiKey),
        nameof(MoonfinSettingsProfile.MdblistApiKey),
        nameof(MoonfinSettingsProfile.TmdbApiKey),
        nameof(MoonfinSettingsProfile.HiddenContinueWatchingItems),
        nameof(MoonfinSettingsProfile.HiddenNextUpSeries),
    ];

    // XmlSerializer loads an unset list default as an empty list, and the admin page never saves
    // an empty sources list, so an empty one in the admin defaults means the admin left it unset.
    private static readonly HashSet<string> RatingSourceLists =
    [
        nameof(MoonfinSettingsProfile.MdblistRatingSources),
        nameof(MoonfinSettingsProfile.HomeRowsMdblistRatingSources),
        nameof(MoonfinSettingsProfile.MediaBarMdblistRatingSources),
    ];

    public MoonfinSettingsService(ILogger<MoonfinSettingsService> logger, IServiceProvider? serviceProvider = null)
        : this(logger, MoonfinPlugin.ResolveDataFolderPath(), serviceProvider)
    {
    }

    /// <summary>Lets tests point the store at a temp directory.</summary>
    internal MoonfinSettingsService(ILogger<MoonfinSettingsService> logger, string dataPath, IServiceProvider? serviceProvider = null)
    {
        _logger = logger;
        _dataPath = dataPath;
        _serviceProvider = serviceProvider;

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

    /// <summary>
    /// Notes what a profile was just handed for the hidden content maps, which is what the next
    /// push from it gets merged against.
    /// </summary>
    public async Task RecordHiddenContentBaselineAsync(Guid userId, string profileName, MoonfinSettingsProfile resolved)
    {
        if (string.IsNullOrEmpty(profileName))
        {
            return;
        }

        var name = profileName.ToLowerInvariant();
        var filePath = GetUserSettingsPath(userId);

        await _lock.WaitAsync();
        try
        {
            var settings = ReadSettingsWithRecovery(filePath);
            if (settings == null)
            {
                return;
            }

            if (settings.HiddenContentBaselines != null &&
                settings.HiddenContentBaselines.TryGetValue(name, out var known) &&
                known.ContinueWatching == resolved.HiddenContinueWatchingItems &&
                known.NextUpSeries == resolved.HiddenNextUpSeries)
            {
                return;
            }

            var baseline = BaselineFor(settings, name);
            baseline.ContinueWatching = resolved.HiddenContinueWatchingItems;
            baseline.NextUpSeries = resolved.HiddenNextUpSeries;

            AtomicFile.WriteAllText(filePath, JsonSerializer.Serialize(settings, _jsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not record the hidden content baseline for {UserId}", userId);
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
    /// Resolves a flat profile: device override, then global, then admin device defaults, then admin defaults.
    /// </summary>
    public MoonfinSettingsProfile ResolveProfile(MoonfinUserSettings settings, string profileName)
        => ResolveProfile(settings, profileName, MoonfinPlugin.Instance?.Configuration);

    internal MoonfinSettingsProfile ResolveProfile(MoonfinUserSettings settings, string profileName, PluginConfiguration? config)
    {
        var global = settings.Global;
        var isDevice = IsDeviceProfile(profileName);
        var deviceProfile = isDevice ? settings.GetProfile(profileName) : null;
        var adminDefaults = config?.DefaultUserSettings;
        var adminDevice = isDevice ? config?.GetDefaultsFor(profileName) : null;

        var resolved = new MoonfinSettingsProfile();
        var properties = ProfileProps;

        foreach (var prop in properties)
        {
            var value = deviceProfile != null ? prop.GetValue(deviceProfile) : null;
            value ??= global != null ? prop.GetValue(global) : null;
            value ??= AdminDeviceValue(prop, adminDevice);
            if (value == null && adminDefaults != null)
            {
                value = prop.GetValue(adminDefaults);
                if (value is List<string> { Count: 0 } && RatingSourceLists.Contains(prop.Name))
                {
                    value = null;
                }
            }

            if (value != null)
            {
                prop.SetValue(resolved, value);
            }
        }

        var homeLayout = ResolveHomeLayout(deviceProfile, global, HasHomeLayout(adminDevice) ? adminDevice : null, adminDefaults);
        resolved.HomeSections = homeLayout.HomeSections;
        resolved.HomeRowOrder = homeLayout.HomeRowOrder;

        foreach (var sources in new[] { resolved.MdblistRatingSources, resolved.HomeRowsMdblistRatingSources, resolved.MediaBarMdblistRatingSources })
        {
            if (sources == null) continue;
            for (var i = 0; i < sources.Count; i++)
            {
                if (string.Equals(sources[i], "rtAudience", StringComparison.OrdinalIgnoreCase))
                {
                    sources[i] = "tomatoes_audience";
                }
            }
        }

        if (string.IsNullOrWhiteSpace(resolved.TmdbApiKey))
        {
            resolved.TmdbApiKey = config?.TmdbApiKey;
        }

        if (string.IsNullOrWhiteSpace(resolved.MdblistApiKey))
        {
            resolved.MdblistApiKey = config?.MdblistApiKey;
        }

        return resolved;
    }

    /// <summary>
    /// What a profile resolves to for a user with no settings of their own: the device's admin
    /// defaults over the global ones. Global, or no device, is the global defaults as stored.
    /// </summary>
    public MoonfinSettingsProfile? AdminDefaultsFor(string? profileName)
        => AdminDefaultsFor(profileName, MoonfinPlugin.Instance?.Configuration);

    internal static MoonfinSettingsProfile? AdminDefaultsFor(string? profileName, PluginConfiguration? config)
    {
        var global = config?.DefaultUserSettings;
        var device = IsDeviceProfile(profileName) ? config?.GetDefaultsFor(profileName) : null;
        if (device == null)
        {
            return global;
        }

        var merged = new MoonfinSettingsProfile();
        foreach (var prop in ProfileProps)
        {
            var value = AdminDeviceValue(prop, device) ?? (global != null ? prop.GetValue(global) : null);
            if (value != null)
            {
                prop.SetValue(merged, value);
            }
        }

        // The home layout moves as one unit, the same as in ResolveProfile.
        var layout = HasHomeLayout(device) ? device : global;
        merged.HomeSections = layout?.HomeSections;
        merged.HomeRowOrder = layout?.HomeRowOrder;
        return merged;
    }

    private static bool IsDeviceProfile(string? profileName) =>
        !string.IsNullOrEmpty(profileName) && !string.Equals(profileName, "global", StringComparison.OrdinalIgnoreCase);

    // XmlSerializer loads every list a device's defaults never set as an empty one, and an empty
    // list there would hide the global default, so it counts as not set.
    private static object? AdminDeviceValue(PropertyInfo prop, MoonfinSettingsProfile? deviceDefaults)
    {
        var value = deviceDefaults != null ? prop.GetValue(deviceDefaults) : null;
        return value is System.Collections.ICollection { Count: 0 } ? null : value;
    }

    private static bool HasHomeLayout(MoonfinSettingsProfile? profile) =>
        profile != null && (profile.HomeSections is { Count: > 0 } || profile.HomeRowOrder is { Count: > 0 });

    private static (List<MoonfinHomeSectionConfig>? HomeSections, List<string>? HomeRowOrder) ResolveHomeLayout(
        MoonfinSettingsProfile? deviceProfile,
        MoonfinSettingsProfile? global,
        MoonfinSettingsProfile? adminDevice,
        MoonfinSettingsProfile? adminDefaults)
    {
        foreach (var profile in new[] { deviceProfile, global, adminDevice, adminDefaults })
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
        MoonfinUserSettings? savedSnapshot = null;

        await _lock.WaitAsync();
        try
        {
            MoonfinUserSettings finalSettings;
            var customSectionsBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? beforeComparisonJson = null;
            string? storedHiddenContinueWatching = null;
            string? storedHiddenNextUp = null;

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
                storedHiddenContinueWatching = existingSettings?.Global?.HiddenContinueWatchingItems;
                storedHiddenNextUp = existingSettings?.Global?.HiddenNextUpSeries;
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

            MoveContentHidingToGlobal(finalSettings, storedHiddenContinueWatching, storedHiddenNextUp);
            PropagateCustomHomeSectionsAcrossProfiles(
                finalSettings,
                removed: RemovedCustomHomeSections(
                    customSectionsBefore,
                    CustomHomeSectionIds(finalSettings)));

            settingsChanged = SettingsDiffer(beforeComparisonJson, finalSettings);
            savedSnapshot = finalSettings;

            var json = JsonSerializer.Serialize(finalSettings, _jsonOptions);
            AtomicFile.WriteAllText(filePath, json);
            InvalidateHideUpgradesCache();
        }
        finally
        {
            _lock.Release();
        }

        if (savedSnapshot != null)
        {
            await SyncRecentRowPreferencesAsync(userId, savedSnapshot, null).ConfigureAwait(false);
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
          bool notifySettingsChanged = true,
          bool replace = false)
    {
        var filePath = GetUserSettingsPath(userId);
        bool settingsChanged;
        MoonfinUserSettings? savedSnapshot = null;

        await _lock.WaitAsync();
        try
        {
            var settings = ReadSettingsWithRecovery(filePath) ?? new MoonfinUserSettings();

            if (settings.NeedsMigration)
            {
                settings = MigrateV1ToV2(settings);
            }

            var beforeComparisonJson = SerializeForComparison(settings);
            var storedHiddenContinueWatching = settings.Global?.HiddenContinueWatchingItems;
            var storedHiddenNextUp = settings.Global?.HiddenNextUpSeries;

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

            if (existingProfile != null && replace)
            {
                // A replace swaps the preferences only, the user's own keys and hidden items stay.
                foreach (var prop in ProfileProps)
                {
                    if (PersonalProps.Contains(prop.Name))
                    {
                        prop.SetValue(profile, prop.GetValue(existingProfile));
                    }
                }

                settings.SetProfile(profileName, profile);
                existingProfile = null;
            }
            else if (existingProfile != null)
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

            MoveContentHidingToGlobal(settings, storedHiddenContinueWatching, storedHiddenNextUp);
            PropagateCustomHomeSectionsAcrossProfiles(
                settings,
                savedProfile,
                RemovedCustomHomeSections(
                    customSectionsBefore,
                    CustomHomeSectionIds(savedProfile)));

            settingsChanged = SettingsDiffer(beforeComparisonJson, settings);
            savedSnapshot = settings;

            var serialized = JsonSerializer.Serialize(settings, _jsonOptions);
            AtomicFile.WriteAllText(filePath, serialized);
            InvalidateHideUpgradesCache();
        }
        finally
        {
            _lock.Release();
        }

        if (savedSnapshot != null)
        {
            await SyncRecentRowPreferencesAsync(userId, savedSnapshot, profileName).ConfigureAwait(false);
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
        node.Remove("hiddenContentBaselines");
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

    private static readonly (string Name, Func<MoonfinUserSettings, MoonfinSettingsProfile?> Get)[] HidingProfiles =
    {
        ("global", s => s.Global),
        ("desktop", s => s.Desktop),
        ("mobile", s => s.Mobile),
        ("tv", s => s.Tv),
    };

    /// <summary>
    /// Content hiding is a global preference, so each profile's hidden lists get lifted into the
    /// global one. Everything a profile pushed is merged in, anything its baseline still lists
    /// that the push leaves out counts as unhidden and is dropped, and hides it hasn't pulled yet
    /// are left alone. The stored global maps are passed in because a push to the global profile
    /// overwrites them in place before this runs.
    /// </summary>
    private static void MoveContentHidingToGlobal(
        MoonfinUserSettings settings,
        string? storedContinueWatching,
        string? storedNextUp)
    {
        settings.Global ??= new MoonfinSettingsProfile();

        var hiddenContinueWatching = MergeHiddenEntries(
            settings,
            storedContinueWatching,
            p => p.HiddenContinueWatchingItems,
            b => b.ContinueWatching,
            (b, v) => b.ContinueWatching = v);

        if (hiddenContinueWatching != null)
        {
            settings.Global.HiddenContinueWatchingItems = hiddenContinueWatching;
        }

        var hiddenNextUp = MergeHiddenEntries(
            settings,
            storedNextUp,
            p => p.HiddenNextUpSeries,
            b => b.NextUpSeries,
            (b, v) => b.NextUpSeries = v);

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

    /// <summary>
    /// Folds every profile's push of one hidden map into the stored global value. Null when no
    /// profile pushed the field, so a partial save leaves what is on file alone.
    /// </summary>
    private static string? MergeHiddenEntries(
        MoonfinUserSettings settings,
        string? stored,
        Func<MoonfinSettingsProfile, string?> selector,
        Func<HiddenContentBaseline, string?> readBaseline,
        Action<HiddenContentBaseline, string?> writeBaseline)
    {
        var merged = ParseHiddenEntries(stored);
        var pushed = false;

        foreach (var (name, get) in HidingProfiles)
        {
            var profile = get(settings);
            if (profile == null) continue;

            var value = selector(profile);
            if (value == null) continue;

            // The global profile is where the stored map lives, so it only counts as a push when
            // a client sent something other than what was already on file.
            if (name == "global" && value == stored) continue;

            pushed = true;
            var entries = ParseHiddenEntries(value);

            string? baseline = null;
            if (settings.HiddenContentBaselines != null &&
                settings.HiddenContentBaselines.TryGetValue(name, out var known))
            {
                baseline = readBaseline(known);
            }

            foreach (var key in ParseHiddenEntries(baseline).Keys)
            {
                if (!entries.ContainsKey(key))
                {
                    merged.Remove(key);
                }
            }

            foreach (var pair in entries)
            {
                if (!merged.TryGetValue(pair.Key, out var existingDate) ||
                    IsLaterHide(pair.Value, existingDate))
                {
                    merged[pair.Key] = pair.Value;
                }
            }

            writeBaseline(BaselineFor(settings, name), value);
        }

        return pushed ? JsonSerializer.Serialize(merged) : null;
    }

    private static HiddenContentBaseline BaselineFor(MoonfinUserSettings settings, string profileName)
    {
        settings.HiddenContentBaselines ??= new Dictionary<string, HiddenContentBaseline>();

        if (!settings.HiddenContentBaselines.TryGetValue(profileName, out var baseline))
        {
            baseline = new HiddenContentBaseline();
            settings.HiddenContentBaselines[profileName] = baseline;
        }

        return baseline;
    }

    /// <summary>
    /// Whether one hide timestamp is later than another. Clients write ISO-8601, but the field is
    /// free-form, so anything that won't parse falls back to comparing the text.
    /// </summary>
    private static bool IsLaterHide(string candidate, string existing)
    {
        if (DateTimeOffset.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var candidateAt) &&
            DateTimeOffset.TryParse(existing, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var existingAt))
        {
            return candidateAt > existingAt;
        }

        return string.Compare(candidate, existing, StringComparison.Ordinal) > 0;
    }

    private static Dictionary<string, string> ParseHiddenEntries(string? value)
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
    /// Resets users to clean settings files holding only the admin defaults of the given profiles.
    /// Their existing profiles and personal customizations are discarded, and users without a
    /// settings file get one. When <paramref name="deleteOrphans"/> is true, settings files
    /// belonging to users that no longer exist on the server are removed.
    /// </summary>
    public async Task<(int usersReset, int orphansDeleted)> ResetUsersToDefaultsAsync(
        PluginConfiguration config,
        IReadOnlyCollection<string> profiles,
        IReadOnlyCollection<Guid> userIds,
        bool deleteOrphans)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(userIds);

        EnsureDataDirectory();

        var push = DefaultsToPush(config, profiles, keys: null);
        var usersReset = 0;
        foreach (var userId in userIds)
        {
            try
            {
                await WriteDefaultsOnlyAsync(userId, push, "admin-reset");
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
            var keep = new HashSet<Guid>(userIds);
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
                        InvalidateHideUpgradesCache();
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

        await SyncUpgradePreferenceStateIfNeededAsync(Guid.Empty).ConfigureAwait(false);
        return (usersReset, orphansDeleted);
    }

    private async Task WriteDefaultsOnlyAsync(
        Guid userId,
        IReadOnlyDictionary<string, MoonfinSettingsProfile> push,
        string clientId)
    {
        var settings = new MoonfinUserSettings
        {
            SchemaVersion = 2,
            SyncEnabled = true,
            Global = push.TryGetValue("global", out var global) ? CloneProfile(global) : new MoonfinSettingsProfile(),
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LastUpdatedBy = clientId,
        };

        foreach (var (profileName, profile) in push)
        {
            if (IsDeviceProfile(profileName))
            {
                settings.SetProfile(profileName, CloneProfile(profile));
            }
        }

        var filePath = GetUserSettingsPath(userId);

        await _lock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(settings, _jsonOptions);
            AtomicFile.WriteAllText(filePath, json);
            InvalidateHideUpgradesCache();
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
    /// Merges admin defaults into users' existing settings. Only the values set in the pushed
    /// profiles are written, narrowed to <paramref name="keys"/> when given, and each user keeps
    /// their own values for the rest. Null <paramref name="userIds"/> means every user with a
    /// settings file, since the others already resolve to the admin defaults.
    /// </summary>
    public async Task<int> MergeDefaultsAsync(
        PluginConfiguration config,
        IReadOnlyCollection<string> profiles,
        IReadOnlyCollection<string>? keys,
        IReadOnlyCollection<Guid>? userIds)
    {
        ArgumentNullException.ThrowIfNull(config);

        var push = DefaultsToPush(config, profiles, keys);
        if (push.Count == 0)
        {
            return 0;
        }

        // Every device's own defaults, pushed or not, so Global's never lands where a device differs.
        var deviceDefaults = MoonfinUserSettings.ValidProfiles
            .Where(IsDeviceProfile)
            .ToDictionary(name => name, name => config.GetDefaultsFor(name) is { } own ? SetDefaultsOnly(own) : null);

        EnsureDataDirectory();

        var usersUpdated = 0;
        foreach (var userId in userIds ?? StoredUserIds())
        {
            try
            {
                await MergeDefaultsIntoProfilesAsync(userId, push, deviceDefaults);
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
    /// A device profile wins over global, so Global's defaults also go into each device profile the
    /// user already has, except where that device has defaults of its own. A device's own defaults
    /// create its profile when the user has none, or they would never reach that device.
    /// </summary>
    private async Task MergeDefaultsIntoProfilesAsync(
        Guid userId,
        IReadOnlyDictionary<string, MoonfinSettingsProfile> push,
        IReadOnlyDictionary<string, MoonfinSettingsProfile?> deviceDefaults)
    {
        var existing = await GetUserSettingsAsync(userId);
        push.TryGetValue("global", out var global);
        foreach (var profileName in MoonfinUserSettings.ValidProfiles)
        {
            MoonfinSettingsProfile? layer;
            if (!IsDeviceProfile(profileName))
            {
                layer = global;
            }
            else
            {
                layer = global != null && existing?.GetProfile(profileName) != null
                    ? Without(global, deviceDefaults[profileName])
                    : null;
                if (push.TryGetValue(profileName, out var own))
                {
                    layer = Overlay(layer, own);
                }
            }

            if (layer == null || !HasAnyProfileValues(layer))
            {
                continue;
            }

            // SaveProfileAsync changes what it's handed, so each profile gets its own copy.
            await SaveProfileAsync(
                userId,
                profileName,
                CloneProfile(layer),
                "admin-default-merge",
                notifySettingsChanged: false);
        }
    }

    /// <summary>
    /// Copies one user's saved profiles onto other users. Merge keeps each target's values for the
    /// settings the copy leaves out, replace makes every copied profile an exact copy. Personal data
    /// such as API keys and hidden items always stays with its owner.
    /// </summary>
    public async Task<int> CopySettingsAsync(
        Guid fromUserId,
        IReadOnlyCollection<Guid> toUserIds,
        IReadOnlyCollection<string> profiles,
        IReadOnlyCollection<string>? keys,
        bool replace)
    {
        var source = await GetUserSettingsAsync(fromUserId);
        if (source == null)
        {
            return 0;
        }

        var copies = new Dictionary<string, MoonfinSettingsProfile>();
        foreach (var profileName in profiles)
        {
            var profile = IsDeviceProfile(profileName) ? source.GetProfile(profileName) : source.Global;
            if (profile == null)
            {
                continue;
            }

            var copy = KeepOnly(CloneProfile(profile), keys);
            foreach (var prop in ProfileProps)
            {
                if (PersonalProps.Contains(prop.Name))
                {
                    prop.SetValue(copy, null);
                }
            }

            if (replace || HasAnyProfileValues(copy))
            {
                copies[profileName.ToLowerInvariant()] = copy;
            }
        }

        if (copies.Count == 0)
        {
            return 0;
        }

        var usersUpdated = 0;
        foreach (var userId in toUserIds)
        {
            if (userId == fromUserId)
            {
                continue;
            }

            try
            {
                foreach (var (profileName, copy) in copies)
                {
                    await SaveProfileAsync(userId, profileName, CloneProfile(copy), "admin-copy", notifySettingsChanged: false, replace: replace);
                }

                NotifySettingsChanged(userId);
                usersUpdated++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to copy settings to user {UserId}", userId);
            }
        }

        return usersUpdated;
    }

    /// <summary>
    /// The admin defaults to push into each of <paramref name="profiles"/>, without the values they
    /// leave unset and narrowed to <paramref name="keys"/>. A profile left with nothing is skipped.
    /// </summary>
    private Dictionary<string, MoonfinSettingsProfile> DefaultsToPush(
        PluginConfiguration config,
        IEnumerable<string> profiles,
        IReadOnlyCollection<string>? keys)
    {
        var push = new Dictionary<string, MoonfinSettingsProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var profileName in profiles)
        {
            var defaults = config.GetDefaultsFor(profileName);
            if (defaults == null)
            {
                continue;
            }

            var copy = KeepOnly(SetDefaultsOnly(defaults), keys);
            if (HasAnyProfileValues(copy))
            {
                push[profileName.ToLowerInvariant()] = copy;
            }
        }

        return push;
    }

    private List<Guid> StoredUserIds()
    {
        var ids = new List<Guid>();
        foreach (var filePath in Directory.EnumerateFiles(_dataPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (Guid.TryParse(Path.GetFileNameWithoutExtension(filePath), out var userId))
            {
                ids.Add(userId);
            }
        }

        return ids;
    }

    /// <summary>A copy of <paramref name="source"/> without the settings <paramref name="mask"/> sets.</summary>
    private MoonfinSettingsProfile Without(MoonfinSettingsProfile source, MoonfinSettingsProfile? mask)
    {
        var copy = CloneProfile(source);
        if (mask == null)
        {
            return copy;
        }

        foreach (var prop in ProfileProps)
        {
            if (prop.GetValue(mask) != null)
            {
                prop.SetValue(copy, null);
            }
        }

        if (HasHomeLayout(mask))
        {
            copy.HomeSections = null;
            copy.HomeRowOrder = null;
        }

        return copy;
    }

    /// <summary>Writes every value <paramref name="over"/> sets onto <paramref name="under"/>, or onto a new profile when there's none.</summary>
    private static MoonfinSettingsProfile Overlay(MoonfinSettingsProfile? under, MoonfinSettingsProfile over)
    {
        var result = under ?? new MoonfinSettingsProfile();
        foreach (var prop in ProfileProps)
        {
            var value = prop.GetValue(over);
            if (value != null)
            {
                prop.SetValue(result, value);
            }
        }

        if (HasHomeLayout(over))
        {
            result.HomeSections = over.HomeSections;
            result.HomeRowOrder = over.HomeRowOrder;
        }

        return result;
    }

    /// <summary>Clears every setting of <paramref name="profile"/> outside <paramref name="keys"/>, by their JSON names.</summary>
    private static MoonfinSettingsProfile KeepOnly(MoonfinSettingsProfile profile, IReadOnlyCollection<string>? keys)
    {
        if (keys == null)
        {
            return profile;
        }

        var keep = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        foreach (var prop in ProfileProps)
        {
            if (!keep.Contains(ProfileJsonNames[prop.Name]))
            {
                prop.SetValue(profile, null);
            }
        }

        return profile;
    }

    /// <summary>
    /// A copy of the defaults without their empty lists. XmlSerializer loads every list the admin
    /// never set as an empty one, so an empty list counts as not set rather than wiping a user's own.
    /// </summary>
    private MoonfinSettingsProfile SetDefaultsOnly(MoonfinSettingsProfile defaults)
    {
        var copy = CloneProfile(defaults);
        foreach (var prop in ProfileProps)
        {
            if (prop.GetValue(copy) is System.Collections.ICollection { Count: 0 })
            {
                prop.SetValue(copy, null);
            }
        }

        return copy;
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
            InvalidateHideUpgradesCache();
        }
        finally
        {
            _lock.Release();
        }

        await SyncUpgradePreferenceStateIfNeededAsync(userId).ConfigureAwait(false);
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
            InvalidateHideUpgradesCache();
        }
        finally
        {
            _lock.Release();
        }

        await SyncUpgradePreferenceStateIfNeededAsync(userId).ConfigureAwait(false);
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

    public async Task<List<(Guid UserId, MoonfinUserSettings Settings)>> GetAllUserSettingsAsync()
    {
        var result = new List<(Guid, MoonfinUserSettings)>();
        if (!Directory.Exists(_dataPath)) return result;

        foreach (var file in Directory.EnumerateFiles(_dataPath, "*.json"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (Guid.TryParse(fileName, out var userId))
            {
                var settings = await GetUserSettingsAsync(userId);
                if (settings != null)
                {
                    result.Add((userId, settings));
                }
            }
        }

        return result;
    }

    public bool IsAnyHideUpgradesEnabled()
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        if (config?.DefaultUserSettings?.HideUpgradesInRecentRows == true ||
            config?.GetDefaultsFor("tv")?.HideUpgradesInRecentRows == true ||
            config?.GetDefaultsFor("desktop")?.HideUpgradesInRecentRows == true ||
            config?.GetDefaultsFor("mobile")?.HideUpgradesInRecentRows == true)
        {
            return true;
        }

        var cached = Volatile.Read(ref _userHideUpgradesCache);
        if (cached != -1)
        {
            return cached == 1;
        }

        if (!Directory.Exists(_dataPath))
        {
            Volatile.Write(ref _userHideUpgradesCache, 0);
            return false;
        }

        var anyUserEnabled = false;
        foreach (var file in Directory.EnumerateFiles(_dataPath, "*.json"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (!Guid.TryParse(fileName, out _))
            {
                continue;
            }

            var settings = ReadSettingsWithRecovery(file);
            if (settings == null)
            {
                continue;
            }

            if (settings.Global?.HideUpgradesInRecentRows == true ||
                settings.Tv?.HideUpgradesInRecentRows == true ||
                settings.Desktop?.HideUpgradesInRecentRows == true ||
                settings.Mobile?.HideUpgradesInRecentRows == true)
            {
                anyUserEnabled = true;
                break;
            }
        }

        Volatile.Write(ref _userHideUpgradesCache, anyUserEnabled ? 1 : 0);
        return anyUserEnabled;
    }

    private async Task SyncRecentRowPreferencesAsync(Guid userId, MoonfinUserSettings settings, string? activeProfileName)
    {
        var resolved = ResolveProfile(settings, string.IsNullOrEmpty(activeProfileName) ? "global" : activeProfileName);
        var hideWatched = resolved.HideWatchedInRecentRows
            ?? settings.Global?.HideWatchedInRecentRows
            ?? settings.Tv?.HideWatchedInRecentRows
            ?? settings.Desktop?.HideWatchedInRecentRows
            ?? settings.Mobile?.HideWatchedInRecentRows;

        if (hideWatched.HasValue && _serviceProvider != null && GetUserDtoMethod != null && UpdateConfigurationMethod != null)
        {
            try
            {
                var userManager = UserReflection.UserManager(_serviceProvider);
                var user = UserReflection.ResolveUser(_serviceProvider, userId);
                if (userManager != null && user != null)
                {
                    var args = new object?[GetUserDtoMethod.GetParameters().Length];
                    args[0] = user;
                    var userConfig = (GetUserDtoMethod.Invoke(userManager, args) as UserDto)?.Configuration;
                    if (userConfig != null && userConfig.HidePlayedInLatest != hideWatched.Value)
                    {
                        userConfig.HidePlayedInLatest = hideWatched.Value;
                        if (UpdateConfigurationMethod.Invoke(userManager, [userId, userConfig]) is Task updateTask)
                        {
                            await updateTask.ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not sync HidePlayedInLatest for user {UserId}", userId);
            }
        }

        await SyncUpgradePreferenceStateIfNeededAsync(userId).ConfigureAwait(false);
    }

    private async Task SyncUpgradePreferenceStateIfNeededAsync(Guid userId)
    {
        var callback = UpgradePreferenceChanged;
        if (callback == null)
        {
            return;
        }

        var effectiveServerWide = IsAnyHideUpgradesEnabled();
        var targetState = effectiveServerWide ? 1 : 0;
        var previousState = Interlocked.Exchange(ref _lastAppliedUpgradeState, targetState);
        if (previousState == targetState)
        {
            return;
        }

        try
        {
            await Task.Run(() => callback(effectiveServerWide)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _lastAppliedUpgradeState, -1, targetState);
            _logger.LogDebug(ex, "Could not apply upgrade date preference for user {UserId}", userId);
        }
    }
}
