using System.Reflection;
using MediaBrowser.Model.Plugins;
using Moonfin.Server.Models;

namespace Moonfin.Server;

/// <summary>
/// Admin-level plugin configuration for Moonfin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Enable settings sync across Moonfin clients.
    /// </summary>
    public bool EnableSettingsSync { get; set; } = true;

    /// <summary>
    /// Enable Seerr integration for all users.
    /// </summary>
    public bool SeerrEnabled { get; set; } = false;

    /// <summary>
    /// Seerr server URL for server-to-server communication from Jellyfin.
    /// Example: http://seerr:5055 or http://192.168.50.20:5055
    /// </summary>
    public string? SeerrUrl { get; set; }

    /// <summary>
    /// Optional display name override (e.g., "Requests", "Media Requests").
    /// Leave empty to auto-detect based on server version.
    /// </summary>
    public string? SeerrDisplayName { get; set; }

    // Legacy keys from before the Jellyseerr -> Seerr rename. Kept only so existing
    // configs deserialize; MigrateLegacyKeys() copies them into the Seerr* keys on load.
    public string? JellyseerrUrl { get; set; }
    public bool JellyseerrEnabled { get; set; }
    public string? JellyseerrDisplayName { get; set; }

    /// <summary>
    /// Shared secret Seerr must present (header or query) when calling the Moonfin
    /// webhook. Auto-generated on first load if empty.
    /// </summary>
    public string? SeerrWebhookSecret { get; set; }

    /// <summary>
    /// Optional public base URL of this server (scheme + host, no trailing slash) used to
    /// build the webhook URL Seerr calls. Falls back to the request origin when empty.
    /// Example: https://jellyfin.example.com
    /// </summary>
    public string? PublicServerUrl { get; set; }

    /// <summary>
    /// Server-wide MDBList API key shared with all users.
    /// Users who set their own key will use that instead.
    /// </summary>
    public string? MdblistApiKey { get; set; }

    /// <summary>
    /// Server-wide TMDB API key shared with all users.
    /// Users who set their own key will use that instead.
    /// </summary>
    public string? TmdbApiKey { get; set; }

    /// <summary>
    /// Fetch and cache MDBList official lists (curated charts) on a schedule so clients
    /// can show them as home rows. Uses the server-wide MDBList key above.
    /// </summary>
    public bool MdblistOfficialListsEnabled { get; set; } = true;

    /// <summary>
    /// Fetch and cache IMDb lists (curated charts) on a schedule so clients can show them as home rows.
    /// </summary>
    public bool ImdbListsEnabled { get; set; } = true;

    /// <summary>
    /// Maximum number of items cached per official list. Caps cache size and API calls
    /// (250 covers charts like IMDb Top 250).
    /// </summary>
    public int MdblistOfficialListsMaxItems { get; set; } = 250;

    /// <summary>
    /// Fetch and cache TMDB studio (production company) logos on a schedule so clients
    /// can show them on the details screen. Uses the server-wide TMDB key above.
    /// </summary>
    public bool StudioLogosEnabled { get; set; } = true;

    /// <summary>
    /// How long a cached studio-logo entry stays fresh before the sync task refetches it.
    /// </summary>
    public int StudioLogosMaxAgeDays { get; set; } = 30;

    /// <summary>
    /// Optional default server URL shown in the Moonfin web Add Server dialog.
    /// </summary>
    public string? WebDefaultServerUrl { get; set; }

    /// <summary>
    /// Optional forced server URL for Moonfin web plugin mode auto-connect.
    /// </summary>
    public string? WebForcedServerUrl { get; set; }

    /// <summary>
    /// Enable WebRTC private subnet scan when running Moonfin web plugin mode.
    /// </summary>
    public bool WebEnableWebRtcScan { get; set; } = true;

    /// <summary>
    /// Firebase service-account JSON, pasted by the admin, used to mint FCM access tokens
    /// for push delivery. This is a secret: it is never logged. Leave empty to disable push.
    /// </summary>
    public string? FcmServiceAccountJson { get; set; }

    /// <summary>
    /// Optional path to a Firebase service-account JSON file on the server. When both this
    /// and <see cref="FcmServiceAccountJson"/> are set, the file path wins.
    /// </summary>
    public string? FcmServiceAccountPath { get; set; }

    /// <summary>
    /// The relay key this build was stamped with, or empty when it was built without one.
    /// Set by passing -p:MoonfinRelayAppKey=... at build time, which the build scripts read
    /// from a gitignored .env. A build without a key simply leaves push disabled.
    /// </summary>
    private static readonly string BuiltInRelayAppKey =
        typeof(PluginConfiguration).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "MoonfinRelayAppKey")?.Value
        ?? string.Empty;

    /// <summary>
    /// URL of the hosted push relay that holds the shared service account and forwards to FCM.
    /// The plugin POSTs tokens and payload here by default so self-hosters need no service account.
    /// </summary>
    public string PushRelayUrl { get; set; } = "https://push.moonfin.io/send";

    /// <summary>
    /// Optional override for the relay app key, for anyone running their own relay. When empty
    /// the key stamped into the build is used. This is a secret and is never logged.
    /// </summary>
    public string? PushRelayAppKey { get; set; }

    /// <summary>
    /// Effective relay app key: the admin override when set, otherwise whatever this build was
    /// stamped with. Returns null when neither is available, meaning the relay isn't usable.
    /// </summary>
    public string? GetRelayAppKey()
    {
        var key = !string.IsNullOrWhiteSpace(PushRelayAppKey) ? PushRelayAppKey : BuiltInRelayAppKey;
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    /// <summary>
    /// True when a service account is configured (inline JSON or file path) for direct FCM sends.
    /// </summary>
    public bool HasServiceAccount =>
        !string.IsNullOrWhiteSpace(FcmServiceAccountPath) ||
        !string.IsNullOrWhiteSpace(FcmServiceAccountJson);

    /// <summary>
    /// True when push can run: either a service account (direct self-hosted send) or a usable
    /// relay key (hosted default send) is available.
    /// </summary>
    public bool PushEnabled =>
        HasServiceAccount ||
        GetRelayAppKey() != null;

    /// <summary>
    /// Resolves the effective service-account JSON, preferring the file path when set.
    /// Returns null when neither source yields usable content.
    /// </summary>
    public string? GetFcmServiceAccountJson()
    {
        if (!string.IsNullOrWhiteSpace(FcmServiceAccountPath))
        {
            try
            {
                if (File.Exists(FcmServiceAccountPath))
                {
                    var fromFile = File.ReadAllText(FcmServiceAccountPath);
                    if (!string.IsNullOrWhiteSpace(fromFile))
                    {
                        return fromFile;
                    }
                }
            }
            catch
            {
                // Fall through to the inline value on any read error.
            }
        }

        return string.IsNullOrWhiteSpace(FcmServiceAccountJson) ? null : FcmServiceAccountJson;
    }

    /// <summary>
    /// Admin-configured default settings for all users.
    /// Users who haven't customized a setting will inherit this value.
    /// Users can override any default in their own Moonfin settings.
    /// </summary>
    public MoonfinSettingsProfile? DefaultUserSettings { get; set; }

    /// <summary>
    /// Metadata index for uploaded custom themes stored in the plugin data folder.
    /// </summary>
    public List<UploadedThemeEntry> UploadedThemes { get; set; } = new();

    /// <summary>
    /// Admin messages shown to users in the app. They are only a few KB of text, so they live
    /// in the config instead of the data folder.
    /// </summary>
    public List<ServerMessage> Messages { get; set; } = new();

    // ---------------------------------------------------------------------
    // Retro games (EmulatorJS) configuration
    // ---------------------------------------------------------------------

    /// <summary>
    /// Enables the retro-games (EmulatorJS) feature for all Moonfin clients.
    /// </summary>
    public bool GamesEnabled { get; set; } = false;

    /// <summary>
    /// Jellyfin library IDs (GUID strings) that hold retro game ROMs using the
    /// "System folder → BIOS + per-game folder" convention. When empty, libraries
    /// whose name contains "game", "rom", or "emulat" are auto-detected.
    /// </summary>
    public List<string> GameLibraryIds { get; set; } = new();

    /// <summary>
    /// Optional override for where EmulatorJS loads its runtime and cores from. When empty,
    /// self-hosted cores are used if installed under the plugin data folder, otherwise the
    /// EmulatorJS CDN. Advanced users can point this at their own mirror.
    /// </summary>
    public string? GamesCoreDataUrl { get; set; }

    /// <summary>
    /// Optional URL of an EmulatorJS cores zip (containing the data/ folder) that the
    /// "Download cores to server" button fetches. When empty, the plugin looks for an
    /// "emulatorjs-data.zip" asset on its own latest GitHub release.
    /// </summary>
    public string? GamesCoreZipUrl { get; set; }

    /// <summary>
    /// Enables keyless game metadata (genre, developer, year, ...) sourced from the libretro
    /// database. Files are fetched lazily per system and cached under the plugin data folder.
    /// </summary>
    public bool GamesMetadataEnabled { get; set; } = true;

    /// <summary>
    /// Base location for libretro <c>.rdb</c> files, ending in a slash. Defaults to the
    /// jsDelivr CDN mirror of libretro-database (no per-release maintenance). An http(s) value
    /// is downloaded; a local directory path is read directly for offline/air-gapped servers.
    /// </summary>
    public string GamesMetadataDbUrlBase { get; set; } =
        "https://cdn.jsdelivr.net/gh/libretro/libretro-database@master/rdb/";

    /// <summary>
    /// Enables rich game metadata (overview, genre, developer, year, ...) from the LaunchBox
    /// Games Database. The whole database is one ~100 MB download, fetched once and reduced to a
    /// compact per-system cache under the plugin data folder.
    /// </summary>
    public bool GamesLaunchBoxEnabled { get; set; } = true;

    /// <summary>URL of the LaunchBox metadata zip (contains Metadata.xml).</summary>
    public string GamesLaunchBoxUrl { get; set; } =
        "https://gamesdb.launchbox-app.com/Metadata.zip";

    /// <summary>
    /// Gets the effective Seerr URL for server-to-server communication.
    /// </summary>
    public string? GetEffectiveSeerrUrl()
    {
        return NormalizeSeerrUrl(SeerrUrl ?? JellyseerrUrl);
    }

    /// <summary>
    /// Copies any pre-rename Jellyseerr* values into the Seerr* keys and clears the
    /// legacy ones. Returns true when something changed so the caller can persist.
    /// </summary>
    public bool MigrateLegacyKeys()
    {
        var changed = false;

        if (string.IsNullOrEmpty(SeerrUrl) && !string.IsNullOrEmpty(JellyseerrUrl))
        {
            SeerrUrl = JellyseerrUrl;
            changed = true;
        }

        if (!SeerrEnabled && JellyseerrEnabled)
        {
            SeerrEnabled = true;
            changed = true;
        }

        if (string.IsNullOrEmpty(SeerrDisplayName) && !string.IsNullOrEmpty(JellyseerrDisplayName))
        {
            SeerrDisplayName = JellyseerrDisplayName;
            changed = true;
        }

        if (JellyseerrUrl != null || JellyseerrEnabled || JellyseerrDisplayName != null)
        {
            JellyseerrUrl = null;
            JellyseerrEnabled = false;
            JellyseerrDisplayName = null;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Ensures a webhook secret exists, generating one when empty. Returns true when a
    /// value was created so the caller can persist the change.
    /// </summary>
    public bool EnsureWebhookSecret()
    {
        if (string.IsNullOrWhiteSpace(SeerrWebhookSecret))
        {
            SeerrWebhookSecret = Guid.NewGuid().ToString("N");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Normalizes a user-entered Seerr URL so downstream <see cref="Uri"/> parsing and
    /// string concatenation always receive a sane absolute http/https address.
    /// Trims whitespace and surrounding quotes, prepends <c>http://</c> when no scheme
    /// is present (otherwise <c>new Uri("seerr:5055")</c> treats "seerr" as the scheme),
    /// strips trailing slashes, and validates the result. Returns <c>null</c> when the
    /// value is empty or not a usable http/https URL.
    /// </summary>
    public static string? NormalizeSeerrUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return null;
        }

        var value = rawUrl.Trim().Trim('"', '\'').Trim();
        if (value.Length == 0)
        {
            return null;
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "http://" + value;
        }

        value = value.TrimEnd('/');

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return value;
    }
}

/// <summary>
/// Metadata for an uploaded custom theme JSON file.
/// </summary>
public class UploadedThemeEntry
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTimeOffset UploadedAtUtc { get; set; }
    public string? UploadedByUserId { get; set; }
    public string ChecksumSha256 { get; set; } = string.Empty;
}

/// <summary>
/// One message the admin wrote for users to read in the app.
/// </summary>
public class ServerMessage
{
    /// <summary>Highest number of messages kept. Older ones are dropped on save.</summary>
    public const int MaxStored = 50;

    /// <summary>Body length cap, so a huge paste cannot break the app layout.</summary>
    public const int MaxBodyLength = 2000;

    /// <summary>Title length cap, the same limit the config page puts on its field.</summary>
    public const int MaxTitleLength = 120;

    public const string ColorGreen = "green";
    public const string ColorRed = "red";
    public const string ColorYellow = "yellow";
    public const string ColorBlue = "blue";
    public const string ColorWhite = "white";

    /// <summary>Only marks the menu button as unread.</summary>
    public const string DeliveryInbox = "inbox";

    /// <summary>Opens the message window once, until the user reads it.</summary>
    public const string DeliveryPopup = "popup";

    public const string AudienceAll = "all";
    public const string AudienceUsers = "users";
    public const string AudienceAdmins = "admins";

    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Color { get; set; } = ColorWhite;
    public string Delivery { get; set; } = DeliveryInbox;
    public string? ActionLabel { get; set; }
    public string? ActionUrl { get; set; }

    /// <summary>When the message starts showing. Null means right away.</summary>
    public DateTime? StartUtc { get; set; }

    /// <summary>When the message stops showing. Null means never.</summary>
    public DateTime? EndUtc { get; set; }

    public string Audience { get; set; } = AudienceAll;

    /// <summary>User IDs to show this to. Only used when <see cref="Audience"/> is "users".</summary>
    public List<string> TargetUserIds { get; set; } = new();

    public DateTime CreatedUtc { get; set; }
    public string? CreatedByUserId { get; set; }

    /// <summary>
    /// True when this message should show right now, for this user.
    /// </summary>
    public bool IsVisibleTo(Guid userId, bool isAdmin, DateTime nowUtc)
    {
        if (StartUtc.HasValue && nowUtc < StartUtc.Value)
        {
            return false;
        }

        if (EndUtc.HasValue && nowUtc >= EndUtc.Value)
        {
            return false;
        }

        return Audience switch
        {
            AudienceAdmins => isAdmin,
            AudienceUsers => TargetUserIds.Any(id =>
                Guid.TryParse(id, out var target) && target == userId),
            _ => true
        };
    }

    /// <summary>
    /// Cleans admin input before it is saved. Bad values fall back to the default instead of
    /// being rejected, except the action URL which is dropped when it is not http or https.
    /// </summary>
    public void Sanitize()
    {
        // Strip before cutting, so trimming an emoji in half can't put a lone surrogate back.
        Title = XmlText.Truncate(XmlText.Sanitize(Title).Trim(), MaxTitleLength);
        Body = XmlText.Truncate(XmlText.Sanitize(Body).Trim(), MaxBodyLength);

        Color = Color switch
        {
            ColorGreen => ColorGreen,
            ColorRed => ColorRed,
            ColorYellow => ColorYellow,
            ColorBlue => ColorBlue,
            _ => ColorWhite
        };

        Delivery = Delivery == DeliveryPopup ? DeliveryPopup : DeliveryInbox;

        Audience = Audience switch
        {
            AudienceUsers => AudienceUsers,
            AudienceAdmins => AudienceAdmins,
            _ => AudienceAll
        };

        if (Audience != AudienceUsers)
        {
            TargetUserIds = new List<string>();
        }
        else
        {
            TargetUserIds = TargetUserIds
                .Where(id => Guid.TryParse(id, out _))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        ActionLabel = XmlText.SanitizeOrNull(ActionLabel)?.Trim();
        ActionUrl = SanitizeActionUrl(ActionUrl);

        // A link with no label is useless to the user, and a label with no link does nothing.
        if (ActionUrl == null)
        {
            ActionLabel = null;
        }
        else if (ActionLabel == null)
        {
            ActionUrl = null;
        }

        // An end date before the start date would hide the message forever.
        if (StartUtc.HasValue && EndUtc.HasValue && EndUtc.Value <= StartUtc.Value)
        {
            EndUtc = null;
        }
    }

    /// <summary>
    /// Keeps only real http and https links. Anything else is dropped, since this URL gets
    /// opened on the user's device.
    /// </summary>
    private static string? SanitizeActionUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return null;
        }

        var value = rawUrl.Trim().Trim('"', '\'').Trim();

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        // AbsoluteUri keeps the escaping the admin typed, where ToString would decode it.
        return uri.AbsoluteUri;
    }

    /// <summary>
    /// Drops expired messages, then the oldest ones if the list is still too long. Keeps the
    /// config file from growing forever without needing a scheduled task.
    /// </summary>
    public static void Prune(List<ServerMessage> messages)
    {
        var now = DateTime.UtcNow;
        messages.RemoveAll(m => m.EndUtc.HasValue && m.EndUtc.Value < now);

        if (messages.Count <= MaxStored)
        {
            return;
        }

        var extra = messages
            .OrderBy(m => m.CreatedUtc)
            .Take(messages.Count - MaxStored)
            .ToList();

        foreach (var message in extra)
        {
            messages.Remove(message);
        }
    }
}
