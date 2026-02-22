using System.Net.Mime;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Models;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// API controller for Moonfin settings synchronization.
/// </summary>
[ApiController]
[Route("Moonfin")]
[Produces(MediaTypeNames.Application.Json)]
public class MoonfinController : ControllerBase
{
    private readonly MoonfinSettingsService _settingsService;
    private readonly IHttpClientFactory _httpClientFactory;
    
    // Cache for auto-detected variant
    private static string? _cachedVariant;
    private static string? _cachedVariantUrl;
    private static DateTime _variantCacheExpiry = DateTime.MinValue;
    private static readonly SemaphoreSlim _variantLock = new(1, 1);

    public MoonfinController(MoonfinSettingsService settingsService, IHttpClientFactory httpClientFactory)
    {
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Ping endpoint to check if Moonfin plugin is installed.
    /// </summary>
    /// <returns>Plugin status information including admin defaults.</returns>
    [HttpGet("Ping")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<MoonfinPingResponse> Ping()
    {
        var config = MoonfinPlugin.Instance?.Configuration;

        return Ok(new MoonfinPingResponse
        {
            Installed = true,
            Version = MoonfinPlugin.Instance?.Version.ToString() ?? "1.0.0.0",
            SettingsSyncEnabled = config?.EnableSettingsSync ?? false,
            ServerName = "Jellyfin",
            JellyseerrEnabled = config?.JellyseerrEnabled ?? false,
            JellyseerrUrl = (config?.JellyseerrEnabled == true)
                ? config.JellyseerrUrl
                : null,
            MdblistAvailable = !string.IsNullOrWhiteSpace(config?.MdblistApiKey),
            TmdbAvailable = !string.IsNullOrWhiteSpace(config?.TmdbApiKey),
            DefaultSettings = config?.DefaultUserSettings
        });
    }

    /// <summary>
    /// Gets the settings for the current authenticated user.
    /// </summary>
    /// <returns>The user's Moonfin settings.</returns>
    [HttpGet("Settings")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<MoonfinUserSettings>> GetMySettings()
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        
        if (config?.EnableSettingsSync != true)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { Error = "Settings sync is disabled" });
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { Error = "User not authenticated" });
        }

        var settings = await _settingsService.GetUserSettingsAsync(userId.Value);
        
        if (settings == null)
        {
            return NotFound(new { Error = "No settings found for user", UserId = userId });
        }

