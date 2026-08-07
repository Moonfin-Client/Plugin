namespace Moonfin.Server.Services;

/// <summary>
/// Persists a user's explicit arcade-core choice by content fingerprint. Preferences are kept
/// outside the ROM tree and survive a folder rename or a regenerated opaque game token.
///
/// This is a thin, name-preserving wrapper over <see cref="PerUserPreferenceStore"/>, which holds
/// the actual read/write/JSON-recovery logic shared with <see cref="GameBackendOverrideService"/>.
/// The two services differ only in the vocabulary of their public API (content key / core vs.
/// game key / backend); the on-disk file layout and JSON shape are unchanged from before this
/// wrapper existed.
/// </summary>
public sealed class ArcadeCoreOverrideService
{
    private readonly PerUserPreferenceStore _store;

    /// <summary>Creates the store below a caller-supplied plugin data directory.</summary>
    public ArcadeCoreOverrideService(string root)
    {
        _store = new PerUserPreferenceStore(root);
    }

    public Task<string?> GetAsync(Guid userId, string contentKey, CancellationToken cancellationToken = default) =>
        _store.GetAsync(userId, contentKey, cancellationToken);

    public Task SetAsync(Guid userId, string contentKey, string? core, CancellationToken cancellationToken = default) =>
        _store.SetAsync(userId, contentKey, core, cancellationToken);
}
