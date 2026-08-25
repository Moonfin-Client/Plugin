using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Emby.Plugins.Moonfin
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        private readonly ILogger _logger;

        public static Plugin? Instance { get; private set; }

        public MoonfinSettingsService? SettingsService { get; private set; }
        public MoonfinThemeValidator? ThemeValidator { get; private set; }
        public MoonfinThemeStore? ThemeStore { get; private set; }
        public SeerrSessionService? SeerrService { get; private set; }
        public MdbListCacheService? MdbListCache { get; private set; }
        public MdbListListsCacheService? MdbListListsCache { get; private set; }
        public ImdbListsCacheService? ImdbListsCache { get; private set; }
        public ImdbChartFetcher? ImdbChartFetcher { get; private set; }
        public CustomRowCacheService? CustomRowCache { get; private set; }
        public StudioLogoCacheService? StudioLogoCache { get; private set; }
        public StudioLogoFetchService? StudioLogoFetch { get; private set; }
        public GameThumbService? GameThumbs { get; private set; }
        public NotificationStore? NotificationStore { get; private set; }
        public RelaySender? RelaySender { get; private set; }
        public FcmSender? FcmSender { get; private set; }
        public PushDeliveryService? PushDelivery { get; private set; }
        public SeerrWebhookService? SeerrWebhook { get; private set; }
        public SeerrProvisioningService? SeerrProvisioning { get; private set; }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogManager logManager)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            _logger = logManager.GetLogger("Moonfin");
            _logger.Info("Moonfin plugin loaded", 0);
        }

        /// <summary>
        /// Migrates legacy configuration keys. Must run after construction (not in the ctor):
        /// Emby sets the plugin's configuration attributes after instantiation, so reading
        /// Configuration during the constructor throws inside BasePlugin.ConfigurationFilePath.
        /// </summary>
        internal void MigrateConfiguration()
        {
            if (Configuration.MigrateLegacyKeys())
            {
                SaveConfiguration();
            }
        }

        /// <summary>
        /// Emby's config-save hook: the config page persists changes through this. Re-run webhook
        /// provisioning when PublicServerUrl changes so a newly set public URL is pushed to Seerr
        /// without a restart. Best-effort and never throws into the save path.
        /// </summary>
        public override void UpdateConfiguration(MediaBrowser.Model.Plugins.BasePluginConfiguration configuration)
        {
            var oldUrl = Configuration?.PublicServerUrl;
            base.UpdateConfiguration(configuration);
            var newUrl = Configuration?.PublicServerUrl;

            if (!string.Equals(oldUrl, newUrl, StringComparison.Ordinal))
            {
                var provisioning = SeerrProvisioning;
                if (provisioning != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await provisioning.EnsureWebhookAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug("Moonfin webhook re-provisioning after config save failed: " + ex.Message);
                        }
                    });
                }
            }
        }

        public override string Name => "Moonbase";

        public override string Description => "Moonfin brings a modern TV-style UI to Emby. Features include: custom navbar, media bar with featured content, Seerr integration, and cross-device settings synchronization.";

        public override Guid Id => new Guid("9a1b2c3d-4e5f-6789-abcd-ef0123456789");

        // Provide a stable config filename so Configuration is readable regardless of when Emby
        // sets the plugin attributes. Without this the base getter does Path.Combine(dir, null).
        public override string ConfigurationFileName => "Emby.Plugins.Moonfin.xml";

        public new string DataFolderPath => System.IO.Path.Combine(ApplicationPaths.PluginConfigurationsPath, "Moonfin");

        public string PluginsPath => ApplicationPaths.PluginsPath;

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".assets.logo.png");
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            var ns = GetType().Namespace;
            return new[]
            {
                new PluginPageInfo
                {
                    Name = Name,
                    EmbeddedResourcePath = ns + ".Pages.configPage.html",
                    EnableInMainMenu = true
                },
                // Controller module for the config page, referenced via data-controller="__plugin/moonfinjs".
                new PluginPageInfo
                {
                    Name = "moonfinjs",
                    EmbeddedResourcePath = ns + ".Pages.moonfin.js"
                }
            };
        }

        internal void InitializeServices(ILogManager logManager, IServerApplicationHost appHost)
        {
            var settingsLogger = logManager.GetLogger("MoonfinSettings");
            var themeLogger = logManager.GetLogger("MoonfinThemes");
            var seerrLogger = logManager.GetLogger("MoonfinSeerr");
            var mdbLogger = logManager.GetLogger("MoonfinMdbList");
            var notifyLogger = logManager.GetLogger("MoonfinNotifications");

            ThemeValidator = new MoonfinThemeValidator();
            SettingsService = new MoonfinSettingsService(settingsLogger);
            ThemeStore = new MoonfinThemeStore(ThemeValidator, themeLogger);
            SeerrService = new SeerrSessionService(seerrLogger);
            MdbListCache = new MdbListCacheService(mdbLogger);
            MdbListListsCache = new MdbListListsCacheService(mdbLogger);
            ImdbListsCache = new ImdbListsCacheService(mdbLogger);
            ImdbChartFetcher = new ImdbChartFetcher(mdbLogger);
            CustomRowCache = new CustomRowCacheService(mdbLogger);
            StudioLogoCache = new StudioLogoCacheService(mdbLogger);
            StudioLogoFetch = new StudioLogoFetchService(StudioLogoCache, mdbLogger);
            GameThumbs = new GameThumbService(mdbLogger);

            NotificationStore = new NotificationStore(notifyLogger);
            RelaySender = new RelaySender(notifyLogger);
            FcmSender = new FcmSender(notifyLogger);
            PushDelivery = new PushDeliveryService(NotificationStore, RelaySender, FcmSender, notifyLogger);
            SeerrWebhook = new SeerrWebhookService(SeerrService, SettingsService, NotificationStore, PushDelivery, notifyLogger);
            SeerrProvisioning = new SeerrProvisioningService(SeerrService, appHost, notifyLogger);

            // Ensure a webhook secret exists before provisioning tries to publish it.
            if (Configuration.EnsureWebhookSecret())
                SaveConfiguration();

            _logger.Info("Moonfin services initialized", 0);

            // Best-effort webhook auto-provisioning. Never blocks or throws into startup.
            var provisioning = SeerrProvisioning;
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await provisioning.EnsureWebhookAsync(CancellationToken.None).ConfigureAwait(false);

                    // An outcome the admin has to act on is worth an ordinary log line, the
                    // routine ones stay at debug so a working setup keeps quiet.
                    var needsAttention = result.Status == ProvisioningStatus.AdminSessionExpired ||
                        result.Status == ProvisioningStatus.NeedsPublicUrl ||
                        result.Status == ProvisioningStatus.ForeignWebhookPresent ||
                        result.Status == ProvisioningStatus.Failed;

                    var message = "Moonfin webhook provisioning at startup: " + result.Status + ". " + result.Message;
                    if (needsAttention)
                        _logger.Info(message);
                    else
                        _logger.Debug(message);
                }
                catch (Exception ex)
                {
                    _logger.Debug("Moonfin webhook provisioning at startup failed: " + ex.Message);
                }
            });
        }
    }
}
