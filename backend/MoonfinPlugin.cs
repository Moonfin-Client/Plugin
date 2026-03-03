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
    }

    /// <inheritdoc />
    public override string Name => "Moonfin";

    /// <inheritdoc />
    public override string Description => "Moonfin brings a modern TV-style UI to Jellyfin web. Features include: custom navbar, media bar with featured content, Jellyseerr integration, and cross-device settings synchronization. Works with Android TV, Roku, Tizen, webOS, and Web clients.";

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