        return Ok(settings);
    }

    /// <summary>
    /// Gets the settings for a specific user (admin only).
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <returns>The user's Moonfin settings.</returns>
    [HttpGet("Settings/{userId}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<MoonfinUserSettings>> GetUserSettings([FromRoute] Guid userId)
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        
        if (config?.EnableSettingsSync != true)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { Error = "Settings sync is disabled" });
        }

        var settings = await _settingsService.GetUserSettingsAsync(userId);
        
        if (settings == null)
        {
            return NotFound(new { Error = "No settings found for user", UserId = userId });
        }

        return Ok(settings);
    }

    /// <summary>
    /// Saves settings for the current authenticated user.
    /// </summary>
    /// <param name="request">The settings save request.</param>
    /// <returns>Success status.</returns>
    [HttpPost("Settings")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<MoonfinSaveResponse>> SaveMySettings([FromBody] MoonfinSaveRequest request)
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        
        if (config?.EnableSettingsSync != true)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { Error = "Settings sync is disabled" });
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { Error = "User not authenticated" });
        }

        if (request.Settings == null)
        {
            return BadRequest(new { Error = "Settings are required" });
        }

        var existed = _settingsService.UserSettingsExist(userId.Value);
        
        await _settingsService.SaveUserSettingsAsync(
            userId.Value, 
            request.Settings, 
            request.ClientId,
            request.MergeMode ?? "merge"
        );

        return Ok(new MoonfinSaveResponse
        {
            Success = true,
            Created = !existed,
            UserId = userId.Value
        });
    }

    /// <summary>
    /// Saves settings for a specific user (admin only).
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="request">The settings save request.</param>
    /// <returns>Success status.</returns>
    [HttpPost("Settings/{userId}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<MoonfinSaveResponse>> SaveUserSettings(
        [FromRoute] Guid userId, 
        [FromBody] MoonfinSaveRequest request)
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        
        if (config?.EnableSettingsSync != true)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { Error = "Settings sync is disabled" });
        }

        if (request.Settings == null)
        {
            return BadRequest(new { Error = "Settings are required" });
        }

        var existed = _settingsService.UserSettingsExist(userId);
        
        await _settingsService.SaveUserSettingsAsync(
            userId, 
            request.Settings, 
            request.ClientId,
            request.MergeMode ?? "merge"
        );

        return Ok(new MoonfinSaveResponse
        {
            Success = true,
            Created = !existed,
            UserId = userId
        });
    }

    /// <summary>
    /// Deletes settings for the current authenticated user.
    /// </summary>
    /// <returns>Success status.</returns>
    [HttpDelete("Settings")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> DeleteMySettings()
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        
        if (config?.EnableSettingsSync != true)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { Error = "Settings sync is disabled" });
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { Error = "User not authenticated" });
        }

        await _settingsService.DeleteUserSettingsAsync(userId.Value);
        
        return Ok(new { Success = true, Message = "Settings deleted" });
    }

    /// <summary>
    /// Gets the resolved settings for the current user for a specific device profile.
    /// Resolution order: device overrides → global → admin defaults.
    /// </summary>
    /// <param name="profile">Device profile name: desktop, mobile, tv, or global.</param>
    /// <returns>Flat resolved settings profile.</returns>
    [HttpGet("Settings/Resolved/{profile}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<MoonfinSettingsProfile>> GetResolvedProfile([FromRoute] string profile)
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        
        if (config?.EnableSettingsSync != true)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { Error = "Settings sync is disabled" });
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { Error = "User not authenticated" });
        }

        if (!MoonfinUserSettings.ValidProfiles.Contains(profile.ToLowerInvariant()))
        {
            return BadRequest(new { Error = $"Invalid profile: {profile}. Valid profiles: {string.Join(", ", MoonfinUserSettings.ValidProfiles)}" });
        }

        var resolved = await _settingsService.GetResolvedProfileAsync(userId.Value, profile);
        if (resolved == null)
        {
            // No user settings at all — return admin defaults if available
            var adminDefaults = config?.DefaultUserSettings;
            return adminDefaults != null ? Ok(adminDefaults) : NotFound(new { Error = "No settings found" });
        }

        return Ok(resolved);
    }

    /// <summary>
    /// Saves settings for a specific device profile for the current user.
    /// </summary>
    /// <param name="profile">Device profile name: desktop, mobile, tv, or global.</param>
    /// <param name="request">The profile save request.</param>
    /// <returns>Success status.</returns>
    [HttpPost("Settings/Profile/{profile}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<MoonfinSaveResponse>> SaveMyProfile(
        [FromRoute] string profile, 
        [FromBody] MoonfinProfileSaveRequest request)
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        
        if (config?.EnableSettingsSync != true)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { Error = "Settings sync is disabled" });
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { Error = "User not authenticated" });
        }

        if (!MoonfinUserSettings.ValidProfiles.Contains(profile.ToLowerInvariant()))
        {
            return BadRequest(new { Error = $"Invalid profile: {profile}" });
        }

        if (request.Profile == null)
        {
            return BadRequest(new { Error = "Profile settings are required" });
        }

        var existed = _settingsService.UserSettingsExist(userId.Value);

        await _settingsService.SaveProfileAsync(userId.Value, profile, request.Profile, request.ClientId);

        return Ok(new MoonfinSaveResponse
        {
            Success = true,
            Created = !existed,
            UserId = userId.Value
        });
    }

    /// <summary>
    /// Deletes a device profile for the current user (resets to global).
    /// </summary>
    /// <param name="profile">Device profile name: desktop, mobile, or tv.</param>
    /// <returns>Success status.</returns>
    [HttpDelete("Settings/Profile/{profile}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> DeleteMyProfile([FromRoute] string profile)
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        
        if (config?.EnableSettingsSync != true)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { Error = "Settings sync is disabled" });
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { Error = "User not authenticated" });
        }

        var lower = profile.ToLowerInvariant();
        if (lower == "global")
        {
            return BadRequest(new { Error = "Cannot delete the global profile. Use DELETE /Settings to remove all settings." });
        }

        if (!MoonfinUserSettings.ValidProfiles.Contains(lower))
        {
            return BadRequest(new { Error = $"Invalid profile: {profile}" });
        }

        await _settingsService.DeleteProfileAsync(userId.Value, profile);
        return Ok(new { Success = true, Message = $"Profile '{profile}' deleted" });
    }

    /// <summary>
    /// Gets the admin-configured default user settings.
    /// </summary>
    /// <returns>Default settings profile or empty object.</returns>
    [HttpGet("Defaults")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<MoonfinSettingsProfile> GetDefaults()
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        return Ok(config?.DefaultUserSettings ?? new MoonfinSettingsProfile());
    }

    /// <summary>
    /// Checks if the current user has settings stored.
    /// </summary>
    /// <returns>Whether settings exist.</returns>
    [HttpHead("Settings")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult CheckMySettingsExist()
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        
        if (config?.EnableSettingsSync != true)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized();
        }

        if (_settingsService.UserSettingsExist(userId.Value))
        {
            return Ok();
        }

        return NotFound();
    }

    /// <summary>
    /// Gets the Jellyseerr configuration (admin URL + user enablement).
    /// </summary>
    [HttpGet("Jellyseerr/Config")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<JellyseerrConfigResponse>> GetJellyseerrConfig()
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        
        var userId = this.GetUserIdFromClaims();
        MoonfinUserSettings? userSettings = null;
        
        if (userId != null)
        {
            userSettings = await _settingsService.GetUserSettingsAsync(userId.Value);
        }

        // Auto-detect variant from the API
        var jellyseerrUrl = config?.GetEffectiveJellyseerrUrl();
        var variant = await DetectVariantAsync(jellyseerrUrl);
        
        // Use admin display name if set, otherwise auto-generate from variant
        var displayName = config?.JellyseerrDisplayName;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = variant == "seerr" ? "Seerr" : "Jellyseerr";
        }

        // Resolve Jellyseerr enabled from user's global profile
        var userJellyseerrEnabled = userSettings?.Global?.JellyseerrEnabled 
            ?? userSettings?.JellyseerrEnabled  // legacy v1
            ?? true;

        return Ok(new JellyseerrConfigResponse
        {
            Enabled = config?.JellyseerrEnabled ?? false,
            Url = config?.JellyseerrUrl,
            DirectUrl = config?.JellyseerrDirectUrl,
            DisplayName = displayName,
            Variant = variant,
            UserEnabled = userJellyseerrEnabled
        });
    }
    
    /// <summary>
    /// Auto-detect whether the configured URL is Jellyseerr or Seerr by calling the status API.
    /// Results are cached for 1 hour or until the URL changes.
    /// </summary>
    private async Task<string> DetectVariantAsync(string? jellyseerrUrl)
    {
        if (string.IsNullOrEmpty(jellyseerrUrl))
        {
            return "jellyseerr";
        }
        
        if (_cachedVariant != null && 
            _cachedVariantUrl == jellyseerrUrl && 
            DateTime.UtcNow < _variantCacheExpiry)
        {
            return _cachedVariant;
        }
        
        await _variantLock.WaitAsync();
        try
        {
            // Double-check cache after acquiring lock
            if (_cachedVariant != null && 
                _cachedVariantUrl == jellyseerrUrl && 
                DateTime.UtcNow < _variantCacheExpiry)
            {
                return _cachedVariant;
            }
            
            var variant = "jellyseerr";
            
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                
                var response = await client.GetAsync($"{jellyseerrUrl}/api/v1/status");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    
                    // Seerr uses version >= 3.0.0, Jellyseerr uses version < 3.0.0
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("version", out var versionEl))
                        {
                            var versionStr = versionEl.GetString();
                            if (!string.IsNullOrEmpty(versionStr))
                            {
                                var parts = versionStr.Split('.');
                                if (parts.Length >= 1 && int.TryParse(parts[0], out var major) && major >= 3)
                                {
                                    variant = "seerr";
                                }
                            }
                        }
                    }
                    catch
                    {
                        // JSON parse error - use default
                    }
                }
            }
            catch
            {
                // Network error - use default
            }
            
            _cachedVariant = variant;
            _cachedVariantUrl = jellyseerrUrl;
            _variantCacheExpiry = DateTime.UtcNow.AddHours(1);
            
            return variant;
        }
        finally
        {
            _variantLock.Release();
        }
    }
}

