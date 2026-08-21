namespace Moonfin.Server.Services;

/// <summary>
/// Persists a user's explicit player-backend preference for an individual game.
/// This is intentionally separate from arcade core selection: EmulatorJS is a
/// player backend, not a libretro core.
///
/// This is a thin, name-preserving wrapper over <see cref="PerUserPreferenceStore"/>, which holds
/// the actual read/write/JSON-recovery logic shared with <see cref="ArcadeCoreOverrideService"/>.
/// The two services differ only in the vocabulary of their public API (game key / backend vs.
/// content key / core); the on-disk file layout and JSON shape are unchanged from before this
/// wrapper existed.
/// </summary>
public sealed class GameBackendOverrideService
{
    private readonly PerUserPreferenceStore _store;

    public GameBackendOverrideService(string root)
    {
        _store = new PerUserPreferenceStore(root);
    }

    public Task<string?> GetAsync(Guid userId, string gameKey, CancellationToken cancellationToken = default) =>
        _store.GetAsync(userId, gameKey, cancellationToken);

    public Task SetAsync(Guid userId, string gameKey, string? backend, CancellationToken cancellationToken = default) =>
        _store.SetAsync(userId, gameKey, backend, cancellationToken);
}
