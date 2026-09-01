using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Models;

namespace Moonfin.Server.Services;

/// <summary>
/// Scheduled task that proactively fetches and warms custom home rows (such as Letterboxd,
/// TMDB, and MDBList custom user lists) configured across user profiles.
/// </summary>
public class CustomRowsSyncTask : IScheduledTask
{
    public string Name => "Moonfin Custom Lists Sync";
    public string Key => "Moonfin.CustomRows.Sync";
    public string Description => "Fetches and caches custom home rows (such as Letterboxd, TMDB, and MDBList custom lists) configured across user profiles.";
    public string Category => "Moonfin";

    private readonly MoonfinSettingsService _settingsService;
    private readonly CustomRowCacheService _cacheService;
    private readonly CustomRowFetchService _fetchService;
    private readonly ILogger<CustomRowsSyncTask> _logger;

    public CustomRowsSyncTask(
        MoonfinSettingsService settingsService,
        CustomRowCacheService cacheService,
        CustomRowFetchService fetchService,
        ILogger<CustomRowsSyncTask> logger)
    {
        _settingsService = settingsService;
        _cacheService = cacheService;
        _fetchService = fetchService;
        _logger = logger;
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        _logger.LogInformation("Starting scheduled sync of Moonfin custom home rows...");

        var allUsersSettings = await _settingsService.GetAllUserSettingsAsync();
        if (allUsersSettings.Count == 0)
        {
            _logger.LogInformation("No user settings found to scan for custom home rows.");
            progress.Report(100);
            return;
        }

        // Collect distinct custom row configurations across all users and profiles
        var distinctRows = new Dictionary<string, (string Source, string Type, Dictionary<string, string> Params, Guid UserId)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (userId, userSettings) in allUsersSettings)
        {
            var profiles = new[] { userSettings.Global, userSettings.Desktop, userSettings.Mobile, userSettings.Tv };
            foreach (var profile in profiles)
            {
                if (profile?.HomeSections == null) continue;

                foreach (var section in profile.HomeSections)
                {
                    if (section.Enabled != true) continue;
                    if (!string.Equals(section.PluginSource, "custom", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(section.Kind, "custom", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(section.PluginAdditionalData)) continue;

                    try
                    {
                        using var doc = JsonDocument.Parse(section.PluginAdditionalData);
                        var root = doc.RootElement;
                        var source = root.TryGetProperty("source", out var sProp) ? sProp.GetString() : null;
                        var type = root.TryGetProperty("type", out var tProp) ? tProp.GetString() : null;
                        var paramsDict = new Dictionary<string, string>();

                        if (root.TryGetProperty("params", out var pProp) && pProp.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in pProp.EnumerateObject())
                            {
                                if (prop.Value.ValueKind == JsonValueKind.String)
                                {
                                    paramsDict[prop.Name] = prop.Value.GetString() ?? string.Empty;
                                }
                                else
                                {
                                    paramsDict[prop.Name] = prop.Value.ToString();
                                }
                            }
                        }

                        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(type)) continue;

                        var cacheKey = CustomRowFetchService.ComputeCacheKey(source, type, paramsDict);
                        if (!distinctRows.ContainsKey(cacheKey))
                        {
                            distinctRows[cacheKey] = (source, type, paramsDict, userId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to parse custom home row additionalData for section {Section}", section.PluginSection);
                    }
                }
            }
        }

        if (distinctRows.Count == 0)
        {
            _logger.LogInformation("No enabled custom home rows discovered across user profiles.");
            progress.Report(100);
            return;
        }

        _logger.LogInformation("Found {Count} distinct enabled custom home row(s) to refresh.", distinctRows.Count);

        var processed = 0;
        var total = distinctRows.Count;

        foreach (var (cacheKey, (source, type, parsedParams, userId)) in distinctRows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _logger.LogInformation("Refreshing custom row [{CacheKey}] ({Source}/{Type})...", cacheKey, source, type);
                var items = await _fetchService.FetchCustomRowAsync(source, type, parsedParams, userId, cancellationToken);
                if (items.Count > 0)
                {
                    _cacheService.Set(cacheKey, items);
                    _logger.LogInformation("Successfully cached {Count} items for custom row [{CacheKey}].", items.Count, cacheKey);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh custom row [{CacheKey}] ({Source}/{Type})", cacheKey, source, type);
            }

            processed++;
            progress.Report((double)processed / total * 100.0);

            // Brief polite pause between external API requests
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        _cacheService.PruneOlderThan(TimeSpan.FromDays(7));
        await _cacheService.FlushAsync().ConfigureAwait(false);
        _logger.LogInformation("Completed scheduled sync of Moonfin custom home rows ({Processed}/{Total} processed).", processed, total);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfo.TriggerStartup
        };

        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfo.TriggerDaily,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
        };
    }
}
