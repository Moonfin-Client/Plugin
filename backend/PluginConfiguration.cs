using MediaBrowser.Model.Plugins;

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
    /// Gets the effective Jellyseerr URL for server-to-server communication.
    /// </summary>
    public string? GetEffectiveJellyseerrUrl()
    {
        return JellyseerrUrl?.TrimEnd('/');
    }
}
