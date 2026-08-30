using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Keeps a spare copy of Moonfin.Server.xml, and notices when the live one has been reset.
///
/// Jellyfin wraps the config read in a bare catch. Any failure to parse the file produces a fresh
/// default configuration which it then writes straight back over the original, logging nothing, so
/// from the admin's side every Moonbase server setting simply vanishes after a restart. Sanitizing
/// what the plugin writes stops it creating such a file, but a disk error, a half-written file or
/// a config edited by hand all land in the same place, so the recovery path earns its keep.
/// </summary>
public sealed class ConfigBackupService : IHostedService
{
    private const string BackupFolderName = "config-backup";
    private const string BackupFileName = "Moonfin.Server.xml";

    private readonly ILogger<ConfigBackupService> _logger;
    private readonly IApplicationPaths _paths;

    public ConfigBackupService(ILogger<ConfigBackupService> logger, IApplicationPaths paths)
    {
        _logger = logger;
        _paths = paths;
    }

    /// <summary>
    /// Gets a value indicating whether the configuration is back to defaults while a backup with
    /// real settings in it survives.
    /// </summary>
    public bool ConfigurationLooksReset { get; private set; }

    public string? BackupPath { get; private set; }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Never throws. A hosted service that fails on startup takes the whole host down with it,
        // and a spare copy of a config file isn't worth that.
        try
        {
            Evaluate();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Moonfin could not check or refresh its configuration backup");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Copies the backup over the live file, answering false when there is no backup. The caller
    /// requires the admin, and Jellyfin only re-reads the file on a restart.
    /// </summary>
    public bool Restore()
    {
        var backupPath = ResolveBackupPath();
        var livePath = ResolveLivePath();

        if (backupPath == null || livePath == null || !File.Exists(backupPath))
        {
            return false;
        }

        File.Copy(backupPath, livePath, overwrite: true);
        _logger.LogInformation("Restored the Moonfin plugin configuration from {BackupPath}", backupPath);
        ConfigurationLooksReset = false;
        return true;
    }

    private void Evaluate()
    {
        var backupPath = ResolveBackupPath();
        var livePath = ResolveLivePath();
        BackupPath = backupPath;

        if (backupPath == null || livePath == null || !File.Exists(livePath))
        {
            return;
        }

        var liveIsDefault = IsAllDefaults(MoonfinPlugin.Instance?.Configuration);

        if (!liveIsDefault)
        {
            // Only ever refresh the backup from a configuration that still has settings in it.
            // Copying over it unconditionally would mean the first restart after a reset quietly
            // replaced the last good copy with the defaults that just overwrote it.
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Copy(livePath, backupPath, overwrite: true);
            ConfigurationLooksReset = false;
            return;
        }

        if (!File.Exists(backupPath))
        {
            // A fresh install: nothing to compare against and nothing worth keeping.
            return;
        }

        ConfigurationLooksReset = true;
        _logger.LogWarning(
            "The Moonfin plugin configuration is back to defaults but a backup with real settings "
            + "survives at {BackupPath}. Restore it from the Moonbase config page, or copy it over "
            + "{LivePath} and restart. Per-user settings live in their own files and are unaffected.",
            backupPath,
            livePath);
    }

    private string? ResolveBackupPath()
    {
        var dataFolder = MoonfinPlugin.ResolveDataFolderPath();
        return string.IsNullOrEmpty(dataFolder)
            ? null
            : Path.Combine(dataFolder, BackupFolderName, BackupFileName);
    }

    private string? ResolveLivePath()
    {
        var configurations = _paths.PluginConfigurationsPath;
        return string.IsNullOrEmpty(configurations)
            ? null
            : Path.Combine(configurations, BackupFileName);
    }

    /// <summary>
    /// Answers whether the configuration still holds nothing but the values a brand new one has.
    /// The webhook secret is skipped, since the plugin mints one on first load and a fresh install
    /// and a just-reset one both carry a random value there.
    /// </summary>
    internal static bool IsAllDefaults(PluginConfiguration? config)
    {
        if (config == null)
        {
            return false;
        }

        var pristine = new PluginConfiguration();

        foreach (var property in typeof(PluginConfiguration).GetProperties())
        {
            if (!property.CanRead || !property.CanWrite)
            {
                continue;
            }

            if (property.Name == nameof(PluginConfiguration.SeerrWebhookSecret))
            {
                continue;
            }

            var current = property.GetValue(config);
            var original = property.GetValue(pristine);

            if (current is System.Collections.ICollection currentList
                && original is System.Collections.ICollection originalList)
            {
                if (currentList.Count != originalList.Count)
                {
                    return false;
                }

                continue;
            }

            if (!Equals(current, original))
            {
                return false;
            }
        }

        return true;
    }
}
