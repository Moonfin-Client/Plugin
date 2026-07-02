namespace Moonfin.Server.Services;

/// <summary>
/// Stores per-user EmulatorJS save-state / SRAM blobs under the plugin data folder, mirroring
/// the per-user file layout used by <see cref="MoonfinSettingsService"/>. Saves are binary, so
/// they live under a <c>saves/{userId}/{gameId}.{kind}</c> tree rather than the JSON files.
/// </summary>
public class GameSavesService
{
    private readonly string _savesPath;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public GameSavesService()
    {
        var dataPath = MoonfinPlugin.Instance?.DataFolderPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jellyfin", "plugins", "Moonfin");
        _savesPath = Path.Combine(dataPath, "saves");
    }

    /// <summary>kind is "state" (EmulatorJS save state) or "sram" (battery save).</summary>
    public async Task<byte[]?> GetAsync(Guid userId, string gameId, string kind, CancellationToken cancellationToken)
    {
        var path = ResolvePath(userId, gameId, kind);
        if (path == null || !File.Exists(path))
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(Guid userId, string gameId, string kind, byte[] data, CancellationToken cancellationToken)
    {
        var path = ResolvePath(userId, gameId, kind);
        if (path == null)
        {
            throw new ArgumentException("Invalid game id.", nameof(gameId));
        }

        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.WriteAllBytesAsync(path, data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Builds a contained file path for a save blob. The gameId is sanitized to a safe file
    /// name (it is a base64url ROM token from the games API) so it can never escape the tree.
    /// </summary>
    private string? ResolvePath(Guid userId, string gameId, string kind)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return null;
        }

        var normalizedKind = kind?.Trim().ToLowerInvariant();
        var safeKind = (normalizedKind == "sram" || normalizedKind == "settings")
            ? normalizedKind
            : "state";
        var safeGame = SanitizeFileName(gameId);
        if (safeGame.Length == 0)
        {
            return null;
        }

        var path = Path.GetFullPath(Path.Combine(_savesPath, userId.ToString("N"), $"{safeGame}.{safeKind}"));

        // Defense in depth: ensure the resolved path stays within the saves root.
        var rootFull = Path.GetFullPath(_savesPath);
        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSep, StringComparison.Ordinal) ? path : null;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Where(c => Array.IndexOf(invalid, c) < 0 && c != '.').ToArray();
        var sanitized = new string(chars);
        return sanitized.Length > 200 ? sanitized[..200] : sanitized;
    }
}
