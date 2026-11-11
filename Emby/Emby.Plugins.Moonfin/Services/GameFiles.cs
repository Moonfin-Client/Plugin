using Emby.Plugins.Moonfin.Services;

namespace Moonfin.Games
{
    /// <summary>The shared games code's crash-safe file access, through Emby's AtomicFile.</summary>
    internal static class GameFiles
    {
        internal static T? ReadWithRecovery<T>(string path, Func<string, T?> parse, out Exception? accessError)
            where T : class => AtomicFile.ReadWithRecovery(path, parse, out accessError);

        // Emby's AtomicFile has no async write, so the synchronous one runs off the calling thread.
        internal static Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken) =>
            Task.Run(() => AtomicFile.WriteAllText(path, contents), cancellationToken);
    }
}
