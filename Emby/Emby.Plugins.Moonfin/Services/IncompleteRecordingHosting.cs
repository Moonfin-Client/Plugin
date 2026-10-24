using System;
using MediaBrowser.Model.Logging;
using Moonfin.LiveTv;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>The incomplete recordings settings as stored in the plugin configuration.</summary>
    public static class IncompleteRecordingConfig
    {
        public static IncompleteRecordingSettings Read()
        {
            var config = Plugin.Instance?.Configuration;
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

    /// <summary>Sends the feature's log lines to Emby's logger as plain text.</summary>
    internal sealed class EmbyRecordingLog : IRecordingLog
    {
        private readonly ILogger _logger;

        public EmbyRecordingLog(ILogger logger)
        {
            _logger = logger;
        }

        // Passed as an argument so braces in the text can't be read as format items.
        public void Info(string template, params object?[] args) =>
            _logger.Info("{0}", RecordingLogText.Format(template, args));

        public void Warn(Exception? exception, string template, params object?[] args) =>
            _logger.Warn("{0}", WithException(RecordingLogText.Format(template, args), exception));

        public void Error(Exception? exception, string template, params object?[] args) =>
            _logger.Error("{0}", WithException(RecordingLogText.Format(template, args), exception));

        private static string WithException(string message, Exception? exception) =>
            exception == null ? message : message + ": " + exception;
    }

    /// <summary>Keeps the feature's state file with the plugin's own safe-write helper.</summary>
    internal sealed class EmbyStateFile : IStateFile
    {
        public T? Read<T>(string path, Func<string, T?> parse)
            where T : class => AtomicFile.ReadWithRecovery(path, parse);

        public void Write(string path, string contents) => AtomicFile.WriteAllText(path, contents);
    }
}
