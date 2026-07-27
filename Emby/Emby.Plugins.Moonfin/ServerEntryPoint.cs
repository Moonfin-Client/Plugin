using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin
{
    /// <summary>Initializes plugin-owned service singletons once at server start.</summary>
    public class ServerEntryPoint : IServerEntryPoint
    {
        private readonly ILogManager _logManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly ISessionManager _sessionManager;
        private readonly IServerApplicationHost _appHost;
        private Services.NewMediaNotifier? _newMediaNotifier;

        public ServerEntryPoint(ILogManager logManager, ILibraryManager libraryManager, IUserManager userManager, ISessionManager sessionManager, IServerApplicationHost appHost)
        {
            _logManager = logManager;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _sessionManager = sessionManager;
            _appHost = appHost;
        }

        public void Run()
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return;

            PluginServices.LibraryManager = _libraryManager;
            PluginServices.UserManager = _userManager;
            PluginServices.SessionManager = _sessionManager;

            plugin.MigrateConfiguration();
            plugin.InitializeServices(_logManager, _appHost);

            // New-media notifications: ItemAdded -> coalesced websocket + push fan-out.
            var pushDelivery = plugin.PushDelivery;
            var newMediaStore = plugin.NotificationStore;
            var newMediaSettings = plugin.SettingsService;
            if (pushDelivery != null && newMediaStore != null && newMediaSettings != null)
            {
                _newMediaNotifier = new Services.NewMediaNotifier(
                    _libraryManager,
                    newMediaSettings,
                    newMediaStore,
                    pushDelivery,
                    _logManager.GetLogger("MoonfinNewMedia"));
                _newMediaNotifier.Start();
            }

            // Heal user data files corrupted before atomic writes shipped. Fire and forget so
            // a slow disk can't hold up server startup, and never let it throw into boot.
            var settingsService = plugin.SettingsService;
            var notificationStore = plugin.NotificationStore;
            var seerrService = plugin.SeerrService;
            if (settingsService != null && notificationStore != null && seerrService != null)
            {
                var logger = _logManager.GetLogger("MoonfinDataHeal");
                Task.Run(async () =>
                {
                    try
                    {
                        await Services.MoonfinDataHeal.RunAsync(
                            settingsService,
                            notificationStore,
                            seerrService,
                            logger,
                            progress: null,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.ErrorException("Moonfin data heal failed", ex);
                    }
                });
            }
        }

        public void Dispose()
        {
            _newMediaNotifier?.Dispose();
            _newMediaNotifier = null;
        }
    }

    /// <summary>Holds server-level services that stateless API request handlers access statically.</summary>
    internal static class PluginServices
    {
        public static ILibraryManager? LibraryManager { get; set; }
        public static IUserManager? UserManager { get; set; }
        public static ISessionManager? SessionManager { get; set; }
    }
}
