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
    /// Compile-time default relay key baked into distributed builds. The owner replaces this
    /// literal with the real key before publishing. While it stays the placeholder, the relay
    /// is treated as not configured.
    /// </summary>
    public const string DefaultRelayAppKey = "7e0f7287a3bd310ea0d2ff9c30e55027941062bf19806f2aeac56d32f64b1cba";

    /// <summary>
    /// URL of the hosted push relay that holds the shared service account and forwards to FCM.
    /// The plugin POSTs tokens and payload here by default so self-hosters need no service account.
    /// </summary>
    public string PushRelayUrl { get; set; } = "https://push.moonfin.io/send";

    /// <summary>
    /// Optional override for the relay app key. When empty, the compile-time
    /// <see cref="DefaultRelayAppKey"/> is used. This is a secret and is never logged.
    /// </summary>
    public string? PushRelayAppKey { get; set; }

    /// <summary>
    /// Effective relay app key: the admin override when set, otherwise the compile-time default.
    /// Returns null when the result is empty or still the unreplaced placeholder, meaning the
    /// relay is not usable.
    /// </summary>
    public string? GetRelayAppKey()
    {
        var key = !string.IsNullOrWhiteSpace(PushRelayAppKey) ? PushRelayAppKey : DefaultRelayAppKey;
        if (string.IsNullOrWhiteSpace(key) ||
            string.Equals(key, DefaultRelayAppKey, StringComparison.Ordinal))
        {
            return null;
        }

        return key;
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
