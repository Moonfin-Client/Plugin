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
    /// Enable Jellyseerr integration for all users.
    /// </summary>
    public bool JellyseerrEnabled { get; set; } = false;

    /// <summary>
    /// Jellyseerr server URL (admin-configured, public-facing).
    /// </summary>
    public string? JellyseerrUrl { get; set; }

    /// <summary>
    /// Direct URL for loading Jellyseerr/Seerr in iframe (optional).
    /// Use this when Seerr v3 (Next.js) doesn't work behind a reverse proxy subpath.
    /// When set, the iframe loads directly from this URL instead of through the proxy.
    /// Example: https://jellyseerr.yourdomain.com
    /// </summary>
    public string? JellyseerrDirectUrl { get; set; }

    /// <summary>
    /// Optional display name override (e.g., "Requests", "Media Requests").
    /// Leave empty to auto-detect "Jellyseerr" or "Seerr" based on server version.
    /// </summary>
    public string? JellyseerrDisplayName { get; set; }

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
    /// Admin-configured default settings for all users.
    /// Users who haven't customized a setting will inherit this value.
    /// Users can override any default in their own Moonfin settings.
    /// </summary>
    public MoonfinSettingsProfile? DefaultUserSettings { get; set; }

    /// <summary>
    /// Gets the effective Jellyseerr URL for server-to-server communication.
    /// </summary>
    public string? GetEffectiveJellyseerrUrl()
    {
        return JellyseerrUrl?.TrimEnd('/');
    }
}
