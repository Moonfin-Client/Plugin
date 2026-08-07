using System.Text.Json;

namespace Moonfin.Server.Services;

/// <summary>
/// Shared persistence for a per-user string-to-string preference map, one JSON file per user
/// below a caller-supplied root folder. Extracted from <see cref="ArcadeCoreOverrideService"/> and
/// <see cref="GameBackendOverrideService"/>, which were identical apart from field/parameter
/// naming (arcade core vs. player backend) -- both wrap an instance of this store, parameterized
/// by their own distinct data folder, so their on-disk file layout and JSON shape are unchanged.
///
/// Persistence uses a temp-file-then-atomic-<see cref="File.Move(string, string, bool)"/> write
/// (a torn write can never leave a half-written preferences file in place) and treats a corrupt
/// JSON file as empty rather than throwing (a damaged preference file must never prevent a game
/// from launching).
/// </summary>
public sealed class PerUserPreferenceStore
{
    private readonly string _root;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    /// <summary>Creates the store below a caller-supplied plugin data directory.</summary>
    public PerUserPreferenceStore(string root)
    {
        _root = root;
    }

    public async Task<string?> GetAsync(Guid userId, string key, CancellationToken cancellationToken = default)
    {
        var values = await ReadAsync(userId, cancellationToken).ConfigureAwait(false);
        return values.TryGetValue(key, out var value) ? value : null;
    }

    public async Task SetAsync(Guid userId, string key, string? value, CancellationToken cancellationToken = default)
    {
        var path = PathFor(userId);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var values = await ReadUnsafeAsync(path, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(value))
            {
                values.Remove(key);
            }
            else
            {
                values[key] = value;
            }

            Directory.CreateDirectory(_root);
            var temporary = path + ".tmp";
            try
            {
                await using (var stream = File.Create(temporary))
                {
                    await JsonSerializer.SerializeAsync(stream, values, _jsonOptions, cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadAsync(Guid userId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadUnsafeAsync(PathFor(userId), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadUnsafeAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // A corrupt/damaged preference file must not prevent a game from launching.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private string PathFor(Guid userId) => Path.Combine(_root, $"{userId:N}.json");
}
