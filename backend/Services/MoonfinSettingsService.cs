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
    }

    public async Task SaveProfileAsync(Guid userId, string profileName, MoonfinSettingsProfile profile, string? clientId = null)
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
        settings.MediaBarContentType = null;
        settings.MediaBarItemCount = null;
        settings.MediaBarOpacity = null;
        settings.MediaBarOverlayColor = null;
        settings.MediaBarAutoAdvance = null;
        settings.MediaBarIntervalMs = null;
        settings.MediaBarTrailerPreview = null;
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
