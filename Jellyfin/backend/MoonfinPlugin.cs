using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Moonfin.Server.Services;

namespace Moonfin.Server;

public class MoonfinPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static MoonfinPlugin? Instance { get; private set; }

    public IServiceProvider? ServiceProvider { get; }

    public MoonfinPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : this(applicationPaths, xmlSerializer, null)
    {
    }

    public MoonfinPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, IServiceProvider? serviceProvider)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        ServiceProvider = serviceProvider;

        var changed = Configuration.MigrateLegacyKeys();
        changed |= Configuration.EnsureWebhookSecret();
        if (changed)
        {
            SaveConfiguration();
        }
    }

    /// <inheritdoc />
    public override string Name => "Moonbase";

    /// <inheritdoc />
    public override string Description => "Moonbase is the Moonfin server plugin providing shared infrastructure for every Moonfin client (web, mobile, desktop, and TV): cross-device settings sync with per-device profiles and admin defaults, hosting for the Moonfin Web app at /Moonfin/Web/, a theme editor with custom theme APIs, ratings integrations (MDBList and TMDB), and Seerr proxy with single sign-on. It also powers retro game libraries via EmulatorJS, with keyless libretro box art plus server-hosted cores and save sync.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("8c5d0e91-4f2a-4b6d-9e3f-1a7c8d9e0f2b");

    public new string DataFolderPath => Path.Combine(ApplicationPaths.PluginConfigurationsPath, "Moonfin");

    /// <summary>
    /// Resolves the plugin's data folder for callers that may run before the plugin instance
    /// exists (e.g. a DI factory during host startup): falls back to the same
    /// ApplicationData/Jellyfin/plugins/Moonfin path Jellyfin itself would use, mirroring
    /// <see cref="DataFolderPath"/>.
    /// </summary>
    public static string ResolveDataFolderPath() =>
        Instance?.DataFolderPath
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jellyfin", "plugins", "Moonfin");

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        var previousUrl = Configuration.PublicServerUrl;
        base.UpdateConfiguration(configuration);

        // Re-provision the Seerr webhook when the public URL changes, so the new URL is
        // pushed to Seerr without waiting for a restart. The provisioning guardrail only
        // overwrites our own webhook.
        var newUrl = Configuration.PublicServerUrl;
        if (!string.Equals(previousUrl, newUrl, StringComparison.Ordinal))
        {
            var provisioning = ServiceProvider?.GetService<SeerrProvisioningService>();
            if (provisioning != null)
            {
                _ = provisioning.EnsureWebhookAsync(default);
            }
        }
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Pages.configPage.html",
                EnableInMainMenu = true
            }
        };
    }
}