/// <summary>
/// Response for the ping endpoint.
/// </summary>
public class MoonfinPingResponse
{
    public bool Installed { get; set; }
    public string Version { get; set; } = string.Empty;
    public bool? SettingsSyncEnabled { get; set; }
    public string? ServerName { get; set; }
    public bool? JellyseerrEnabled { get; set; }
    public string? JellyseerrUrl { get; set; }
    public bool? MdblistAvailable { get; set; }
    public bool? TmdbAvailable { get; set; }
    public MoonfinSettingsProfile? DefaultSettings { get; set; }
}

/// <summary>
/// Response for Jellyseerr configuration.
/// </summary>
public class JellyseerrConfigResponse
{
    public bool Enabled { get; set; }
    public string? Url { get; set; }
    public string? DirectUrl { get; set; }
    public string DisplayName { get; set; } = "Jellyseerr";
    public string Variant { get; set; } = "jellyseerr";
    public bool UserEnabled { get; set; }
}

/// <summary>
/// Request for saving the full settings envelope.
/// </summary>
public class MoonfinSaveRequest
{
    public MoonfinUserSettings? Settings { get; set; }
    public string? ClientId { get; set; }
    public string? MergeMode { get; set; }
}

/// <summary>
/// Request for saving a single device profile.
/// </summary>
public class MoonfinProfileSaveRequest
{
    public MoonfinSettingsProfile? Profile { get; set; }
    public string? ClientId { get; set; }
}

/// <summary>
/// Response for saving settings.
/// </summary>
public class MoonfinSaveResponse
{
    public bool Success { get; set; }
    public bool Created { get; set; }
    public Guid UserId { get; set; }
}
