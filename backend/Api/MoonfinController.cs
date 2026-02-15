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
    /// <returns>Plugin status information.</returns>
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
            TmdbAvailable = !string.IsNullOrWhiteSpace(config?.TmdbApiKey)
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

        return Ok(new JellyseerrConfigResponse
        {
            Enabled = config?.JellyseerrEnabled ?? false,
            Url = config?.JellyseerrUrl,
            DirectUrl = config?.JellyseerrDirectUrl,
            DisplayName = displayName,
            Variant = variant,
            UserEnabled = userSettings?.JellyseerrEnabled ?? true
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
    /// <summary>Indicates the plugin is installed.</summary>
    public bool Installed { get; set; }

    /// <summary>Plugin version.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Whether settings sync is enabled by admin.</summary>
    public bool? SettingsSyncEnabled { get; set; }

    /// <summary>Jellyfin server name.</summary>
    public string? ServerName { get; set; }

    /// <summary>Whether Jellyseerr is enabled by admin.</summary>
    public bool? JellyseerrEnabled { get; set; }

    /// <summary>Admin-configured Jellyseerr URL.</summary>
    public string? JellyseerrUrl { get; set; }

    /// <summary>Whether admin has configured a server-wide MDBList API key.</summary>
    public bool? MdblistAvailable { get; set; }

    /// <summary>Whether admin has configured a server-wide TMDB API key.</summary>
    public bool? TmdbAvailable { get; set; }
}

/// <summary>
/// Response for Jellyseerr configuration.
/// </summary>
public class JellyseerrConfigResponse
{
    /// <summary>Whether Jellyseerr is enabled by admin.</summary>
    public bool Enabled { get; set; }

    /// <summary>Admin-configured Jellyseerr URL (used for API proxying).</summary>
    public string? Url { get; set; }

    /// <summary>
    /// Direct URL for loading Jellyseerr/Seerr in iframe (optional).
    /// When set, the iframe loads directly from this URL instead of through the proxy.
    /// </summary>
    public string? DirectUrl { get; set; }

    /// <summary>Display name shown in the UI (e.g., "Jellyseerr" or "Seerr").</summary>
    public string DisplayName { get; set; } = "Jellyseerr";

    /// <summary>UI variant for icon selection: "jellyseerr" or "seerr".</summary>
    public string Variant { get; set; } = "jellyseerr";

    /// <summary>Whether Jellyseerr is enabled in user settings.</summary>
    public bool UserEnabled { get; set; }
}

/// <summary>
/// Request for saving settings.
/// </summary>
public class MoonfinSaveRequest
{
    /// <summary>User settings to save.</summary>
    public MoonfinUserSettings? Settings { get; set; }

    /// <summary>Client identifier for tracking.</summary>
    public string? ClientId { get; set; }

    /// <summary>Merge strategy (replace, merge, client).</summary>
    public string? MergeMode { get; set; }
}

/// <summary>
/// Response for saving settings.
/// </summary>
public class MoonfinSaveResponse
{
    /// <summary>Whether the save was successful.</summary>
    public bool Success { get; set; }

    /// <summary>Whether new settings were created (vs updated).</summary>
    public bool Created { get; set; }

    /// <summary>User ID the settings were saved for.</summary>
    public Guid UserId { get; set; }
}
