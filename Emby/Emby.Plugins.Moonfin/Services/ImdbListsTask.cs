using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Fetches the fixed set of IMDb charts (Top 250, Most Popular, etc.) and caches them
    /// server-side so clients can show them as home rows and custom rows can resolve the imdb
    /// source without a per-user key. Clones the structure of <see cref="MdbListListsTask"/>.
    /// </summary>
    public class ImdbListsTask : IScheduledTask
    {
        public string Name => "Moonfin IMDb Lists Sync";
        public string Key => "Moonfin.Imdb.ListsSync";
        public string Description => "Fetches and caches IMDb charts (Top 250, Most Popular, etc.) so clients can show them as home rows.";
        public string Category => "Moonfin";

        private readonly ImdbChartFetcher _fetcher;
        private readonly ILogger _logger;

        // Resolved lazily: Emby constructs IScheduledTask instances before ServerEntryPoint.Run()
        // initializes the service singletons, so this cannot be read in the ctor.
        private ImdbListsCacheService CacheService => Plugin.Instance?.ImdbListsCache
            ?? throw new InvalidOperationException("ImdbListsCacheService not initialized");

        public ImdbListsTask(ILogManager logManager)
        {
            _logger = logManager.GetLogger("MoonfinImdbLists");
            _fetcher = new ImdbChartFetcher(_logger);
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            PluginConfiguration? config = null;
            try { config = Plugin.Instance?.Configuration; }
            catch { /* configuration not ready yet (e.g. startup trigger before init) */ }

            if (config != null && !config.ImdbListsEnabled)
            {
                _logger.Info("IMDb lists sync skipped: disabled in plugin configuration", 0);
                return;
            }

            // The startup trigger can fire before ServerEntryPoint.Run() initializes the
            // service singletons, so skip the run instead of throwing.
            try { _ = CacheService; }
            catch (InvalidOperationException)
            {
                _logger.Warn("IMDb lists sync skipped: plugin services not initialized yet");
                return;
            }

            progress.Report(0);
            var keys = ImdbChartFetcher.ChartMap.Keys.ToList();
            var processed = 0;

            foreach (var key in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.Info("Fetching IMDb chart: " + key, 0);

                try
                {
                    var items = await _fetcher.FetchChartAsync(key, cancellationToken).ConfigureAwait(false);
                    if (items != null && items.Count > 0)
                    {
                        CacheService.SetItems(key, items);
                        _logger.Info("Cached " + items.Count + " items for IMDb chart: " + key, 0);
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _logger.ErrorException("Failed to fetch IMDb chart " + key, ex);
                }

                processed++;
                progress.Report((double)processed / keys.Count * 100);
            }

            await CacheService.FlushAsync().ConfigureAwait(false);
            _logger.Info("IMDb lists sync complete", 0);
            progress.Report(100);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerStartup };
            yield return new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks };
        }
    }
}
