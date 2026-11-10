using System;
using System.IO;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Keeps a spare copy of the plugin's XML configuration and notices when the live one has been reset.
    ///
    /// Emby replaces a configuration file it can't read with a fresh default one and logs nothing, so
    /// from the admin's side every Moonbase server setting simply vanishes after a restart. A disk
    /// error, a half-written file or a config edited by hand all end up there.
    /// </summary>
    public sealed class ConfigBackupService
    {
        private readonly string _livePath;
        private readonly ILogger _logger;

        public ConfigBackupService(string livePath, string backupPath, ILogger logger)
        {
            _livePath = livePath;
            BackupPath = backupPath;
            _logger = logger;
        }

        /// <summary>
        /// Whether the configuration is back to defaults while a backup with real settings in it survives.
        /// </summary>
        public bool ConfigurationLooksReset { get; private set; }

        public string BackupPath { get; }

        /// <summary>
        /// Refreshes the backup from a configuration that still has settings in it, or flags a reset.
        /// Never throws, since a spare copy of a config file isn't worth failing startup over.
        /// </summary>
        public void Evaluate(PluginConfiguration? live)
        {
            try
            {
                if (!File.Exists(_livePath))
                {
                    return;
                }

                if (!IsAllDefaults(live))
                {
                    // Only ever refresh the backup from a configuration that still has settings in it.
                    // Copying unconditionally would let the first restart after a reset replace the last
                    // good copy with the defaults that just overwrote it.
                    Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
                    File.Copy(_livePath, BackupPath, overwrite: true);
                    ConfigurationLooksReset = false;
                    return;
                }

                if (!File.Exists(BackupPath))
                {
                    return;
                }

                ConfigurationLooksReset = true;
                _logger.Warn(
                    "The Moonfin plugin configuration is back to defaults but a backup with real settings survives at "
                    + BackupPath + ". Restore it from the Moonbase config page, or copy it over " + _livePath
                    + " and restart. Per-user settings live in their own files and are unaffected.");
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Moonfin couldn't check or refresh its configuration backup", ex);
            }
        }

        /// <summary>
        /// Copies the backup over the live file, answering false when there is no backup. Emby only
        /// reads the file again on a restart.
        /// </summary>
        public bool Restore()
        {
            if (!File.Exists(BackupPath))
            {
                return false;
            }

            try
            {
                File.Copy(BackupPath, _livePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Failed to restore the Moonfin plugin configuration from backup", ex);
                throw;
            }

            _logger.Info("Restored the Moonfin plugin configuration from " + BackupPath);
            ConfigurationLooksReset = false;
            return true;
        }

        /// <summary>
        /// Whether the configuration holds nothing but the values a brand new one has. The webhook
        /// secret is skipped, since the plugin mints one on first load, so a fresh install and a
        /// just-reset one both carry a random value there.
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
}
