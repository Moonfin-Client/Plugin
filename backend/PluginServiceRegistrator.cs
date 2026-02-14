using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Moonfin.Server.Services;

namespace Moonfin.Server;

/// <summary>
/// Registers Moonfin services with the Jellyfin dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<MoonfinSettingsService>();
        serviceCollection.AddSingleton<JellyseerrSessionService>();
        serviceCollection.AddSingleton<MdbListCacheService>();
        serviceCollection.AddHttpClient();

        // Auto-register file transformations on plugin load (no manual task needed)
        serviceCollection.AddHostedService<FileTransformationHostedService>();
    }
}
