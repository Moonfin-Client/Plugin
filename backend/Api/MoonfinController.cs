using System.Net.Mime;
using System.Reflection;
using System.Text.Json;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
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
    private const int MaxDetailsBackdropBlur = 40;

    private readonly MoonfinSettingsService _settingsService;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MoonfinController> _logger;
    
    private static readonly Type? _userManagerType = Type.GetType("MediaBrowser.Controller.Library.IUserManager, MediaBrowser.Controller");
    private static readonly MethodInfo? _userManagerGetUserById = _userManagerType?.GetMethod("GetUserById", [typeof(Guid)]);
    private static readonly PropertyInfo? _userManagerUsersProperty = _userManagerType?.GetProperty("Users");
    private static readonly MethodInfo? _internalItemsQuerySetUser = typeof(InternalItemsQuery).GetMethod("SetUser", BindingFlags.Public | BindingFlags.Instance);
    private static readonly PropertyInfo? _internalItemsQueryUserProperty = typeof(InternalItemsQuery).GetProperty(nameof(InternalItemsQuery.User), BindingFlags.Public | BindingFlags.Instance);
    private static readonly MethodInfo? _baseItemIsVisible = typeof(BaseItem)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .FirstOrDefault(m => m.Name == "IsVisible" && m.GetParameters().Length == 1);
    private static readonly Type? _baseItemIsVisibleUserType = _baseItemIsVisible?.GetParameters()[0].ParameterType;

    public MoonfinController(
        MoonfinSettingsService settingsService,
        ILibraryManager libraryManager,
        ILogger<MoonfinController> logger)
    {
        _settingsService = settingsService;
        _libraryManager = libraryManager;
        _logger = logger;
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
            SeerrEnabled = config?.SeerrEnabled ?? false,
            SeerrUrl = (config?.SeerrEnabled == true)
                ? config.SeerrUrl
                : null,
            MdblistAvailable = !string.IsNullOrWhiteSpace(config?.MdblistApiKey),
            TmdbAvailable = !string.IsNullOrWhiteSpace(config?.TmdbApiKey),
            DefaultSettings = config?.DefaultUserSettings
        });
    }

    /// <summary>
    /// Checks server-side write permissions for all libraries that have SaveLocalMetadata enabled.
    /// </summary>
    [HttpGet("Libraries/CheckWriteAccess")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<List<LibraryWriteAccessReport>> CheckLibrariesWriteAccess()
    {
        var report = new List<LibraryWriteAccessReport>();
        var virtualFolders = _libraryManager.GetVirtualFolders();

        foreach (var folder in virtualFolders)
        {
            var options = folder.LibraryOptions;
            if (options == null || !options.SaveLocalMetadata)
            {
                continue;
            }

            var failedPaths = new List<string>();
            foreach (var location in folder.Locations)
            {
                if (string.IsNullOrWhiteSpace(location)) continue;

                bool hasWriteAccess = false;
                string testFile = Path.Combine(location, $"moonfin_write_test_{Guid.NewGuid():N}.tmp");
                try
                {
                    // Attempt to create, write, and delete a temporary file
                    System.IO.File.WriteAllText(testFile, "test");
                    System.IO.File.Delete(testFile);
                    hasWriteAccess = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Jellyfin lacks write access to library path: {Path}", location);
                }

                if (!hasWriteAccess)
                {
                    failedPaths.Add(location);
                }
            }

            if (failedPaths.Count > 0)
            {
                report.Add(new LibraryWriteAccessReport
                {
                    LibraryId = folder.ItemId,
                    LibraryName = folder.Name,
                    FailedPaths = failedPaths
                });
            }
        }

        return Ok(report);
    }

    [HttpGet("Settings/Stream")]
    [Authorize]
    [ApiExplorerSettings(IgnoreApi = true)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> StreamSettings()
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

        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var channel = _settingsService.RegisterSseChannel(userId.Value);

        try
        {
            await Response.WriteAsync(":connected\n\n", HttpContext.RequestAborted);
            await Response.Body.FlushAsync(HttpContext.RequestAborted);

            while (!HttpContext.RequestAborted.IsCancellationRequested)
            {
                using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
                heartbeatCts.CancelAfter(TimeSpan.FromSeconds(30));

                bool hasData;
                try
                {
                    hasData = await channel.Reader.WaitToReadAsync(heartbeatCts.Token);
                }
                catch (OperationCanceledException)
                {
                    if (HttpContext.RequestAborted.IsCancellationRequested)
                    {
                        break;
                    }

                    await Response.WriteAsync(":heartbeat\n\n", HttpContext.RequestAborted);
                    await Response.Body.FlushAsync(HttpContext.RequestAborted);
                    continue;
                }

                if (!hasData)
                {
                    break;
                }

                while (channel.Reader.TryRead(out var eventPayload))
                {
                    var payload = eventPayload;
                    if (string.IsNullOrWhiteSpace(payload))
                    {
                        continue;
                    }

                    if (!payload.TrimStart().StartsWith("{", StringComparison.Ordinal))
                    {
                        payload = JsonSerializer.Serialize(new { type = payload });
                    }

                    await Response.WriteAsync($"data: {payload}\n\n", HttpContext.RequestAborted);
                    await Response.Body.FlushAsync(HttpContext.RequestAborted);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _settingsService.UnregisterSseChannel(userId.Value, channel);
        }

        return new EmptyResult();
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
    /// Applies the configured admin defaults to all users (admin only).
    /// When <paramref name="overwrite"/> is false, defaults are merged into each existing
    /// user's global profile, keeping their other values and device overrides. When true,
    /// every server user is reset to a clean global-only profile equal to the defaults
    /// (including users with no file), and orphaned files for deleted users are removed.
    /// </summary>
    [HttpPost("Admin/PushDefaults")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> ApplyDefaultsToAllUsers([FromQuery] bool overwrite = false)
    {
        var config = MoonfinPlugin.Instance?.Configuration;

        if (config?.EnableSettingsSync != true)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Settings sync is disabled" });
        }

        var defaults = config.DefaultUserSettings;
        if (defaults == null)
        {
            return BadRequest(new { error = "No default user settings configured" });
        }

        int usersAffected;
        var orphansDeleted = 0;

        if (overwrite)
        {
            var serverUserIds = GetAllServerUserIds();
            if (serverUserIds == null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "User manager unavailable" });
            }

            (usersAffected, orphansDeleted) = await _settingsService.ResetAllUsersToDefaultsAsync(
                defaults,
                serverUserIds,
                deleteOrphans: true);
        }
        else
        {
            usersAffected = await _settingsService.MergeDefaultsToAllUsersAsync(defaults);
        }

        var liveRefreshDeliveries = _settingsService.BroadcastSystemEvent("settingsUpdated");

        return Ok(new { success = true, overwrite, usersAffected, orphansDeleted, liveRefreshDeliveries });
    }

    /// <summary>
    /// Applies the configured admin defaults to a single user (admin only).
    /// When <paramref name="overwrite"/> is false, defaults are merged into the user's
    /// global profile, keeping their other values and device overrides. When true, the
    /// user is reset to a clean global-only profile equal to the defaults.
    /// </summary>
    [HttpPost("Admin/PushDefaults/{userId}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> ApplyDefaultsToUser([FromRoute] Guid userId, [FromQuery] bool overwrite = false)
    {
        var config = MoonfinPlugin.Instance?.Configuration;

        if (config?.EnableSettingsSync != true)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Settings sync is disabled" });
        }

        var defaults = config.DefaultUserSettings;
        if (defaults == null)
        {
            return BadRequest(new { error = "No default user settings configured" });
        }

        if (userId == Guid.Empty)
        {
            return BadRequest(new { error = "A valid userId is required" });
        }

        if (overwrite)
        {
            await _settingsService.ResetUserToDefaultsAsync(userId, defaults);
        }
        else
        {
            await _settingsService.MergeDefaultsToUserAsync(userId, defaults);
        }

        return Ok(new { success = true, overwrite });
    }

    [HttpPost("Broadcast")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult BroadcastMessage([FromBody] MoonfinBroadcastRequest request)
    {
        var config = MoonfinPlugin.Instance?.Configuration;

        if (config?.EnableSettingsSync != true)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { Error = "Settings sync is disabled" });
        }

        var message = request.Message?.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            return BadRequest(new { Error = "message is required" });
        }

        var deliveries = _settingsService.BroadcastMessage(message);
        return Ok(new { Success = true, Deliveries = deliveries });
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
            // No user settings at all; return admin defaults if available.
            var adminDefaults = config?.DefaultUserSettings;
            return adminDefaults != null ? Ok(adminDefaults) : NotFound(new { Error = "No settings found" });
        }

        return Ok(resolved);
    }

    /// <summary>
    /// Gets resolved details screen blur for the current user.
    /// </summary>
    [HttpGet("Settings/detailsScreenBlur")]
    [HttpGet("Settings/detailsScreenBlur/{profile}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<MoonfinDetailsScreenBlurResponse>> GetDetailsScreenBlur([FromRoute] string? profile = null)
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

        var resolvedProfileName = string.IsNullOrWhiteSpace(profile) ? "global" : profile.ToLowerInvariant();
        if (!MoonfinUserSettings.ValidProfiles.Contains(resolvedProfileName))
        {
            return BadRequest(new { Error = $"Invalid profile: {resolvedProfileName}. Valid profiles: {string.Join(", ", MoonfinUserSettings.ValidProfiles)}" });
        }

        var resolved = await _settingsService.GetResolvedProfileAsync(userId.Value, resolvedProfileName)
            ?? config?.DefaultUserSettings
            ?? new MoonfinSettingsProfile();

        var blur = Math.Clamp(ResolveBackdropBlur(resolved), 0, MaxDetailsBackdropBlur);

        return Ok(new MoonfinDetailsScreenBlurResponse
        {
            Profile = resolvedProfileName,
            DetailsScreenBlur = blur.ToString()
        });
    }

    /// <summary>
    /// Saves details screen blur for a specific profile for the current user.
    /// </summary>
    [HttpPost("Settings/detailsScreenBlur")]
    [HttpPost("Settings/detailsScreenBlur/{profile}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<MoonfinDetailsScreenBlurSaveResponse>> SaveDetailsScreenBlur(
        [FromBody] MoonfinDetailsScreenBlurRequest request,
        [FromRoute] string? profile = null)
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

        if (string.IsNullOrWhiteSpace(request.DetailsScreenBlur))
        {
            return BadRequest(new { Error = "detailsScreenBlur is required" });
        }

        var targetProfile = string.IsNullOrWhiteSpace(profile)
            ? (string.IsNullOrWhiteSpace(request.Profile) ? "global" : request.Profile.ToLowerInvariant())
            : profile.ToLowerInvariant();

        if (!MoonfinUserSettings.ValidProfiles.Contains(targetProfile))
        {
            return BadRequest(new { Error = $"Invalid profile: {targetProfile}. Valid profiles: {string.Join(", ", MoonfinUserSettings.ValidProfiles)}" });
        }

        if (!int.TryParse(request.DetailsScreenBlur, out var parsedBlur))
        {
            return BadRequest(new { Error = "detailsScreenBlur must be a numeric string" });
        }

        var normalizedBlur = Math.Clamp(parsedBlur, 0, MaxDetailsBackdropBlur);
        var profilePatch = new MoonfinSettingsProfile
        {
            DetailsBackdropBlur = normalizedBlur,
            DetailsScreenBlur = normalizedBlur.ToString()
        };

        var existed = _settingsService.UserSettingsExist(userId.Value);
        await _settingsService.SaveProfileAsync(userId.Value, targetProfile, profilePatch, request.ClientId ?? "moonfin-detailsScreenBlur-endpoint");

        return Ok(new MoonfinDetailsScreenBlurSaveResponse
        {
            Success = true,
            Created = !existed,
            UserId = userId.Value,
            Profile = targetProfile,
            DetailsScreenBlur = normalizedBlur.ToString()
        });
    }

    /// <summary>
    /// Gets resolved details screen opacity for the current user.
    /// </summary>
    [HttpGet("Settings/detailsScreenOpacity")]
    [HttpGet("Settings/detailsScreenOpacity/{profile}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<MoonfinDetailsScreenOpacityResponse>> GetDetailsScreenOpacity([FromRoute] string? profile = null)
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

        var resolvedProfileName = string.IsNullOrWhiteSpace(profile) ? "global" : profile.ToLowerInvariant();
        if (!MoonfinUserSettings.ValidProfiles.Contains(resolvedProfileName))
        {
            return BadRequest(new { Error = $"Invalid profile: {resolvedProfileName}. Valid profiles: {string.Join(", ", MoonfinUserSettings.ValidProfiles)}" });
        }

        var resolved = await _settingsService.GetResolvedProfileAsync(userId.Value, resolvedProfileName)
            ?? config?.DefaultUserSettings
            ?? new MoonfinSettingsProfile();

        var opacity = Math.Clamp(resolved.DetailsBackdropOpacity ?? 90, 0, 100);

        return Ok(new MoonfinDetailsScreenOpacityResponse
        {
            Profile = resolvedProfileName,
            DetailsScreenOpacity = opacity
        });
    }

    /// <summary>
    /// Saves details screen opacity for a specific profile for the current user.
    /// </summary>
    [HttpPost("Settings/detailsScreenOpacity")]
    [HttpPost("Settings/detailsScreenOpacity/{profile}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<MoonfinDetailsScreenOpacitySaveResponse>> SaveDetailsScreenOpacity(
        [FromBody] MoonfinDetailsScreenOpacityRequest request,
        [FromRoute] string? profile = null)
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

        if (!request.DetailsScreenOpacity.HasValue)
        {
            return BadRequest(new { Error = "detailsScreenOpacity is required" });
        }

        var targetProfile = string.IsNullOrWhiteSpace(profile)
            ? (string.IsNullOrWhiteSpace(request.Profile) ? "global" : request.Profile.ToLowerInvariant())
            : profile.ToLowerInvariant();

        if (!MoonfinUserSettings.ValidProfiles.Contains(targetProfile))
        {
            return BadRequest(new { Error = $"Invalid profile: {targetProfile}. Valid profiles: {string.Join(", ", MoonfinUserSettings.ValidProfiles)}" });
        }

        var normalizedOpacity = Math.Clamp(request.DetailsScreenOpacity.Value, 0, 100);
        var profilePatch = new MoonfinSettingsProfile
        {
            DetailsBackdropOpacity = normalizedOpacity
        };

        var existed = _settingsService.UserSettingsExist(userId.Value);
        await _settingsService.SaveProfileAsync(userId.Value, targetProfile, profilePatch, request.ClientId ?? "moonfin-detailsScreenOpacity-endpoint");

        return Ok(new MoonfinDetailsScreenOpacitySaveResponse
        {
            Success = true,
            Created = !existed,
            UserId = userId.Value,
            Profile = targetProfile,
            DetailsScreenOpacity = normalizedOpacity
        });
    }

    private static int ResolveBackdropBlur(MoonfinSettingsProfile profile)
    {
        if (profile.DetailsBackdropBlur.HasValue)
        {
            return profile.DetailsBackdropBlur.Value;
        }

        if (!string.IsNullOrWhiteSpace(profile.DetailsScreenBlur)
            && int.TryParse(profile.DetailsScreenBlur, out var legacyBlur))
        {
            return legacyBlur;
        }

        return 0;
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
    /// Gets all genres available in the user's libraries (Movie + Series).
    /// Used to populate the genre exclusion picker in settings.
    /// </summary>
    /// <returns>List of genre names.</returns>
    [HttpGet("Genres")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetGenres()
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { Error = "User not authenticated" });
        }

        var queryUser = ResolveQueryUser(userId.Value);
        if (queryUser == null)
        {
            return Ok(new { Items = Array.Empty<object>() });
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Genre],
            Recursive = true
        };

        if (!TryApplyQueryUser(query, queryUser))
        {
            return Ok(new { Items = Array.Empty<object>() });
        }

        var genres = _libraryManager.GetItemsResult(query).Items
            .Where(g => !string.IsNullOrWhiteSpace(g.Name) && IsItemVisibleToUser(g, queryUser))
            .Select(g => new { Id = g.Id.ToString("N"), Name = g.Name })
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new { Items = genres });
    }

    /// <summary>
    /// Gets resolved media bar content for the current user.
    /// Combines user settings resolution with server-side item queries so all clients
    /// (web, Android, TV) get identical results from a single call.
    /// </summary>
    /// <param name="profile">Device profile name: desktop, mobile, tv, or global.</param>
    /// <returns>Media bar items as Jellyfin BaseItemDto objects.</returns>
    [HttpGet("MediaBar")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> GetMediaBarItems(
        [FromQuery] string profile = "global")
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { Error = "User not authenticated" });
        }

        // Resolve settings: device profile → global → admin defaults
        var resolved = await _settingsService.GetResolvedProfileAsync(userId.Value, profile);
        var isFallback = resolved == null;
        var settings = resolved ?? MoonfinPlugin.Instance?.Configuration?.DefaultUserSettings ?? new MoonfinSettingsProfile();

        var sourceType = isFallback ? "library" : (settings.MediaBarSourceType ?? "library");
        var limit = isFallback ? 5 : (settings.MediaBarItemCount ?? 10);
        var excludedGenres = settings.MediaBarExcludedGenres;
        var queryUser = ResolveQueryUser(userId.Value);

        if (queryUser == null)
        {
            return Ok(new
            {
                Items = Array.Empty<object>(),
                TotalRecordCount = 0
            });
        }

        List<BaseItem> items;

        if (sourceType == "collection" && settings.MediaBarCollectionIds is { Count: > 0 })
        {
            items = GetCollectionItems(settings.MediaBarCollectionIds, limit, queryUser, excludedGenres);
        }
        else
        {
            items = GetLibraryItems(isFallback ? null : settings.MediaBarLibraryIds, limit, queryUser, excludedGenres);
        }

        var dtos = items
            .Where(HasBackdropImage)
            .Select(MapItemToDto)
            .ToList();

        return Ok(new
        {
            Items = dtos,
            TotalRecordCount = dtos.Count
        });
    }

    /// <summary>
    /// Maps a BaseItem to a lightweight DTO matching Jellyfin's BaseItemDto shape.
    /// Uses only stable BaseItem properties to avoid version-specific API issues.
    /// </summary>
    private static object MapItemToDto(BaseItem item)
    {
        // Build image tags dict
        var imageTags = new Dictionary<string, string>();
        var imageInfo = item.GetImageInfo(ImageType.Primary, 0);
        if (imageInfo != null)
        {
            imageTags["Primary"] = GetTag(imageInfo);
        }
        var logoInfo = item.GetImageInfo(ImageType.Logo, 0);
        if (logoInfo != null)
        {
            imageTags["Logo"] = GetTag(logoInfo);
        }

        // Build backdrop tags array
        var backdropTags = new List<string>();
        var backdropImages = item.GetImages(ImageType.Backdrop).ToList();
        foreach (var bd in backdropImages)
        {
            backdropTags.Add(GetTag(bd));
        }

        return new
        {
            item.Id,
            item.Name,
            Type = item.GetBaseItemKind().ToString(),
            item.ProductionYear,
            item.OfficialRating,
            item.RunTimeTicks,
            item.Genres,
            item.Overview,
            item.CommunityRating,
            item.CriticRating,
            ImageTags = imageTags,
            BackdropImageTags = backdropTags
        };
    }

    private static bool HasBackdropImage(BaseItem item)
    {
        return item.GetImageInfo(ImageType.Backdrop, 0) != null;
    }

    private object? ResolveQueryUser(Guid userId)
    {
        if (_userManagerType == null || _userManagerGetUserById == null)
        {
            return null;
        }

        var userManager = HttpContext?.RequestServices.GetService(_userManagerType);
        if (userManager == null)
        {
            return null;
        }

        return _userManagerGetUserById.Invoke(userManager, [userId]);
    }

    private List<Guid>? GetAllServerUserIds()
    {
        if (_userManagerType == null || _userManagerUsersProperty == null)
        {
            return null;
        }

        var userManager = HttpContext?.RequestServices.GetService(_userManagerType);
        if (userManager == null)
        {
            return null;
        }

        if (_userManagerUsersProperty.GetValue(userManager) is not IEnumerable<object> users)
        {
            return null;
        }

        var ids = new List<Guid>();
        foreach (var user in users)
        {
            if (user?.GetType().GetProperty("Id")?.GetValue(user) is Guid id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static bool TryApplyQueryUser(InternalItemsQuery query, object queryUser)
    {
        if (_internalItemsQuerySetUser != null)
        {
            try
            {
                _internalItemsQuerySetUser.Invoke(query, [queryUser]);
                return true;
            }
            catch
            {
            }
        }

        return TrySetQueryUserProperty(query, queryUser);
    }

    private static bool TrySetQueryUserProperty(InternalItemsQuery query, object queryUser)
    {
        if (_internalItemsQueryUserProperty?.CanWrite != true || !_internalItemsQueryUserProperty.PropertyType.IsInstanceOfType(queryUser))
        {
            return false;
        }

        try
        {
            _internalItemsQueryUserProperty.SetValue(query, queryUser);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private BaseItem? GetItemByIdForUser(Guid itemId, object queryUser)
    {
        var query = new InternalItemsQuery
        {
            ItemIds = [itemId],
            Limit = 1
        };

        if (!TryApplyQueryUser(query, queryUser))
        {
            return null;
        }

        var item = _libraryManager.GetItemsResult(query).Items.FirstOrDefault(i => i.Id == itemId);
        return item != null && IsItemVisibleToUser(item, queryUser) ? item : null;
    }

    /// <summary>
    /// Invokes BaseItem.IsVisible(User) via reflection so we stay compatible with the
    /// User type change between Jellyfin 10.10 and 10.11. Fails closed: if the call
    /// throws or cannot be invoked, the item is treated as not visible.
    /// </summary>
    private static bool IsItemVisibleToUser(BaseItem item, object queryUser)
    {
        if (_baseItemIsVisible == null || _baseItemIsVisibleUserType == null) return true;
        if (!_baseItemIsVisibleUserType.IsInstanceOfType(queryUser)) return true;

        try
        {
            return _baseItemIsVisible.Invoke(item, [queryUser]) is true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets a stable tag string from an ItemImageInfo for cache-busting image URLs.
    /// </summary>
    private static string GetTag(ItemImageInfo info)
    {
        return info.DateModified.Ticks.ToString("X");
    }

    /// <summary>
    /// Computes the included genre IDs by subtracting excluded genre IDs from all available genre IDs.
    /// Returns null if no exclusions are needed (i.e. no genre filter should be applied).
    /// Uses GUIDs instead of names for locale-independent filtering.
    /// </summary>
    private Guid[]? GetIncludedGenreIds(List<string>? excludedGenreIds, object queryUser)
    {
        if (excludedGenreIds is not { Count: > 0 }) return null;

        var genreQuery = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Genre],
            Recursive = true
        };

        if (!TryApplyQueryUser(genreQuery, queryUser)) return Array.Empty<Guid>();

        var allGenres = _libraryManager.GetItemsResult(genreQuery).Items
            .Where(g => IsItemVisibleToUser(g, queryUser))
            .ToList();

        var excluded = new HashSet<Guid>();
        foreach (var idStr in excludedGenreIds)
        {
            if (Guid.TryParse(idStr, out var guid)) excluded.Add(guid);
        }

        var included = allGenres
            .Where(g => !excluded.Contains(g.Id))
            .Select(g => g.Id)
            .ToArray();

        return included.Length < allGenres.Count ? included : null;
    }

    /// <summary>
    /// Resolves excluded genre IDs to their current localized names for in-memory filtering.
    /// Returns null if no exclusions are needed.
    /// </summary>
    private HashSet<string>? ResolveExcludedGenreNames(List<string>? excludedGenreIds)
    {
        if (excludedGenreIds is not { Count: > 0 }) return null;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var idStr in excludedGenreIds)
        {
            if (!Guid.TryParse(idStr, out var guid)) continue;
            var genre = _libraryManager.GetItemById(guid);
            if (genre != null && !string.IsNullOrWhiteSpace(genre.Name))
                names.Add(genre.Name);
        }

        return names.Count > 0 ? names : null;
    }

    /// <summary>
    /// Queries random Movie/Series items, optionally filtered to specific libraries
    /// and with genre exclusions applied at the database level.
    /// </summary>
    private List<BaseItem> GetLibraryItems(List<string>? libraryIds, int limit, object queryUser, List<string>? excludedGenreIds = null)
    {
        var includedGenreIds = GetIncludedGenreIds(excludedGenreIds, queryUser);

        if (includedGenreIds is { Length: 0 }) return [];

        if (libraryIds is not { Count: > 0 })
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                Limit = limit,
                Recursive = true
            };

            if (includedGenreIds != null) query.GenreIds = includedGenreIds;

            if (!TryApplyQueryUser(query, queryUser)) return [];

            SetRandomOrder(query);
            return _libraryManager.GetItemsResult(query).Items
                .Where(item => IsItemVisibleToUser(item, queryUser))
                .ToList();
        }

        // Only allow libraries the user can see; silently drop any the caller
        // doesn't have access to so settings carried over from a previous state
        // (or forged by a client) cannot leak restricted content.
        var allowedLibraryIds = new List<Guid>();
        foreach (var libId in libraryIds)
        {
            if (!Guid.TryParse(libId, out var parentGuid)) continue;
            var parent = _libraryManager.GetItemById(parentGuid);
            if (parent == null || !IsItemVisibleToUser(parent, queryUser)) continue;
            allowedLibraryIds.Add(parentGuid);
        }

        if (allowedLibraryIds.Count == 0) return [];

        var allItems = new List<BaseItem>();
        var seenIds = new HashSet<Guid>();
        var perLibraryLimit = Math.Max(1, limit / allowedLibraryIds.Count + 1);

        foreach (var parentGuid in allowedLibraryIds)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                ParentId = parentGuid,
                Limit = perLibraryLimit,
                Recursive = true
            };

            if (includedGenreIds != null) query.GenreIds = includedGenreIds;

            if (!TryApplyQueryUser(query, queryUser)) return [];

            SetRandomOrder(query);

            foreach (var item in _libraryManager.GetItemsResult(query).Items)
            {
                if (!seenIds.Add(item.Id)) continue;
                if (!IsItemVisibleToUser(item, queryUser)) continue;
                allItems.Add(item);
            }
        }

        for (var i = allItems.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (allItems[i], allItems[j]) = (allItems[j], allItems[i]);
        }

        return allItems.Take(limit).ToList();
    }

    /// <summary>
    /// Sets OrderBy to Random on the query using reflection, avoiding direct
    /// reference to SortOrder which moved between Jellyfin 10.10 and 10.11.
    /// </summary>
    private void SetRandomOrder(InternalItemsQuery query)
    {
        try
        {
            var orderByProp = typeof(InternalItemsQuery).GetProperty(nameof(InternalItemsQuery.OrderBy));
            if (orderByProp == null) return;

            var elementType = orderByProp.PropertyType.GetGenericArguments()[0];
            var sortOrderType = elementType.GetGenericArguments()[1];
            var ascending = Enum.ToObject(sortOrderType, 0);

            var tuple = Activator.CreateInstance(elementType, ItemSortBy.Random, ascending);
            var array = Array.CreateInstance(elementType, 1);
            array.SetValue(tuple, 0);

            orderByProp.SetValue(query, array);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set random sort order via reflection; items will use default order");
        }
    }

    /// <summary>
    /// Queries items from specified collections/playlists, filtered to Movie/Series.
    /// Collection items are iterated individually, so genre exclusion is applied in-memory.
    /// </summary>
    private List<BaseItem> GetCollectionItems(List<string> collectionIds, int limit, object queryUser, List<string>? excludedGenreIds = null)
    {
        var allItems = new List<BaseItem>();
        var seenIds = new HashSet<Guid>();
        var excludedNames = ResolveExcludedGenreNames(excludedGenreIds);

        foreach (var colId in collectionIds)
        {
            if (!Guid.TryParse(colId, out var parentGuid)) continue;

            var parent = GetItemByIdForUser(parentGuid, queryUser);
            if (parent is not Folder folder) continue;

            var linkedIds = folder.LinkedChildren
                .Where(linkedChild => linkedChild.ItemId.HasValue)
                .Select(linkedChild => linkedChild.ItemId!.Value)
                .Distinct()
                .ToArray();

            if (linkedIds.Length == 0) continue;

            var query = new InternalItemsQuery
            {
                ItemIds = linkedIds,
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                Limit = linkedIds.Length
            };

            if (!TryApplyQueryUser(query, queryUser)) return [];

            foreach (var item in _libraryManager.GetItemsResult(query).Items)
            {
                if (!seenIds.Add(item.Id)) continue;
                if (!IsItemVisibleToUser(item, queryUser)) continue;
                if (excludedNames != null && item.Genres.Any(g => excludedNames.Contains(g))) continue;

                allItems.Add(item);
            }
        }

        return allItems.Take(limit).ToList();
    }

    /// <summary>
    /// Gets the Seerr configuration (admin URL + user enablement).
    /// </summary>
    [HttpGet("Seerr/Config")]
    // Legacy alias kept active so older clients keep working while they migrate to /Moonfin/Seerr.
    [HttpGet("Jellyseerr/Config")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<SeerrConfigResponse>> GetSeerrConfig()
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        
        var userId = this.GetUserIdFromClaims();
        MoonfinUserSettings? userSettings = null;
        
        if (userId != null)
        {
            userSettings = await _settingsService.GetUserSettingsAsync(userId.Value);
        }

        var displayName = config?.SeerrDisplayName;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = "Seerr";
        }

        var userSeerrEnabled = userSettings?.Global?.SeerrEnabled
            ?? userSettings?.SeerrEnabled  // legacy v1
            ?? true;

        return Ok(new SeerrConfigResponse
        {
            Enabled = config?.SeerrEnabled ?? false,
            Url = config?.SeerrUrl,
            DisplayName = displayName,
            Variant = "seerr",
            UserEnabled = userSeerrEnabled
        });
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
    public bool? SeerrEnabled { get; set; }
    public string? SeerrUrl { get; set; }
    public bool? MdblistAvailable { get; set; }
    public bool? TmdbAvailable { get; set; }
    public MoonfinSettingsProfile? DefaultSettings { get; set; }

    // Legacy field names so clients from before the Seerr rename keep reading these.
    public bool? JellyseerrEnabled => SeerrEnabled;
    public string? JellyseerrUrl => SeerrUrl;
}

