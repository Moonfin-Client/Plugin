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
        serviceCollection.AddSingleton<MoonfinThemeValidator>();
        serviceCollection.AddSingleton<MoonfinThemeStore>();
        serviceCollection.AddSingleton<SeerrSessionService>();
        serviceCollection.AddSingleton<NotificationStore>();
        serviceCollection.AddSingleton<FcmSender>();
        serviceCollection.AddSingleton<RelaySender>();
        serviceCollection.AddSingleton<SeerrWebhookService>();
        serviceCollection.AddSingleton<SeerrProvisioningService>();
        serviceCollection.AddSingleton<MdbListCacheService>();
        serviceCollection.AddSingleton<MdbListListsCacheService>();
        serviceCollection.AddSingleton<ImdbListsCacheService>();
        serviceCollection.AddSingleton<StudioLogoCacheService>();
        serviceCollection.AddSingleton<StudioLogoFetchService>();
        serviceCollection.AddSingleton<CustomRowCacheService>();
        serviceCollection.AddSingleton<GamesService>();
        serviceCollection.AddSingleton<GameSavesService>();
        serviceCollection.AddSingleton<CoresService>();
        serviceCollection.AddSingleton<RdbService>();
        serviceCollection.AddSingleton<LaunchBoxService>();
        serviceCollection.AddHttpClient();

        // Auto-register file transformations on plugin load (no manual task needed)
        serviceCollection.AddHostedService<FileTransformationHostedService>();

        // Auto-register the Seerr webhook shortly after startup when an admin session exists.
        serviceCollection.AddHostedService<SeerrProvisioningStartupService>();
    }
}
