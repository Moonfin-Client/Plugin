using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Builds the smart search index: downloads the model on first run, embeds the library, then
    /// refreshes the TMDB catalog behind the Seerr row. Only changed entries are embedded again.
    /// </summary>
    public class SmartSearchIndexTask : IScheduledTask
    {
        private readonly ILogger _logger;

        public SmartSearchIndexTask(ILogManager logManager)
        {
            _logger = logManager.GetLogger("MoonfinSmartSearch");
        }

        public string Name => "Moonfin Smart Search Index";

        public string Key => "Moonfin.SmartSearch.Index";

        public string Description => "Indexes the library and the most popular TMDB titles so clients can search with plain sentences.";

        public string Category => "Moonfin";

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (!SmartSearchIndexService.Enabled)
            {
                _logger.Info("Smart search index skipped: disabled in configuration");
                return;
            }

            var service = Plugin.Instance?.SmartSearch;
            if (service == null)
            {
                _logger.Warn("Smart search index skipped: plugin services not initialized yet");
                return;
            }

            await service.RebuildAsync(progress, cancellationToken).ConfigureAwait(false);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerStartup };
            yield return new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks };
        }
    }
}
