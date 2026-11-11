using System.Net.Mime;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Controller.Dto;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moonfin.Calendar;
using Moonfin.Server.Helpers;
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
    private readonly NotificationStore _notificationStore;
    private readonly PushDeliveryService _pushDelivery;
    private readonly ConfigBackupService _configBackup;
    private readonly MoonfinSimilarItemsService _similarItemsService;
    private readonly ItemCardDtoMapper _cardMapper;
    private readonly DefaultHiddenLibraries _hiddenLibraries;
    private readonly ILogger<MoonfinController> _logger;
    
    private static readonly Type? _userManagerType = Type.GetType("MediaBrowser.Controller.Library.IUserManager, MediaBrowser.Controller");
    private static readonly MethodInfo? _userManagerGetUsersIds = _userManagerType?.GetMethod("GetUsersIds", Type.EmptyTypes);
    private static readonly PropertyInfo? _userManagerUsersIdsProperty = _userManagerType?.GetProperty("UsersIds");
    private static readonly MethodInfo? _userManagerGetUsers = _userManagerType?.GetMethod("GetUsers", Type.EmptyTypes);
    private static readonly PropertyInfo? _userManagerUsersProperty = _userManagerType?.GetProperty("Users");

    public MoonfinController(
        MoonfinSettingsService settingsService,
        ILibraryManager libraryManager,
        NotificationStore notificationStore,
        PushDeliveryService pushDelivery,
        ConfigBackupService configBackup,
        MoonfinSimilarItemsService similarItemsService,
        ItemCardDtoMapper cardMapper,
        DefaultHiddenLibraries hiddenLibraries,
        ILogger<MoonfinController> logger)
    {
        _settingsService = settingsService;
        _libraryManager = libraryManager;
        _notificationStore = notificationStore;
        _pushDelivery = pushDelivery;
        _configBackup = configBackup;
        _similarItemsService = similarItemsService;
        _cardMapper = cardMapper;
        _hiddenLibraries = hiddenLibraries;
        _logger = logger;
    }

    /// <summary>
    /// Tells the config page whether to offer a restore.
    /// </summary>
    [HttpGet("Admin/ConfigBackup")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetConfigBackupStatus()
    {
        return Ok(new
        {
            looksReset = _configBackup.ConfigurationLooksReset,
            backupPath = _configBackup.BackupPath,
        });
    }

    /// <summary>
    /// Copies the backup over the live plugin configuration. Deliberately manual, so an admin who
    /// reset the settings on purpose doesn't have that undone behind their back. Needs a restart.
    /// </summary>
    [HttpPost("Admin/RestoreConfig")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult RestoreConfigFromBackup()
    {
        try
        {
            if (!_configBackup.Restore())
            {
                return NotFound(new { error = "No configuration backup was found." });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore the Moonfin plugin configuration from backup");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }

        return Ok(new
        {
            restored = true,
            restartRequired = true,
            message = "Configuration restored. Restart the server for it to take effect.",
        });
    }

    /// <summary>
    /// Ping endpoint to check if Moonfin plugin is installed.
    /// </summary>
    /// <returns>Plugin status information including admin defaults.</returns>
    [HttpGet("Ping")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<MoonfinPingResponse> Ping([FromQuery] string? profile = null)
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
            TmdbThumbsSupported = true,
            TmdbPersonLinksSupported = true,
            TmdbReviewsSupported = true,
            TmdbDetailsSupported = true,
            MessagesSupported = true,
            PinSupported = true,
            IncompleteRecordingsSupported = config?.LiveTvDetectIncompleteRecordings ?? true,
            ExtendedCompleteShowingsSupported = config?.LiveTvDetectIncompleteRecordings ?? true,
            RecommendationsSupported = config?.RecommendationsProviderEnabled ?? true,
            SmartSearchEnabled = config?.SmartSearchEnabled ?? false,
            CalendarAvailable = config?.SeerrEnabled == true
                && !string.IsNullOrEmpty(config.GetEffectiveSeerrUrl())
                && ArrCalendarWindow.Refusal(config.CalendarAdminsOnly, this.IsAdminFromClaims()) == null,
            CalendarDaysBack = Math.Max(0, config?.CalendarDaysBack ?? 14),
            CalendarDaysAhead = Math.Max(0, config?.CalendarDaysAhead ?? 90),
            CalendarReminders = true,
            ArrQueueAvailable = config?.SeerrEnabled == true && !string.IsNullOrEmpty(config.GetEffectiveSeerrUrl()),
            SocketRelay = config?.EnableSocketRelay ?? false,
            DefaultSettings = _settingsService.AdminDefaultsFor(profile)
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
    /// Applies every profile's admin defaults to all users (admin only). Kept for callers of the
    /// old route, the admin page uses <see cref="ApplyDefaults"/>.
    /// </summary>
    [HttpPost("Admin/PushDefaults")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<ActionResult> ApplyDefaultsToAllUsers([FromQuery] bool overwrite = false)
        => ApplyDefaults(new MoonfinApplyDefaultsRequest { Overwrite = overwrite });

    /// <summary>
    /// Applies every profile's admin defaults to a single user (admin only). Kept for callers of
    /// the old route, the admin page uses <see cref="ApplyDefaults"/>.
    /// </summary>
    [HttpPost("Admin/PushDefaults/{userId}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<ActionResult> ApplyDefaultsToUser([FromRoute] Guid userId, [FromQuery] bool overwrite = false)
        => ApplyDefaults(new MoonfinApplyDefaultsRequest { Overwrite = overwrite, UserIds = [userId] });

    /// <summary>
    /// Pushes the admin defaults onto users' existing settings (admin only). Merge writes the set
    /// defaults of the chosen profiles, optionally only some keys, and keeps everything else a user
    /// chose. Overwrite resets each user to a clean file holding only those defaults, and for all
    /// users also removes the files of users that no longer exist.
    /// </summary>
    [HttpPost("Admin/ApplyDefaults")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> ApplyDefaults([FromBody] MoonfinApplyDefaultsRequest request)
    {
        var config = MoonfinPlugin.Instance?.Configuration;

        if (config?.EnableSettingsSync != true)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Settings sync is disabled" });
        }

        var profiles = ProfilesOrAll(request.Profiles);
        if (profiles == null)
        {
            return BadRequest(new { error = UnknownProfileError });
        }

        if (profiles.All(p => config.GetDefaultsFor(p) == null))
        {
            return BadRequest(new { error = "No default user settings configured" });
        }

        if (request.Overwrite && request.Keys != null)
        {
            return BadRequest(new { error = "A full reset applies every default, so it can't take a list of settings" });
        }

        if (request.UserIds != null && (request.UserIds.Count == 0 || request.UserIds.Contains(Guid.Empty)))
        {
            return BadRequest(new { error = "A valid userId is required" });
        }

        var serverUserIds = request.UserIds == null ? GetAllServerUserIds() : null;
        int usersAffected;
        var orphansDeleted = 0;

        if (request.Overwrite)
        {
            var targets = request.UserIds ?? serverUserIds;
            if (targets == null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "User manager unavailable" });
            }

            (usersAffected, orphansDeleted) = await _settingsService.ResetUsersToDefaultsAsync(
                config,
                profiles,
                targets,
                deleteOrphans: request.UserIds == null);
        }
        else
        {
            usersAffected = await _settingsService.MergeDefaultsAsync(config, profiles, request.Keys, request.UserIds);
        }

        // Hidden libraries are part of the defaults as a whole, not a setting someone can pick.
        if (request.Keys == null)
        {
            await _hiddenLibraries.ApplyAsync(request.UserIds ?? serverUserIds ?? new List<Guid>());
        }

        var liveRefreshDeliveries = 0;
        if (request.UserIds == null)
        {
            liveRefreshDeliveries = _settingsService.BroadcastSystemEvent("settingsUpdated");
        }
        else
        {
            foreach (var userId in request.UserIds)
            {
                _settingsService.NotifySettingsChanged(userId);
            }
        }

        return Ok(new { success = true, overwrite = request.Overwrite, usersAffected, orphansDeleted, liveRefreshDeliveries });
    }

    /// <summary>
    /// Copies one user's saved Moonfin settings onto other users (admin only). Merge keeps each
    /// target's values for whatever the copy leaves out, replace makes every copied profile an exact
    /// copy. API keys and hidden items always stay with their owner.
    /// </summary>
    [HttpPost("Admin/CopySettings")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> CopySettings([FromBody] MoonfinCopySettingsRequest request)
    {
        var config = MoonfinPlugin.Instance?.Configuration;

        if (config?.EnableSettingsSync != true)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Settings sync is disabled" });
        }

        var targets = (request.ToUserIds ?? new List<Guid>()).Where(id => id != Guid.Empty && id != request.FromUserId).Distinct().ToList();
        if (request.FromUserId == Guid.Empty || targets.Count == 0)
        {
            return BadRequest(new { error = "A user to copy from and at least one other user to copy to are required" });
        }

        var profiles = ProfilesOrAll(request.Profiles);
        if (profiles == null)
        {
            return BadRequest(new { error = UnknownProfileError });
        }

        if (request.Replace && request.Keys != null)
        {
            return BadRequest(new { error = "Replace copies whole profiles, so it can't take a list of settings" });
        }

        if (await _settingsService.GetUserSettingsAsync(request.FromUserId) == null)
        {
            return NotFound(new { error = "That user has no saved Moonfin settings" });
        }

        var usersAffected = await _settingsService.CopySettingsAsync(request.FromUserId, targets, profiles, request.Keys, request.Replace);
        return Ok(new { success = true, replace = request.Replace, usersAffected });
    }

    private static readonly string UnknownProfileError =
        "Unknown profile. Valid profiles: " + string.Join(", ", MoonfinUserSettings.ValidProfiles);

    /// <summary>The requested profile names, every profile when none were given, or null when one isn't valid.</summary>
    private static List<string>? ProfilesOrAll(List<string>? requested)
    {
        if (requested is not { Count: > 0 })
        {
            return MoonfinUserSettings.ValidProfiles.ToList();
        }

        return requested.All(p => MoonfinUserSettings.ValidProfiles.Contains(p.ToLowerInvariant())) ? requested : null;
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

        // Sanitize up front so the live SSE text, the push text and the stored copy all agree.
        var message = XmlText.Sanitize(request.Message).Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            return BadRequest(new { Error = "message is required" });
        }

        var deliveries = _settingsService.BroadcastMessage(message);

        // Also save it, so users who were not connected still find it in the app. Older
        // clients keep reading the "adminMessage" event above and ignore this.
        var stored = new ServerMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            Body = message,
            Color = ServerMessage.ColorWhite,
            Delivery = ServerMessage.DeliveryPopup,
            Audience = ServerMessage.AudienceAll,
            CreatedUtc = DateTime.UtcNow,
            CreatedByUserId = this.GetUserIdFromClaims()?.ToString("N")
        };

        stored.Sanitize();

        config.Messages.Add(stored);
        ServerMessage.Prune(config.Messages);
        MoonfinPlugin.Instance?.SaveConfiguration();
        _settingsService.BroadcastSystemEvent("messagesChanged");

        // SSE only reaches clients that are open right now, so push covers
        // backgrounded and killed apps. The route opens the messages window on tap.
        var pushTargets = 0;
        foreach (var userId in _notificationStore.GetUsersWithDevices())
        {
            _pushDelivery.QueueToUser(userId, "Message from your server", message, route: "messages");
            pushTargets++;
        }

        return Ok(new { Success = true, Deliveries = deliveries, PushTargets = pushTargets });
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
            var adminDefaults = _settingsService.AdminDefaultsFor(profile);
            return adminDefaults != null ? Ok(adminDefaults) : NotFound(new { Error = "No settings found" });
        }

        // A pull is the only place a client learns about hides made elsewhere, so this is where
        // its baseline moves.
        await _settingsService.RecordHiddenContentBaselineAsync(userId.Value, profile, resolved);

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
            ?? _settingsService.AdminDefaultsFor(resolvedProfileName)
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
            ?? _settingsService.AdminDefaultsFor(resolvedProfileName)
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

        if (!UserReflection.ApplyUser(query, queryUser))
        {
            return Ok(new { Items = Array.Empty<object>() });
        }

        var genres = _libraryManager.GetItemsResult(query).Items
            .Where(g => !string.IsNullOrWhiteSpace(g.Name) && UserReflection.IsVisibleTo(g, queryUser))
            .Select(g => new { Id = g.Id.ToString("N"), Name = g.Name })
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new { Items = genres });
    }

    /// <summary>
    /// Gets similar items (recommendations) for an item scored by Moonfin's recommendation algorithm.
    /// </summary>
    /// <param name="id">Item ID of the seed movie or series.</param>
    /// <param name="userId">Optional user ID for library filtering.</param>
    /// <param name="limit">Optional maximum number of suggestions to return. Defaults to 20 and is capped at 200.</param>
    /// <param name="excludeItemIds">Optional item IDs to exclude from results.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Scored similar items, as the same trimmed card shape the other Moonfin browse endpoints return.</returns>
    [HttpGet("Items/{id}/Similar")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetSimilarItems(
        [FromRoute] Guid id,
        [FromQuery] Guid? userId,
        [FromQuery] int? limit,
        [FromQuery] Guid[]? excludeItemIds,
        CancellationToken cancellationToken)
    {
        // The provider pipeline rechecks this per request, so this endpoint has to as well or
        // turning recommendations off leaves it serving scored results anyway. Empty rather than
        // 404 because that's what the client falls back to stock similars on.
        if (MoonfinPlugin.Instance?.Configuration?.RecommendationsProviderEnabled == false)
        {
            return Ok(new { Items = Array.Empty<object>(), TotalRecordCount = 0 });
        }

        var item = _libraryManager.GetItemById(id);
        if (item == null)
        {
            return NotFound();
        }

        var effectiveUserId = (userId.HasValue && userId.Value != Guid.Empty) ? userId : this.GetUserIdFromClaims();
        var queryUser = (effectiveUserId.HasValue && effectiveUserId.Value != Guid.Empty) ? ResolveQueryUser(effectiveUserId.Value) : null;

        // Without a resolved user we can't tell which libraries this caller is allowed to see, so
        // return nothing rather than everything.
        if (queryUser == null)
        {
            return Ok(new { Items = Array.Empty<object>(), TotalRecordCount = 0 });
        }

        var items = await _similarItemsService.GetSimilarItemsAsync(
            item,
            queryUser,
            limit,
            excludeItemIds,
            cancellationToken).ConfigureAwait(false);

        // The query only applies parental ratings and blocked tags. Library access is a separate
        // check every other browse endpoint here makes, so make it here too.
        var dtos = items.Where(i => UserReflection.IsVisibleTo(i, queryUser)).Select(i => _cardMapper.Map(i, queryUser)).ToList();
        return Ok(new
        {
            Items = dtos,
            TotalRecordCount = dtos.Count
        });
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

        MoonfinSettingsProfile? resolved = null;
        try
        {
            resolved = await _settingsService.GetResolvedProfileAsync(userId.Value, profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to read settings for user {UserId} in GetMediaBarItems; falling back to random items", userId.Value);
        }

        var isFallback = resolved == null;
        var settings = resolved ?? _settingsService.AdminDefaultsFor(profile) ?? new MoonfinSettingsProfile();

        var sourceType = isFallback ? "random" : (settings.MediaBarSourceType ?? "random");
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

        // The picked libraries and collections draw as one pool whatever the source type, and the
        // library and collection sources from before fall through to the random draw.
        var libraryIds = isFallback ? null : settings.MediaBarLibraryIds;
        var collectionIds = isFallback ? null : settings.MediaBarCollectionIds;
        var hideWatchedInRecent = !isFallback && settings.HideWatchedInRecentRows == true;
        var items = sourceType switch
        {
            "recentlyAdded" => GetLibraryItems(libraryIds, collectionIds, limit, queryUser, excludedGenres, ItemSortBy.DateCreated, hideWatchedInRecent),
            "recentlyReleased" => GetLibraryItems(libraryIds, collectionIds, limit, queryUser, excludedGenres, ItemSortBy.PremiereDate, hideWatchedInRecent),
            "newReleases" => GetNewReleaseSeries(libraryIds, collectionIds, settings.MediaBarNewReleaseDays ?? 30, limit, queryUser, excludedGenres),
            _ => GetLibraryItems(libraryIds, collectionIds, limit, queryUser, excludedGenres)
        };

        // When nothing that aired in the window has a backdrop, the bar shows random titles instead, the way the app does.
        if (sourceType == "newReleases" && !items.Any(HasBackdropImage))
        {
            items = GetLibraryItems(libraryIds, collectionIds, limit, queryUser, excludedGenres);
        }

        var dtos = items
            .Where(HasBackdropImage)
            .Select(i => _cardMapper.Map(i, queryUser))
            .ToList();

        return Ok(new
        {
            Items = dtos,
            TotalRecordCount = dtos.Count
        });
    }

    private static bool HasBackdropImage(BaseItem item)
    {
        return item.GetImageInfo(ImageType.Backdrop, 0) != null;
    }

    private object? ResolveQueryUser(Guid userId) =>
        HttpContext?.RequestServices is { } services ? UserReflection.ResolveUser(services, userId) : null;

    private List<Guid>? GetAllServerUserIds()
    {
        if (_userManagerType == null ||
    (_userManagerGetUsersIds == null && _userManagerUsersIdsProperty == null &&
     _userManagerGetUsers == null && _userManagerUsersProperty == null))
        {
            return null;
        }

        var userManager = HttpContext?.RequestServices.GetService(_userManagerType);
        if (userManager == null)
        {
            return null;
        }

        // Preferred path: ask the server for the Guid list directly.
        object? idsObject = _userManagerGetUsersIds != null
            ? _userManagerGetUsersIds.Invoke(userManager, null)
            : _userManagerUsersIdsProperty?.GetValue(userManager);
        if (idsObject is IEnumerable<Guid> guidIds)
        {
            return guidIds.ToList();
        }

        // Fallback: enumerate User objects and reflect their Id (older shapes / safety).
        object? usersObject = _userManagerGetUsers != null
            ? _userManagerGetUsers.Invoke(userManager, null)
            : _userManagerUsersProperty?.GetValue(userManager);
        if (usersObject is not IEnumerable<object> users)
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

    private BaseItem? GetItemByIdForUser(Guid itemId, object queryUser)
    {
        var query = new InternalItemsQuery
        {
            ItemIds = [itemId],
            Limit = 1
        };

        if (!UserReflection.ApplyUser(query, queryUser))
        {
            return null;
        }

        var item = _libraryManager.GetItemsResult(query).Items.FirstOrDefault(i => i.Id == itemId);
        return item != null && UserReflection.IsVisibleTo(item, queryUser) ? item : null;
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

        if (!UserReflection.ApplyUser(genreQuery, queryUser)) return Array.Empty<Guid>();

        var allGenres = _libraryManager.GetItemsResult(genreQuery).Items
            .Where(g => UserReflection.IsVisibleTo(g, queryUser))
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
    /// Queries Movie/Series items from the picked libraries and collections as one pool, shuffled or
    /// newest first by <paramref name="order"/>, with genre exclusions applied at the database level.
    /// With nothing picked it reads every library.
    /// </summary>
    private List<BaseItem> GetLibraryItems(List<string>? libraryIds, List<string>? collectionIds, int limit, object queryUser, List<string>? excludedGenreIds = null, ItemSortBy order = ItemSortBy.Random, bool excludePlayed = false)
    {
        var includedGenreIds = GetIncludedGenreIds(excludedGenreIds, queryUser);

        if (includedGenreIds is { Length: 0 }) return [];

        // Titles without a backdrop are left off the bar, so a newest-first list reads further ahead to still fill it.
        var fetchLimit = order == ItemSortBy.Random ? limit : Math.Max(limit * 4, 50);

        var collectionCount = collectionIds?.Count ?? 0;
        if (libraryIds is not { Count: > 0 } && collectionCount == 0)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                Limit = fetchLimit,
                Recursive = true
            };

            if (includedGenreIds != null) query.GenreIds = includedGenreIds;
            if (excludePlayed) query.IsPlayed = false;

            if (!UserReflection.ApplyUser(query, queryUser)) return [];

            SetMediaBarOrder(query, order);
            return _libraryManager.GetItemsResult(query).Items
                .Where(item => UserReflection.IsVisibleTo(item, queryUser) && HasBackdropImage(item))
                .Take(limit)
                .ToList();
        }

        List<Guid> allowedLibraryIds = libraryIds is { Count: > 0 } ? VisibleLibraryIds(libraryIds, queryUser) : [];
        List<BaseItem> collectionItems = collectionCount > 0 ? GetCollectionItems(collectionIds!, int.MaxValue, queryUser, excludedGenreIds, excludePlayed) : [];
        if (allowedLibraryIds.Count == 0 && collectionItems.Count == 0) return [];

        var allItems = new List<BaseItem>();
        var seenIds = new HashSet<Guid>();
        // Newest first has to compare every library's newest, so each gives the full count.
        var perLibraryLimit = order == ItemSortBy.Random ? Math.Max(1, limit / (allowedLibraryIds.Count + collectionCount) + 1) : fetchLimit;

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
            if (excludePlayed) query.IsPlayed = false;

            if (!UserReflection.ApplyUser(query, queryUser)) return [];

            SetMediaBarOrder(query, order);

            foreach (var item in _libraryManager.GetItemsResult(query).Items)
            {
                if (!seenIds.Add(item.Id)) continue;
                if (!UserReflection.IsVisibleTo(item, queryUser)) continue;
                allItems.Add(item);
            }
        }

        // A random draw gives each collection the share a library gets, so one big collection
        // doesn't crowd the libraries out.
        var fromCollections = order == ItemSortBy.Random
            ? collectionItems.OrderBy(_ => Random.Shared.Next()).Take(perLibraryLimit * collectionCount)
            : collectionItems.Where(item => order != ItemSortBy.PremiereDate || item.PremiereDate <= DateTime.UtcNow);
        foreach (var item in fromCollections)
        {
            if (seenIds.Add(item.Id)) allItems.Add(item);
        }

        if (order != ItemSortBy.Random)
        {
            return allItems
                .Where(HasBackdropImage)
                .OrderByDescending(item => order == ItemSortBy.DateCreated ? item.DateCreated : item.PremiereDate ?? DateTime.MinValue)
                .Take(limit)
                .ToList();
        }

        for (var i = allItems.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (allItems[i], allItems[j]) = (allItems[j], allItems[i]);
        }

        return allItems.Take(limit).ToList();
    }

    /// <summary>
    /// The libraries in <paramref name="libraryIds"/> the user can see. Any the caller has no access
    /// to are dropped silently, so settings carried over from a previous state (or forged by a
    /// client) can't leak restricted content.
    /// </summary>
    private List<Guid> VisibleLibraryIds(List<string> libraryIds, object queryUser)
    {
        var allowedLibraryIds = new List<Guid>();
        foreach (var libId in libraryIds)
        {
            if (!Guid.TryParse(libId, out var parentGuid)) continue;
            var parent = _libraryManager.GetItemById(parentGuid);
            if (parent == null || !UserReflection.IsVisibleTo(parent, queryUser)) continue;
            allowedLibraryIds.Add(parentGuid);
        }

        return allowedLibraryIds;
    }

    /// <summary>
    /// Series with an episode on the server that aired within the last <paramref name="days"/> days,
    /// newest first. A series' own premiere date is its first season's, so the episodes are queried
    /// and brought back to their series.
    /// </summary>
    private List<BaseItem> GetNewReleaseSeries(List<string>? libraryIds, List<string>? collectionIds, int days, int limit, object queryUser, List<string>? excludedGenreIds = null)
    {
        var excludedNames = ResolveExcludedGenreNames(excludedGenreIds);

        // The window comes from synced settings, so keep it to the 7 to 90 days the apps offer.
        days = Math.Clamp(days, 7, 90);
        var now = DateTime.UtcNow;
        var episodeQuery = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            MinPremiereDate = now.AddDays(-days),
            MaxPremiereDate = now,
            IsVirtualItem = false,
            Limit = 300,
            Recursive = true
        };

        if (libraryIds is { Count: > 0 } || collectionIds is { Count: > 0 })
        {
            List<Guid> ancestorIds = libraryIds is { Count: > 0 } ? VisibleLibraryIds(libraryIds, queryUser) : [];
            // A collection links its series rather than holding their episodes, so its series stand in for it.
            if (collectionIds is { Count: > 0 })
            {
                ancestorIds.AddRange(GetCollectionItems(collectionIds, int.MaxValue, queryUser).OfType<Series>().Select(item => item.Id));
            }

            if (ancestorIds.Count == 0) return [];
            episodeQuery.AncestorIds = ancestorIds.ToArray();
        }

        if (!UserReflection.ApplyUser(episodeQuery, queryUser)) return [];
        SetOrder(episodeQuery, ItemSortBy.PremiereDate, descending: true);

        var seen = new HashSet<Guid>();
        var series = new List<BaseItem>();
        // Hidden or excluded series, or ones without a backdrop, can use up a whole page of episodes, so a few more pages are read before giving up.
        for (var page = 0; page < 3 && series.Count < limit; page++)
        {
            episodeQuery.StartIndex = page * 300;
            var episodes = _libraryManager.GetItemsResult(episodeQuery).Items;
            var seriesIds = episodes
                .OfType<Episode>()
                .Select(episode => episode.SeriesId)
                .Where(id => id != Guid.Empty && seen.Add(id))
                .ToArray();

            if (seriesIds.Length > 0)
            {
                var seriesQuery = new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Series],
                    ItemIds = seriesIds,
                    Recursive = true
                };

                if (!UserReflection.ApplyUser(seriesQuery, queryUser)) return [];

                var rank = seriesIds.Select((id, index) => (id, index)).ToDictionary(pair => pair.id, pair => pair.index);
                series.AddRange(_libraryManager.GetItemsResult(seriesQuery).Items
                    .Where(item => UserReflection.IsVisibleTo(item, queryUser) && HasBackdropImage(item))
                    .Where(item => excludedNames == null || !item.Genres.Any(g => excludedNames.Contains(g)))
                    .OrderBy(item => rank[item.Id]));
            }

            if (episodes.Count < 300) break;
        }

        return series.Take(limit).ToList();
    }

    /// <summary>
    /// Shuffles the library source, and lists the dated ones newest first. A release date still to
    /// come isn't recently released.
    /// </summary>
    private void SetMediaBarOrder(InternalItemsQuery query, ItemSortBy order)
    {
        if (order == ItemSortBy.PremiereDate) query.MaxPremiereDate = DateTime.UtcNow;
        SetOrder(query, order, descending: order != ItemSortBy.Random);
    }

    /// <summary>
    /// Sets OrderBy on the query using reflection, avoiding a direct reference to SortOrder,
    /// which moved between Jellyfin 10.10 and 10.11.
    /// </summary>
    private void SetOrder(InternalItemsQuery query, ItemSortBy sortBy, bool descending)
    {
        try
        {
            var orderByProp = typeof(InternalItemsQuery).GetProperty(nameof(InternalItemsQuery.OrderBy));
            if (orderByProp == null) return;

            var elementType = orderByProp.PropertyType.GetGenericArguments()[0];
            var sortOrderType = elementType.GetGenericArguments()[1];
            var direction = Enum.ToObject(sortOrderType, descending ? 1 : 0);

            var tuple = Activator.CreateInstance(elementType, sortBy, direction);
            var array = Array.CreateInstance(elementType, 1);
            array.SetValue(tuple, 0);

            orderByProp.SetValue(query, array);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set the sort order via reflection, so items will use the default order");
        }
    }

    /// <summary>
    /// Queries items from specified collections/playlists, filtered to Movie/Series.
    /// Collection items are iterated individually, so genre exclusion is applied in-memory.
    /// </summary>
    private List<BaseItem> GetCollectionItems(List<string> collectionIds, int limit, object queryUser, List<string>? excludedGenreIds = null, bool excludePlayed = false)
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

            if (excludePlayed) query.IsPlayed = false;

            if (!UserReflection.ApplyUser(query, queryUser)) return [];

            foreach (var item in _libraryManager.GetItemsResult(query).Items)
            {
                if (!seenIds.Add(item.Id)) continue;
                if (!UserReflection.IsVisibleTo(item, queryUser)) continue;
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

public class MoonfinApplyDefaultsRequest
{
    /// <summary>Reset each user to a clean file holding only the defaults, instead of merging.</summary>
    public bool Overwrite { get; set; }

    /// <summary>The users to apply to. Null means all of them.</summary>
    public List<Guid>? UserIds { get; set; }

    /// <summary>The profiles whose defaults are applied: global, tv, desktop, mobile. Empty means all.</summary>
    public List<string>? Profiles { get; set; }

    /// <summary>The settings to push, by JSON key. Null means every set default.</summary>
    public List<string>? Keys { get; set; }
}

public class MoonfinCopySettingsRequest
{
    public Guid FromUserId { get; set; }

    public List<Guid>? ToUserIds { get; set; }

    /// <summary>The profiles to copy: global, tv, desktop, mobile. Empty means all the source has.</summary>
    public List<string>? Profiles { get; set; }

    /// <summary>The settings to copy, by JSON key. Null means everything the source saved.</summary>
    public List<string>? Keys { get; set; }

    /// <summary>Make each copied profile an exact copy instead of merging into the target's.</summary>
    public bool Replace { get; set; }
}

/// <summary>
/// Response for the ping endpoint.
/// </summary>
public class MoonfinPingResponse
{
    // The host serializes member names verbatim, so the documented camelCase
    // is pinned here.
    [JsonPropertyName("installed")]
    public bool Installed { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("settingsSyncEnabled")]
    public bool? SettingsSyncEnabled { get; set; }

    [JsonPropertyName("serverName")]
    public string? ServerName { get; set; }

    [JsonPropertyName("seerrEnabled")]
    public bool? SeerrEnabled { get; set; }

    [JsonPropertyName("seerrUrl")]
    public string? SeerrUrl { get; set; }

    [JsonPropertyName("mdblistAvailable")]
    public bool? MdblistAvailable { get; set; }

    [JsonPropertyName("tmdbAvailable")]
    public bool? TmdbAvailable { get; set; }

    /// <summary>
    /// True when this plugin has the Tmdb/Thumb endpoint. Older plugins leave it out, so the app
    /// keeps the backdrop Seerr gave it.
    /// </summary>
    [JsonPropertyName("tmdbThumbsSupported")]
    public bool? TmdbThumbsSupported { get; set; }

    /// <summary>
    /// True when this plugin has the Tmdb/PersonLinks endpoint. Older plugins leave it out, so
    /// the app shows only the IMDb and TMDB links it can build itself.
    /// </summary>
    [JsonPropertyName("tmdbPersonLinksSupported")]
    public bool? TmdbPersonLinksSupported { get; set; }

    /// <summary>
    /// True when this plugin has the Tmdb/Reviews endpoint. Older plugins leave it out, so the
    /// app hides the Reviews section rather than asking for it.
    /// </summary>
    [JsonPropertyName("tmdbReviewsSupported")]
    public bool? TmdbReviewsSupported { get; set; }

    /// <summary>
    /// True when this plugin has the Tmdb/Details endpoint. Older plugins leave it out, so the
    /// app shows an outside title with only what its list carried.
    /// </summary>
    [JsonPropertyName("tmdbDetailsSupported")]
    public bool? TmdbDetailsSupported { get; set; }

    /// <summary>
    /// True when this plugin has the admin messages endpoints. Older plugins leave it out, so
    /// the app reads a missing value as false and hides the button.
    /// </summary>
    [JsonPropertyName("messagesSupported")]
    public bool? MessagesSupported { get; set; }

    /// <summary>
    /// True when this plugin keeps PINs on the server. Older plugins leave it out, so the app
    /// keeps using the PIN stored on the device.
    /// </summary>
    [JsonPropertyName("pinSupported")]
    public bool? PinSupported { get; set; }

    /// <summary>
    /// True when the admin turned on smart search, so clients can call /Moonfin/SmartSearch.
    /// </summary>
    [JsonPropertyName("smartSearchEnabled")]
    public bool? SmartSearchEnabled { get; set; }

    /// <summary>
    /// True when this plugin has the incomplete Live TV recordings endpoints and the admin left
    /// detection on. Older plugins leave it out, so the app hides the badge, the Record prompt and
    /// the setting.
    /// </summary>
    [JsonPropertyName("incompleteRecordingsSupported")]
    public bool? IncompleteRecordingsSupported { get; set; }

    /// <summary>Movie recovery and user-confirmed current-guide suggestions.</summary>
    [JsonPropertyName("extendedCompleteShowingsSupported")]
    public bool? ExtendedCompleteShowingsSupported { get; set; }

    /// <summary>
    /// True when this plugin supports server-side recommendations scoring.
    /// </summary>
    [JsonPropertyName("recommendationsSupported")]
    public bool? RecommendationsSupported { get; set; }

    /// <summary>
    /// True when the websocket relay for clients without TLS sockets, such as Roku, is on.
    /// </summary>
    [JsonPropertyName("socketRelay")]
    public bool? SocketRelay { get; set; }

    /// <summary>
    /// True when Seerr is set up for the arr calendar and this user passes the admins-only setting.
    /// </summary>
    [JsonPropertyName("calendarAvailable")]
    public bool? CalendarAvailable { get; set; }

    [JsonPropertyName("calendarDaysBack")]
    public int? CalendarDaysBack { get; set; }

    [JsonPropertyName("calendarDaysAhead")]
    public int? CalendarDaysAhead { get; set; }

    /// <summary>
    /// True when this plugin has the Calendar/Reminders endpoints.
    /// </summary>
    [JsonPropertyName("calendarReminders")]
    public bool? CalendarReminders { get; set; }

    /// <summary>
    /// True when this plugin has the Seerr/Radarr/Queue and Seerr/Sonarr/Queue endpoints and Seerr
    /// is set up for them. Removing a download from the queue is for server admins only.
    /// </summary>
    [JsonPropertyName("arrQueueAvailable")]
    public bool? ArrQueueAvailable { get; set; }

    [JsonPropertyName("defaultSettings")]
    public MoonfinSettingsProfile? DefaultSettings { get; set; }
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
