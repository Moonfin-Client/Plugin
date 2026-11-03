using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Helpers;

namespace Moonfin.Server.Services;

/// <summary>
/// Builds the smart search index: downloads the model on first run, embeds the library, then
/// refreshes the TMDB catalog behind the Seerr row. Only changed entries are embedded again.
/// </summary>
public class SmartSearchIndexTask : IScheduledTask
{
    private readonly SmartSearchService _service;
    private readonly ILogger<SmartSearchIndexTask> _logger;

    public SmartSearchIndexTask(SmartSearchService service, ILogger<SmartSearchIndexTask> logger)
    {
        _service = service;
        _logger = logger;
    }

    public string Name => "Moonfin Smart Search Index";

    public string Key => "Moonfin.SmartSearch.Index";

    public string Description => "Indexes the library and the most popular TMDB titles so clients can search with plain sentences.";

    public string Category => "Moonfin";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!SmartSearchService.Enabled)
        {
            _logger.LogInformation("Smart search index skipped: disabled in configuration");
            return;
        }

        await _service.RebuildAsync(progress, cancellationToken).ConfigureAwait(false);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return TaskTriggers.Startup();
        yield return TaskTriggers.Daily(TimeSpan.FromHours(3));
    }
}
