using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Emby.Plugins.Moonfin.Models;
using MediaBrowser.Model.Plugins;

namespace Emby.Plugins.Moonfin
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public bool EnableSettingsSync { get; set; } = true;

        public bool SeerrEnabled { get; set; } = false;

        /// <summary>Seerr server URL for server-to-server calls. Example: http://seerr:5055</summary>
        public string? SeerrUrl { get; set; }

        /// <summary>Optional display name override. Leave empty to auto-detect.</summary>
        public string? SeerrDisplayName { get; set; }

        /// <summary>Shared secret Seerr must present (header or query) when calling the Moonfin webhook. Auto-generated on first load if empty.</summary>
        public string? SeerrWebhookSecret { get; set; }

        /// <summary>Optional public base URL of this server (scheme + host, no trailing slash) used to build the webhook URL Seerr calls. Falls back to the server's own address when empty. Example: https://emby.example.com</summary>
        public string? PublicServerUrl { get; set; }

        /// <summary>Firebase service-account JSON, pasted by the admin, used to mint FCM access tokens for direct push delivery. Secret: never logged. Leave empty to use the hosted relay instead.</summary>
        public string? FcmServiceAccountJson { get; set; }

        /// <summary>Optional path to a Firebase service-account JSON file on the server. When both this and FcmServiceAccountJson are set, the file path wins.</summary>
        public string? FcmServiceAccountPath { get; set; }

        /// <summary>The relay key this build was stamped with, or empty when it was built without one. Set by passing -p:MoonfinRelayAppKey=... at build time, which the build scripts read from a gitignored .env. A build without a key simply leaves push disabled.</summary>
        private static readonly string BuiltInRelayAppKey =
            typeof(PluginConfiguration).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "MoonfinRelayAppKey")?.Value
            ?? string.Empty;

        /// <summary>URL of the hosted push relay that holds the shared service account and forwards to FCM. The plugin posts tokens plus payload here by default so self-hosters need no service account.</summary>
        public string PushRelayUrl { get; set; } = "https://push.moonfin.io/send";

        /// <summary>Optional override for the relay app key, for anyone running their own relay. When empty the key stamped into the build is used. Secret: never logged.</summary>
        public string? PushRelayAppKey { get; set; }

        /// <summary>Effective relay app key: the admin override when set, otherwise whatever this build was stamped with. Returns null when neither is available, meaning the relay isn't usable.</summary>
        public string? GetRelayAppKey()
        {
            var key = !string.IsNullOrWhiteSpace(PushRelayAppKey) ? PushRelayAppKey : BuiltInRelayAppKey;
            if (string.IsNullOrWhiteSpace(key))
                return null;
            return key;
        }

        /// <summary>True when a service account is configured (inline JSON or file path) for direct FCM sends.</summary>
        public bool HasServiceAccount =>
            !string.IsNullOrWhiteSpace(FcmServiceAccountPath) ||
            !string.IsNullOrWhiteSpace(FcmServiceAccountJson);

        /// <summary>True when push can run: either a service account (direct self-hosted send) or a usable relay key (hosted default send) is available.</summary>
        public bool PushEnabled => HasServiceAccount || GetRelayAppKey() != null;

        /// <summary>Resolves the effective service-account JSON, preferring the file path when set. Returns null when neither source yields usable content.</summary>
        public string? GetFcmServiceAccountJson()
        {
            if (!string.IsNullOrWhiteSpace(FcmServiceAccountPath))
            {
                try
                {
                    if (System.IO.File.Exists(FcmServiceAccountPath))
                    {
                        var fromFile = System.IO.File.ReadAllText(FcmServiceAccountPath);
                        if (!string.IsNullOrWhiteSpace(fromFile)) return fromFile;
                    }
                }
                catch
                {
                    // Fall through to the inline value on any read error.
                }
            }

            return string.IsNullOrWhiteSpace(FcmServiceAccountJson) ? null : FcmServiceAccountJson;
        }

        /// <summary>Ensures a webhook secret exists, generating one when empty. Returns true when a value was created so the caller can persist the change.</summary>
        public bool EnsureWebhookSecret()
        {
            if (string.IsNullOrWhiteSpace(SeerrWebhookSecret))
            {
                SeerrWebhookSecret = Guid.NewGuid().ToString("N");
                return true;
            }

            return false;
        }

        /// <summary>Server-wide MDBList API key shared with all users.</summary>
        public string? MdblistApiKey { get; set; }

        /// <summary>Server-wide TMDB API key shared with all users.</summary>
        public string? TmdbApiKey { get; set; }

        /// <summary>Fetch and cache MDBList official lists on a schedule so clients can show them as home rows. Uses the server-wide MDBList key above.</summary>
        public bool MdblistOfficialListsEnabled { get; set; } = true;

        /// <summary>Maximum number of items cached per official list (250 covers charts like IMDb Top 250).</summary>
        public int MdblistOfficialListsMaxItems { get; set; } = 250;

        /// <summary>Fetch and cache IMDb charts (Top 250, Most Popular, etc.) on a schedule so clients can show them as home rows and custom rows can resolve the imdb source.</summary>
        public bool ImdbListsEnabled { get; set; } = true;

        /// <summary>Pre-warm the studio-logo cache from TMDB on a schedule so detail screens can show production-company logos. Uses the server-wide TMDB key above.</summary>
        public bool StudioLogosEnabled { get; set; } = true;

        /// <summary>How long a cached studio logo stays fresh before the sync refetches it.</summary>
        public int StudioLogosMaxAgeDays { get; set; } = 30;

        /// <summary>Optional default server URL shown in the Moonfin web Add Server dialog.</summary>
        public string? WebDefaultServerUrl { get; set; }

        /// <summary>Optional forced server URL for Moonfin web plugin mode auto-connect.</summary>
        public string? WebForcedServerUrl { get; set; }

        public bool WebEnableWebRtcScan { get; set; } = true;

        /// <summary>Admin-configured default settings. Users who haven't customized a setting inherit this value.</summary>
        public MoonfinSettingsProfile? DefaultUserSettings { get; set; }

        /// <summary>Metadata index for uploaded custom themes stored in the plugin data folder.</summary>
        public List<UploadedThemeEntry> UploadedThemes { get; set; } = new List<UploadedThemeEntry>();

        // Retro games (EmulatorJS) configuration.
        public bool GamesEnabled { get; set; } = false;
        public List<string> GameLibraryIds { get; set; } = new List<string>();
        public string? GamesCoreDataUrl { get; set; }
        public string? GamesCoreZipUrl { get; set; }
        public bool GamesMetadataEnabled { get; set; } = true;
        public string GamesMetadataDbUrlBase { get; set; } =
            "https://cdn.jsdelivr.net/gh/libretro/libretro-database@master/rdb/";
        public bool GamesLaunchBoxEnabled { get; set; } = true;
        public string GamesLaunchBoxUrl { get; set; } =
            "https://gamesdb.launchbox-app.com/Metadata.zip";

        // Legacy keys from before the Jellyseerr -> Seerr rename. Kept only so existing
        // configs deserialize. MigrateLegacyKeys() copies them into the Seerr* keys on load.
        public string? JellyseerrUrl { get; set; }
        public bool JellyseerrEnabled { get; set; }
        public string? JellyseerrDisplayName { get; set; }

        public string? GetEffectiveSeerrUrl() => NormalizeSeerrUrl(SeerrUrl ?? JellyseerrUrl);

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
        /// Normalizes a user-entered Seerr URL so downstream Uri parsing and string
        /// concatenation always receive a sane absolute http/https address. Trims
        /// whitespace and surrounding quotes, prepends http:// when no scheme is present
        /// (otherwise new Uri("seerr:5055") treats "seerr" as the scheme), strips trailing
        /// slashes, and validates the result. Returns null when empty or not http/https.
        /// </summary>
        public static string? NormalizeSeerrUrl(string? rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl)) return null;

            var value = rawUrl.Trim().Trim('"', '\'').Trim();
            if (value.Length == 0) return null;

            if (value.IndexOf("://", StringComparison.Ordinal) < 0)
                value = "http://" + value;

            value = value.TrimEnd('/');

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return null;

            return value;
        }
    }

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
}
