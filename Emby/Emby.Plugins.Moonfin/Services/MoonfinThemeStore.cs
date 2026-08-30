using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Models;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin.Services
{
    public sealed class MoonfinThemeStore
    {
        private const int MaxThemeBytes = 256 * 1024;

        private static readonly HashSet<string> ReservedThemeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "moonfin", "neon_pulse"
        };

        private static readonly SemaphoreSlim ThemeWriteLock = new SemaphoreSlim(1, 1);

        private readonly string _themesPath;
        private readonly MoonfinThemeValidator _validator;
        private readonly ILogger _logger;

        public MoonfinThemeStore(MoonfinThemeValidator validator, ILogger logger)
        {
            _validator = validator;
            _logger = logger;

            var root = Plugin.Instance?.DataFolderPath
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Emby-Server", "programdata", "plugins", "Moonfin");
            _themesPath = Path.Combine(root, "themes");
            EnsureDirectory();
        }

        public async Task<IReadOnlyList<JsonElement>> ListThemesAsync()
        {
            EnsureDirectory();
            var result = new List<JsonElement>();
            var index = GetIndexEntries();

            if (index.Count == 0)
            {
                foreach (var path in Directory.EnumerateFiles(_themesPath, "*.json", SearchOption.TopDirectoryOnly)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var theme = await ReadThemeFileAsync(path).ConfigureAwait(false);
                    if (theme.HasValue) result.Add(theme.Value);
                }
                return result;
            }

            foreach (var item in index.OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var path = ResolveThemePath(item);
                var theme = await ReadThemeFileAsync(path).ConfigureAwait(false);
                if (theme.HasValue) result.Add(theme.Value);
                else _logger.Warn("Theme file missing for indexed theme " + item.Id + ": " + path);
            }
            return result;
        }

        public async Task<JsonElement?> GetThemeAsync(string themeId)
        {
            EnsureDirectory();
            var normalizedId = NormalizeThemeId(themeId);
            if (string.IsNullOrEmpty(normalizedId)) return null;

            var indexEntry = GetIndexEntries().FirstOrDefault(e => string.Equals(e.Id, normalizedId, StringComparison.OrdinalIgnoreCase));
            var path = indexEntry != null ? ResolveThemePath(indexEntry) : Path.Combine(_themesPath, normalizedId + ".json");
            return await ReadThemeFileAsync(path).ConfigureAwait(false);
        }

        public IReadOnlyList<UploadedThemeEntry> GetThemeIndex()
        {
            return GetIndexEntries()
                .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Id, StringComparer.Ordinal)
                .Select(CloneEntry)
                .ToList();
        }

        public async Task<(UploadedThemeEntry? Entry, IReadOnlyList<string> Errors)> SaveThemeAsync(JsonElement themePayload, Guid? uploadedByUserId)
        {
            var validation = _validator.Validate(themePayload);
            if (!validation.IsValid) return (null, validation.Errors);
            if (ReservedThemeIds.Contains(validation.ThemeId)) return (null, new[] { "id conflicts with a protected built-in theme." });

            var rawJson = themePayload.GetRawText();
            var utf8Bytes = Encoding.UTF8.GetBytes(rawJson);
            if (utf8Bytes.Length > MaxThemeBytes)
                return (null, new[] { $"Theme JSON exceeds max size ({MaxThemeBytes} bytes)." });

            var checksum = BitConverter.ToString(SHA256.Create().ComputeHash(utf8Bytes)).Replace("-", "").ToLowerInvariant();
            var fileName = validation.ThemeId + ".json";
            var targetPath = Path.Combine(_themesPath, fileName);
            var uploadTime = DateTimeOffset.UtcNow;

            EnsureDirectory();
            await ThemeWriteLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await Task.Run(() => File.WriteAllText(targetPath, rawJson)).ConfigureAwait(false);

                var config = Plugin.Instance?.Configuration;
                if (config == null)
                {
                    return (new UploadedThemeEntry
                    {
                        Id = validation.ThemeId, DisplayName = XmlText.Sanitize(validation.DisplayName),
                        FileName = fileName, SizeBytes = utf8Bytes.Length,
                        UploadedAtUtc = uploadTime, UploadedByUserId = uploadedByUserId?.ToString(),
                        ChecksumSha256 = checksum
                    }, Array.Empty<string>());
                }

                if (config.UploadedThemes == null) config.UploadedThemes = new List<UploadedThemeEntry>();

                var existing = config.UploadedThemes.FirstOrDefault(e =>
                    string.Equals(e.Id, validation.ThemeId, StringComparison.OrdinalIgnoreCase));
                if (existing == null) { existing = new UploadedThemeEntry(); config.UploadedThemes.Add(existing); }

                existing.Id = validation.ThemeId;
                existing.DisplayName = XmlText.Sanitize(validation.DisplayName);
                existing.FileName = fileName;
                existing.SizeBytes = utf8Bytes.Length;
                existing.UploadedAtUtc = uploadTime;
                existing.UploadedByUserId = uploadedByUserId?.ToString();
                existing.ChecksumSha256 = checksum;

                PersistPluginConfiguration(config);
                return (CloneEntry(existing), Array.Empty<string>());
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Failed to save theme " + validation.ThemeId, ex);
                return (null, new[] { "Failed to save theme file." });
            }
            finally
            {
                ThemeWriteLock.Release();
            }
        }

        public async Task<bool> DeleteThemeAsync(string themeId)
        {
            var normalizedId = NormalizeThemeId(themeId);
            if (string.IsNullOrEmpty(normalizedId)) return false;

            EnsureDirectory();
            await ThemeWriteLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var config = Plugin.Instance?.Configuration;
                UploadedThemeEntry? match = null;
                if (config?.UploadedThemes != null)
                    match = config.UploadedThemes.FirstOrDefault(e => string.Equals(e.Id, normalizedId, StringComparison.OrdinalIgnoreCase));

                var path = match != null ? ResolveThemePath(match) : Path.Combine(_themesPath, normalizedId + ".json");
                var removedAny = false;

                if (File.Exists(path)) { File.Delete(path); removedAny = true; }

                if (match != null && config?.UploadedThemes != null)
                {
                    removedAny = config.UploadedThemes.Remove(match) || removedAny;
                    PersistPluginConfiguration(config);
                }

                return removedAny;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Failed to delete theme " + normalizedId, ex);
                return false;
            }
            finally
            {
                ThemeWriteLock.Release();
            }
        }

        private async Task<JsonElement?> ReadThemeFileAsync(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var json = await Task.Run(() => File.ReadAllText(path)).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.Clone();
            }
            catch (Exception ex)
            {
                _logger.Warn("Failed to parse theme JSON at " + path + ": " + ex.Message);
                return null;
            }
        }

        private static UploadedThemeEntry CloneEntry(UploadedThemeEntry source) =>
            new UploadedThemeEntry
            {
                Id = source.Id, DisplayName = source.DisplayName, FileName = source.FileName,
                SizeBytes = source.SizeBytes, UploadedAtUtc = source.UploadedAtUtc,
                UploadedByUserId = source.UploadedByUserId, ChecksumSha256 = source.ChecksumSha256
            };

        private static void PersistPluginConfiguration(PluginConfiguration config)
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return;

            var pluginType = plugin.GetType();
            var updateConfig = pluginType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => string.Equals(m.Name, "UpdateConfiguration", StringComparison.Ordinal) && m.GetParameters().Length == 1);
            if (updateConfig != null) { updateConfig.Invoke(plugin, new object?[] { config }); return; }

            var saveConfig = pluginType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => string.Equals(m.Name, "SaveConfiguration", StringComparison.Ordinal) && m.GetParameters().Length == 0);
            saveConfig?.Invoke(plugin, null);
        }

        private string ResolveThemePath(UploadedThemeEntry entry)
        {
            var fileName = string.IsNullOrWhiteSpace(entry.FileName) ? entry.Id + ".json" : entry.FileName;
            return Path.Combine(_themesPath, fileName);
        }

        private List<UploadedThemeEntry> GetIndexEntries() =>
            Plugin.Instance?.Configuration?.UploadedThemes ?? new List<UploadedThemeEntry>();

        private void EnsureDirectory()
        {
            if (!Directory.Exists(_themesPath)) Directory.CreateDirectory(_themesPath);
        }

        private static string NormalizeThemeId(string raw) => (raw ?? string.Empty).Trim().ToLowerInvariant();
    }
}
