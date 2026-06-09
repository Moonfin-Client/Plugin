using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Text.Json;
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

    public MoonfinSettingsService(ILogger<MoonfinSettingsService> logger)
    {
        _logger = logger;
        _dataPath = MoonfinPlugin.Instance?.DataFolderPath 
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jellyfin", "plugins", "Moonfin");
        
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

    public async Task<MoonfinUserSettings?> GetUserSettingsAsync(Guid userId)
    {
        var filePath = GetUserSettingsPath(userId);
        
        if (!File.Exists(filePath))
        {
            return null;
        }

        await _lock.WaitAsync();
        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var settings = JsonSerializer.Deserialize<MoonfinUserSettings>(json, _jsonOptions);

            if (settings != null && settings.NeedsMigration)
            {
                _logger.LogInformation("Migrating v1 settings to v2 for user {UserId}", userId);
                settings = MigrateV1ToV2(settings);

                // Persist the migrated version
                var migratedJson = JsonSerializer.Serialize(settings, _jsonOptions);
                await File.WriteAllTextAsync(filePath, migratedJson);
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
        var properties = typeof(MoonfinSettingsProfile).GetProperties();

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

        return resolved;
    }

    public async Task SaveUserSettingsAsync(Guid userId, MoonfinUserSettings settings, string? clientId = null, string mergeMode = "merge")
    {
        var filePath = GetUserSettingsPath(userId);

        await _lock.WaitAsync();
        try
        {
            MoonfinUserSettings finalSettings;

            if (mergeMode == "merge" && File.Exists(filePath))
            {
                var existingJson = await File.ReadAllTextAsync(filePath);
                var existingSettings = JsonSerializer.Deserialize<MoonfinUserSettings>(existingJson, _jsonOptions);

                // Migrate v1 if needed
                if (existingSettings != null && existingSettings.NeedsMigration)
                {
                    existingSettings = MigrateV1ToV2(existingSettings);
                }

                finalSettings = MergeSettings(existingSettings, settings);
            }
            else
            {
                finalSettings = settings;
            }

            // Update metadata
            finalSettings.LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            finalSettings.LastUpdatedBy = clientId ?? "unknown";
            finalSettings.SchemaVersion = 2;

            var json = JsonSerializer.Serialize(finalSettings, _jsonOptions);
            await File.WriteAllTextAsync(filePath, json);
        }
        finally
        {
            _lock.Release();
        }

          NotifySettingsChanged(userId);
    }

      public async Task SaveProfileAsync(
          Guid userId,
          string profileName,
          MoonfinSettingsProfile profile,
          string? clientId = null,
          bool notifySettingsChanged = true)
    {
        var filePath = GetUserSettingsPath(userId);

        await _lock.WaitAsync();
        try
        {
            MoonfinUserSettings settings;

            if (File.Exists(filePath))
            {
                var json = await File.ReadAllTextAsync(filePath);
                settings = JsonSerializer.Deserialize<MoonfinUserSettings>(json, _jsonOptions) ?? new MoonfinUserSettings();

                if (settings.NeedsMigration)
                {
                    settings = MigrateV1ToV2(settings);
                }
            }
            else
            {
                settings = new MoonfinUserSettings();
            }

            // Merge profile properties
            var existingProfile = profileName.ToLowerInvariant() == "global" 
                ? settings.Global 
                : settings.GetProfile(profileName);

            if (existingProfile != null)
            {
                MergeProfile(existingProfile, profile);
            }
            else
            {
                settings.SetProfile(profileName, profile);
            }

            // Update metadata
            settings.LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            settings.LastUpdatedBy = clientId ?? "unknown";
            settings.SchemaVersion = 2;

            var serialized = JsonSerializer.Serialize(settings, _jsonOptions);
            await File.WriteAllTextAsync(filePath, serialized);
        }
        finally
        {
            _lock.Release();
        }

        if (notifySettingsChanged)
        {
            NotifySettingsChanged(userId);
        }
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
        if (!_sseChannels.TryGetValue(userId, out var channels))
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new { type = "settingsUpdated" });

        foreach (var channel in channels.Keys)
        {
            channel.Writer.TryWrite(payload);
        }
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
        var profileProps = typeof(MoonfinSettingsProfile).GetProperties();
        var v1Props = typeof(MoonfinUserSettings).GetProperties();

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
        settings.JellyseerrEnabled = null;
        settings.JellyseerrApiKey = null;
        settings.JellyseerrRows = null;
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

    private void MergeProfile(MoonfinSettingsProfile existing, MoonfinSettingsProfile incoming)
    {
        var properties = typeof(MoonfinSettingsProfile).GetProperties();
        foreach (var prop in properties)
        {
            var incomingValue = prop.GetValue(incoming);
            if (incomingValue != null)
            {
                prop.SetValue(existing, incomingValue);
            }
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
        var props = typeof(MoonfinUserSettings).GetProperties();
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
                        File.Delete(filePath);
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
            await File.WriteAllTextAsync(filePath, json);
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
                await SaveProfileAsync(
                    userId,
                    "global",
                    defaults,
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

        await SaveProfileAsync(userId, "global", defaults, "admin-default-merge");
    }

    private static bool HasAnyProfileValues(MoonfinSettingsProfile profile)
    {
        foreach (var prop in typeof(MoonfinSettingsProfile).GetProperties())
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
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
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
            await File.WriteAllTextAsync(filePath, json);
        }
        finally
        {
            _lock.Release();
        }
    }

    public bool UserSettingsExist(Guid userId)
    {
        return File.Exists(GetUserSettingsPath(userId));
    }
}
