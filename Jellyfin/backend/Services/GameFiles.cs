using Moonfin.Server.Services;

namespace Moonfin.Games;

/// <summary>The shared games code's crash-safe file access, through Jellyfin's AtomicFile.</summary>
internal static class GameFiles
{
    internal static T? ReadWithRecovery<T>(string path, Func<string, T?> parse)
        where T : class =>
        AtomicFile.ReadWithRecovery(path, parse);

    internal static Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken) =>
        AtomicFile.WriteAllTextAsync(path, contents, cancellationToken);
}
