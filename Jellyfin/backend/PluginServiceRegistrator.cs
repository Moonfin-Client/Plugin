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
        serviceCollection.AddSingleton<PushDeliveryService>();
        serviceCollection.AddSingleton<SeerrWebhookService>();
        serviceCollection.AddSingleton<SeerrProvisioningService>();
        serviceCollection.AddSingleton<MdbListCacheService>();
        serviceCollection.AddSingleton<MdbListListsCacheService>();
        serviceCollection.AddSingleton<ImdbListsCacheService>();
        serviceCollection.AddSingleton<StudioLogoCacheService>();
        serviceCollection.AddSingleton<StudioLogoFetchService>();
        serviceCollection.AddSingleton<CustomRowCacheService>();
        serviceCollection.AddSingleton<CollectionOrderService>();
        serviceCollection.AddSingleton<GamesService>();
        serviceCollection.AddSingleton<GameSavesService>();
        // MoonfinPlugin.ResolveDataFolderPath() must be called INSIDE each factory lambda, not
        // hoisted to a shared local: RegisterServices runs while the IServiceCollection is still
        // being built, before the service provider (and before MoonfinPlugin.Instance) exists.
        // Evaluating it here eagerly would permanently bake in the Instance-less fallback path.
        // Deferring the call to first resolution (same as the original inline
        // `MoonfinPlugin.Instance?.DataFolderPath ?? ...` chains this replaces) keeps it lazy.
        serviceCollection.AddSingleton(_ => new ArcadeCompatibilityService(MoonfinPlugin.ResolveDataFolderPath()));
        serviceCollection.AddSingleton(_ => new ArcadeCoreOverrideService(
            Path.Combine(MoonfinPlugin.ResolveDataFolderPath(), "arcade-core-overrides")));
        serviceCollection.AddSingleton(_ => new GameBackendOverrideService(
            Path.Combine(MoonfinPlugin.ResolveDataFolderPath(), "game-backend-overrides")));
        serviceCollection.AddSingleton(_ => new GameArtworkCatalog(
            Path.Combine(MoonfinPlugin.ResolveDataFolderPath(), "game-artwork-catalog")));
        serviceCollection.AddSingleton<PerUserConcurrencyLimiter>();
        serviceCollection.AddSingleton<GameArtworkDeliveryLimiter>();
        serviceCollection.AddSingleton<GameArtworkReconciliationService>();
        serviceCollection.AddSingleton<CoresService>();
        serviceCollection.AddSingleton<RdbService>();
        serviceCollection.AddSingleton<GameThumbService>();
        serviceCollection.AddSingleton<LaunchBoxService>();
        serviceCollection.AddSingleton<UserBookmarksService>();
        serviceCollection.AddHttpClient();

        // Auto-register file transformations on plugin load (no manual task needed)
        serviceCollection.AddHostedService<FileTransformationHostedService>();

        // Auto-register the Seerr webhook shortly after startup when an admin session exists.
        serviceCollection.AddHostedService<SeerrProvisioningStartupService>();
        serviceCollection.AddHostedService<NewMediaNotifier>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<GameArtworkReconciliationService>());
    }
}
