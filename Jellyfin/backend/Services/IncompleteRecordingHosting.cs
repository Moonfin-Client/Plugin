using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moonfin.LiveTv;

namespace Moonfin.Server.Services;

/// <summary>The incomplete recordings settings as stored in the plugin configuration.</summary>
public static class IncompleteRecordingConfig
{
    public static IncompleteRecordingSettings Read()
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        return new IncompleteRecordingSettings
        {
            Enabled = config?.LiveTvDetectIncompleteRecordings ?? true,
            AutoRecordComplete = config?.LiveTvAutoRecordCompleteShowing ?? false,
            ThresholdPercent = IncompleteRecordingRules.ClampThreshold(
                config?.LiveTvIncompleteThresholdPercent ?? IncompleteRecordingRules.DefaultThresholdPercent),
            DeleteIncompleteCopies = config?.LiveTvDeleteIncompleteCopies ?? false,
        };
    }
}

/// <summary>Runs the incomplete recordings watcher for as long as the server does.</summary>
public sealed class IncompleteRecordingHostedService : BackgroundService
{
    private readonly IncompleteRecordingWatcher _watcher;

    public IncompleteRecordingHostedService(IncompleteRecordingWatcher watcher)
    {
        _watcher = watcher;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _watcher.RunAsync(stoppingToken);
}

/// <summary>The recording owners settings and store location as the plugin configuration has them.</summary>
public static class RecordingOwnershipConfig
{
    public static bool Enabled() => MoonfinPlugin.Instance?.Configuration?.LiveTvRecordingOwners ?? true;

    /// <summary>
    /// The owners file's path. Throws until the plugin instance exists, so the file is never
    /// written to <see cref="MoonfinPlugin.ResolveDataFolderPath"/>'s AppData fallback.
    /// </summary>
    public static string StorePath() =>
        MoonfinPlugin.Instance?.DataFolderPath is { } folder
            ? Path.Combine(folder, RecordingOwnershipService.StoreFileName)
            : throw new InvalidOperationException("The Moonfin plugin isn't loaded yet, so the recording owners file has no home");
}

/// <summary>Runs the recording ownership watcher for as long as the server does.</summary>
public sealed class RecordingOwnershipHostedService : BackgroundService
{
    private readonly RecordingOwnershipWatcher _watcher;

    public RecordingOwnershipHostedService(RecordingOwnershipWatcher watcher)
    {
        _watcher = watcher;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _watcher.RunAsync(stoppingToken);
}

/// <summary>Sends the feature's log lines to Jellyfin's logger, keeping their structure.</summary>
internal sealed class JellyfinRecordingLog : IRecordingLog
{
    private readonly ILogger _logger;

    public JellyfinRecordingLog(ILogger logger)
    {
        _logger = logger;
    }

    public void Info(string template, params object?[] args) => _logger.LogInformation(template, args);

    public void Warn(Exception? exception, string template, params object?[] args) => _logger.LogWarning(exception, template, args);

    public void Error(Exception? exception, string template, params object?[] args) => _logger.LogError(exception, template, args);
}

/// <summary>Keeps the feature's state file with the plugin's own safe-write helper.</summary>
internal sealed class JellyfinStateFile : IStateFile
{
    public T? Read<T>(string path, Func<string, T?> parse)
        where T : class => AtomicFile.ReadWithRecovery(path, parse);

    public void Write(string path, string contents) => AtomicFile.WriteAllText(path, contents);
}
