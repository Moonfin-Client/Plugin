using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Moonfin.Server;

/// <summary>
/// Moonfin Server Plugin for Jellyfin.
/// Provides settings synchronization across Moonfin clients.
/// </summary>
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

        if (Configuration.MigrateLegacyKeys())
        {
            SaveConfiguration();
        }
    }

    /// <inheritdoc />
    public override string Name => "Moonfin";

    /// <inheritdoc />
    public override string Description => "Moonbase is the Moonfin server plugin providing shared infrastructure for every Moonfin client (web, mobile, desktop, and TV): cross-device settings sync with per-device profiles and admin defaults, hosting for the Moonfin Web app at /Moonfin/Web/, a theme editor with custom theme APIs, ratings integrations (MDBList and TMDB), and Seerr proxy with single sign-on. It also powers retro game libraries via EmulatorJS, with keyless libretro box art plus server-hosted cores and save sync.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("8c5d0e91-4f2a-4b6d-9e3f-1a7c8d9e0f2b");

    public new string DataFolderPath => Path.Combine(ApplicationPaths.PluginConfigurationsPath, "Moonfin");

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
