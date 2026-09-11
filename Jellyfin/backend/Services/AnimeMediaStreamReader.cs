using System.Collections;
using System.Reflection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Moonfin.Server.Services;

/// <summary>
/// A helper for reading the audio tracks on a BaseItem, in a way that works across Jellyfin versions.
/// </summary>
public static class AnimeMediaStreamReader
{
    private static bool _resolved;
    private static Func<IMediaSourceManager?, BaseItem, IEnumerable?>? _accessor;
    private static readonly object Gate = new();

    /// <summary>
    /// The language of every audio track on an item. Empty when the streams could not be
    /// read at all, which the caller must treat as "unknown", never as "no dub".
    /// </summary>
    public static IReadOnlyList<string?> GetAudioLanguages(IMediaSourceManager? sourceManager, BaseItem item)
    {
        var streams = Resolve()?.Invoke(sourceManager, item);
        if (streams == null)
        {
            return Array.Empty<string?>();
        }

        var languages = new List<string?>();

        foreach (var stream in streams)
        {
            if (stream == null)
            {
                continue;
            }

            var type = stream.GetType();

            // MediaStream.Type is an enum whose name is stable even where the assembly
            // identity is not, so it is compared as text.
            var typeValue = type.GetProperty("Type")?.GetValue(stream)?.ToString();
            if (!string.Equals(typeValue, "Audio", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            languages.Add(type.GetProperty("Language")?.GetValue(stream) as string);
        }

        return languages;
    }

    public static bool IsSupported => Resolve() != null;

    private static Func<IMediaSourceManager?, BaseItem, IEnumerable?>? Resolve()
    {
        if (_resolved)
        {
            return _accessor;
        }

        lock (Gate)
        {
            if (_resolved)
            {
                return _accessor;
            }

            _accessor = BuildAccessor();
            _resolved = true;
            return _accessor;
        }
    }

    private static Func<IMediaSourceManager?, BaseItem, IEnumerable?>? BuildAccessor()
    {
        // Jellyfin 10.11 removed the IMediaSourceManager.GetMediaStreams(Guid) method that this plugin compiled against, 
        // so the plugin must now read the streams in a way that works across versions.
        var byGuid = typeof(IMediaSourceManager).GetMethod(
            "GetMediaStreams",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: new[] { typeof(Guid) },
            modifiers: null);

        if (byGuid != null)
        {
            return (manager, item) => manager == null
                ? null
                : byGuid.Invoke(manager, new object[] { item.Id }) as IEnumerable;
        }

        // Fallback: the entity helper, which is what 10.10 offered.
        var onItem = typeof(BaseItem).GetMethod(
            "GetMediaStreams",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);

        if (onItem != null)
        {
            return (_, item) => onItem.Invoke(item, Array.Empty<object>()) as IEnumerable;
        }

        return null;
    }
}