/// <summary>
/// Response for Seerr configuration.
/// </summary>
public class SeerrConfigResponse
{
    public bool Enabled { get; set; }
    public string? Url { get; set; }
    public string DisplayName { get; set; } = "Seerr";
    public string Variant { get; set; } = "seerr";
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

public class MoonfinDetailsScreenBlurRequest
{
    public string? Profile { get; set; }
    public string? DetailsScreenBlur { get; set; }
    public string? ClientId { get; set; }
}

public class MoonfinDetailsScreenBlurResponse
{
    public string Profile { get; set; } = "global";
    public string DetailsScreenBlur { get; set; } = "0";
}

public class MoonfinDetailsScreenBlurSaveResponse : MoonfinDetailsScreenBlurResponse
{
    public bool Success { get; set; }
    public bool Created { get; set; }
    public Guid UserId { get; set; }
}

public class MoonfinDetailsScreenOpacityRequest
{
    public string? Profile { get; set; }
    public int? DetailsScreenOpacity { get; set; }
    public string? ClientId { get; set; }
}

public class MoonfinDetailsScreenOpacityResponse
{
    public string Profile { get; set; } = "global";
    public int DetailsScreenOpacity { get; set; }
}

public class MoonfinDetailsScreenOpacitySaveResponse : MoonfinDetailsScreenOpacityResponse
{
    public bool Success { get; set; }
    public bool Created { get; set; }
    public Guid UserId { get; set; }
}

public class MoonfinBroadcastRequest
{
    public string? Message { get; set; }
}

/// <summary>
/// Report on write access for a media library.
/// </summary>
public class LibraryWriteAccessReport
{
    /// <summary>
    /// Gets or sets the library's unique identifier.
    /// </summary>
    public string LibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library's name.
    /// </summary>
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a list of locations where write access failed.
    /// </summary>
    public List<string> FailedPaths { get; set; } = new();
}
