using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using Moonfin.LiveTv;

namespace Emby.Plugins.Moonfin
{
    /// <summary>Initializes plugin-owned service singletons once at server start.</summary>
    public class ServerEntryPoint : IServerEntryPoint
    {
        private readonly ILogManager _logManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userDataManager;
        private readonly ISessionManager _sessionManager;
        private readonly IServerApplicationHost _appHost;
        private readonly ILiveTvManager _liveTvManager;
        private readonly ITaskManager _taskManager;
        private Services.NewMediaNotifier? _newMediaNotifier;
        private Services.EmbyLiveTvRecordingHost? _recordingHost;
        private IncompleteRecordingWatcher? _recordingWatcher;
        private CancellationTokenSource? _recordingStop;

        public ServerEntryPoint(ILogManager logManager, ILibraryManager libraryManager, IUserManager userManager, IUserDataManager userDataManager, ISessionManager sessionManager, IServerApplicationHost appHost, ILiveTvManager liveTvManager, ITaskManager taskManager)
        {
            _logManager = logManager;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _userDataManager = userDataManager;
            _sessionManager = sessionManager;
            _appHost = appHost;
            _liveTvManager = liveTvManager;
            _taskManager = taskManager;
        }

        public void Run()
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return;

            PluginServices.LibraryManager = _libraryManager;
            PluginServices.UserManager = _userManager;
            PluginServices.UserDataManager = _userDataManager;
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

            StartIncompleteRecordings(plugin);

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

        private void StartIncompleteRecordings(Plugin plugin)
        {
            var logger = _logManager.GetLogger("MoonfinLiveTvRecordings");
            _recordingHost = new Services.EmbyLiveTvRecordingHost(_liveTvManager, _libraryManager, _userManager, _taskManager, logger);
            var log = new Services.EmbyRecordingLog(logger);
            var service = new IncompleteRecordingService(
                _recordingHost,
                log,
                new Services.EmbyStateFile(),
                () => DateTime.UtcNow,
                System.IO.Path.Combine(plugin.DataFolderPath, IncompleteRecordingService.StoreFileName),
                Services.IncompleteRecordingConfig.Read);
            PluginServices.IncompleteRecordings = service;

            _recordingWatcher = new IncompleteRecordingWatcher(service, _recordingHost, log);
            _recordingStop = new CancellationTokenSource();
            var watcher = _recordingWatcher;
            var token = _recordingStop.Token;
            _ = Task.Run(() => watcher.RunAsync(token));
        }

        public void Dispose()
        {
            _newMediaNotifier?.Dispose();
            _newMediaNotifier = null;

            _recordingStop?.Cancel();
            _recordingHost?.Dispose();
            _recordingWatcher?.Dispose();
            _recordingStop?.Dispose();
            PluginServices.IncompleteRecordings = null;
        }
    }

    /// <summary>Holds server-level services that stateless API request handlers access statically.</summary>
    internal static class PluginServices
    {
        public static ILibraryManager? LibraryManager { get; set; }
        public static IUserManager? UserManager { get; set; }
        public static IUserDataManager? UserDataManager { get; set; }
        public static ISessionManager? SessionManager { get; set; }
        public static IncompleteRecordingService? IncompleteRecordings { get; set; }
    }
}
