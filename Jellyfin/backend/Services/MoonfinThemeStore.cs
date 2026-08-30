using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Models;

namespace Moonfin.Server.Services;

/// <summary>
/// Stores uploaded custom theme JSON files and maintains plugin metadata index entries.
/// </summary>
public sealed class MoonfinThemeStore
{
    private const int MaxThemeBytes = 256 * 1024;

    private static readonly HashSet<string> ReservedThemeIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "moonfin",
        "neon_pulse"
    };

    private static readonly SemaphoreSlim ThemeWriteLock = new(1, 1);

    private readonly string _themesPath;
    private readonly MoonfinThemeValidator _validator;
    private readonly ILogger<MoonfinThemeStore> _logger;

    public MoonfinThemeStore(MoonfinThemeValidator validator, ILogger<MoonfinThemeStore> logger)
    {
        _validator = validator;
        _logger = logger;

        var root = MoonfinPlugin.Instance?.DataFolderPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jellyfin", "plugins", "Moonfin");
        _themesPath = Path.Combine(root, "themes");

        EnsureDirectory();
    }

    /// <summary>
    /// List all uploaded theme payloads for client sync consumption.
    /// </summary>
    public async Task<IReadOnlyList<JsonElement>> ListThemesAsync()
    {
        EnsureDirectory();

        var result = new List<JsonElement>();
        var index = GetIndexEntries();

        if (index.Count == 0)
        {
            foreach (var path in Directory.EnumerateFiles(_themesPath, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var theme = await ReadThemeFileAsync(path).ConfigureAwait(false);
                if (theme.HasValue)
                {
                    result.Add(theme.Value);
                }
            }

            return result;
        }

        foreach (var item in index.OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var path = ResolveThemePath(item);
            var theme = await ReadThemeFileAsync(path).ConfigureAwait(false);
            if (theme.HasValue)
            {
                result.Add(theme.Value);
                continue;
            }

            _logger.LogWarning("Theme file missing for indexed theme {ThemeId}: {ThemePath}", item.Id, path);
        }

        return result;
    }

    /// <summary>
    /// Get one uploaded theme payload by ID.
    /// </summary>
    public async Task<JsonElement?> GetThemeAsync(string themeId)
    {
        EnsureDirectory();

        var normalizedId = NormalizeThemeId(themeId);
        if (string.IsNullOrEmpty(normalizedId))
        {
            return null;
        }

        var indexEntry = GetIndexEntries().FirstOrDefault(entry => string.Equals(entry.Id, normalizedId, StringComparison.OrdinalIgnoreCase));
        var path = indexEntry != null ? ResolveThemePath(indexEntry) : Path.Combine(_themesPath, normalizedId + ".json");

        return await ReadThemeFileAsync(path).ConfigureAwait(false);
    }

    /// <summary>
    /// Get uploaded themes metadata index for admin UI.
    /// </summary>
    public IReadOnlyList<UploadedThemeEntry> GetThemeIndex()
    {
        var entries = GetIndexEntries();
        return entries
            .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .Select(CloneEntry)
            .ToList();
    }

    /// <summary>
    /// Save or replace an uploaded theme.
    /// </summary>
    public async Task<(UploadedThemeEntry? Entry, IReadOnlyList<string> Errors)> SaveThemeAsync(JsonElement themePayload, Guid? uploadedByUserId)
    {
        var validation = _validator.Validate(themePayload);
        if (!validation.IsValid)
        {
            return (null, validation.Errors);
        }

        if (ReservedThemeIds.Contains(validation.ThemeId))
        {
            return (null, ["id conflicts with a protected built-in theme."]);
        }

        var rawJson = themePayload.GetRawText();
        var utf8Bytes = Encoding.UTF8.GetBytes(rawJson);
        if (utf8Bytes.Length > MaxThemeBytes)
        {
            return (null, [$"Theme JSON exceeds max size ({MaxThemeBytes} bytes)."]);
        }

        var checksum = Convert.ToHexString(SHA256.HashData(utf8Bytes)).ToLowerInvariant();
        var fileName = validation.ThemeId + ".json";
        var targetPath = Path.Combine(_themesPath, fileName);
        var uploadTime = DateTimeOffset.UtcNow;

        EnsureDirectory();

        await ThemeWriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await File.WriteAllTextAsync(targetPath, rawJson).ConfigureAwait(false);

            var config = MoonfinPlugin.Instance?.Configuration;
            if (config == null)
            {
                return (new UploadedThemeEntry
                {
                    Id = validation.ThemeId,
                    DisplayName = XmlText.Sanitize(validation.DisplayName),
                    FileName = fileName,
                    SizeBytes = utf8Bytes.Length,
                    UploadedAtUtc = uploadTime,
                    UploadedByUserId = uploadedByUserId?.ToString(),
                    ChecksumSha256 = checksum
                }, Array.Empty<string>());
            }

            config.UploadedThemes ??= new List<UploadedThemeEntry>();

            var existing = config.UploadedThemes.FirstOrDefault(entry =>
                string.Equals(entry.Id, validation.ThemeId, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                existing = new UploadedThemeEntry();
                config.UploadedThemes.Add(existing);
            }

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
            _logger.LogError(ex, "Failed to save theme {ThemeId}", validation.ThemeId);
            return (null, ["Failed to save theme file."]);
        }
        finally
        {
            ThemeWriteLock.Release();
        }
    }

    /// <summary>
    /// Delete an uploaded theme by ID.
    /// </summary>
    public async Task<bool> DeleteThemeAsync(string themeId)
    {
        var normalizedId = NormalizeThemeId(themeId);
        if (string.IsNullOrEmpty(normalizedId))
        {
            return false;
        }

        EnsureDirectory();

        await ThemeWriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var config = MoonfinPlugin.Instance?.Configuration;
            UploadedThemeEntry? match = null;

            if (config?.UploadedThemes != null)
            {
                match = config.UploadedThemes.FirstOrDefault(entry =>
                    string.Equals(entry.Id, normalizedId, StringComparison.OrdinalIgnoreCase));
            }

            var path = match != null ? ResolveThemePath(match) : Path.Combine(_themesPath, normalizedId + ".json");
            var removedAny = false;

            if (File.Exists(path))
            {
                File.Delete(path);
                removedAny = true;
            }

            if (match != null && config?.UploadedThemes != null)
            {
                removedAny = config.UploadedThemes.Remove(match) || removedAny;
                PersistPluginConfiguration(config);
            }

            return removedAny;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete theme {ThemeId}", normalizedId);
            return false;
        }
        finally
        {
            ThemeWriteLock.Release();
        }
    }

    private async Task<JsonElement?> ReadThemeFileAsync(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse theme JSON at {ThemePath}", path);
            return null;
        }
    }

    private static UploadedThemeEntry CloneEntry(UploadedThemeEntry source)
    {
        return new UploadedThemeEntry
        {
            Id = source.Id,
            DisplayName = source.DisplayName,
            FileName = source.FileName,
            SizeBytes = source.SizeBytes,
            UploadedAtUtc = source.UploadedAtUtc,
            UploadedByUserId = source.UploadedByUserId,
            ChecksumSha256 = source.ChecksumSha256
        };
    }

    private static void PersistPluginConfiguration(PluginConfiguration config)
    {
        var plugin = MoonfinPlugin.Instance;
        if (plugin == null)
        {
            return;
        }

        // Jellyfin plugin APIs differ by version; try known methods via reflection.
        var pluginType = plugin.GetType();

        var updateConfig = pluginType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
                string.Equals(method.Name, "UpdateConfiguration", StringComparison.Ordinal)
                && method.GetParameters().Length == 1);
        if (updateConfig != null)
        {
            updateConfig.Invoke(plugin, new object?[] { config });
            return;
        }

        var saveConfig = pluginType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
                string.Equals(method.Name, "SaveConfiguration", StringComparison.Ordinal)
                && method.GetParameters().Length == 0);
        if (saveConfig != null)
        {
            saveConfig.Invoke(plugin, null);
        }
    }

    private string ResolveThemePath(UploadedThemeEntry entry)
    {
        var fileName = string.IsNullOrWhiteSpace(entry.FileName)
            ? entry.Id + ".json"
            : entry.FileName;
        return Path.Combine(_themesPath, fileName);
    }

    private List<UploadedThemeEntry> GetIndexEntries()
    {
        var entries = MoonfinPlugin.Instance?.Configuration?.UploadedThemes;
        if (entries == null)
        {
            return new List<UploadedThemeEntry>();
        }

        return entries;
    }

    private void EnsureDirectory()
    {
        if (!Directory.Exists(_themesPath))
        {
            Directory.CreateDirectory(_themesPath);
        }
    }

    private static string NormalizeThemeId(string raw)
    {
        return (raw ?? string.Empty).Trim().ToLowerInvariant();
    }
}
