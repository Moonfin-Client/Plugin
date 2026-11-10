using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moonfin.Romm;

namespace Moonfin.Games;

public partial class GamesService
{
    public GamesService(
        ILibraryManager libraryManager,
        RdbService? rdb = null,
        LaunchBoxService? launchBox = null,
        ArcadeCompatibilityService? arcadeCompatibility = null,
        ArcadeCoreOverrideService? arcadeCoreOverrides = null,
        GameBackendOverrideService? gameBackendOverrides = null,
        ILogger<GamesService>? logger = null,
        RommSource? romm = null)
        : this(
            new JellyfinGameLibraryHost(libraryManager, (ILogger?)logger ?? NullLogger.Instance),
            rdb,
            launchBox,
            arcadeCompatibility,
            arcadeCoreOverrides,
            gameBackendOverrides,
            logger,
            romm)
    {
    }
}

/// <summary>Finds game libraries among Jellyfin's virtual folders.</summary>
internal sealed class JellyfinGameLibraryHost : IGameLibraryHost
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;

    internal JellyfinGameLibraryHost(ILibraryManager libraryManager, ILogger logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public List<GameLibrary> FindLibraries(IReadOnlyList<string> configuredIds)
    {
        var result = new List<GameLibrary>();

        if (configuredIds.Count > 0)
        {
            // The picker stores the id the app sees, a user view id, which can differ from
            // VirtualFolderInfo.ItemId. So each one is looked up with GetItemById first, and a
            // virtual folder with the same id is the fallback.
            foreach (var cid in configuredIds)
            {
                var resolved = ResolveConfiguredLibrary(cid);
                if (resolved != null)
                {
                    result.Add(resolved);
                }
            }

            return result;
        }

        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            if (string.IsNullOrEmpty(folder.Name) || !GamesService.AutoDetectLibraryName.IsMatch(folder.Name))
            {
                continue;
            }

            var locations = (folder.Locations ?? Array.Empty<string>()).Where(Directory.Exists).ToList();
            if (locations.Count == 0)
            {
                continue;
            }

            result.Add(new GameLibrary
            {
                Id = folder.ItemId ?? string.Empty,
                Name = folder.Name,
                Locations = locations
            });
        }

        return result;
    }

    public string? LibraryName(string libraryId)
    {
        if (Guid.TryParse(libraryId, out var guid))
        {
            try
            {
                var name = _libraryManager.GetItemById(guid)?.Name;
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Looking up the RomM library {LibraryId} by id failed", libraryId);
            }
        }

        return _libraryManager.GetVirtualFolders()
            .FirstOrDefault(folder => GamesService.SameId(folder.ItemId, libraryId))?.Name;
    }

    public object DescribeFolders() =>
        _libraryManager.GetVirtualFolders().Select(f => new
        {
            name = f.Name,
            itemId = f.ItemId,
            collectionType = f.CollectionType?.ToString(),
            locations = f.Locations,
            locationsExist = (f.Locations ?? Array.Empty<string>()).Select(Directory.Exists).ToArray()
        }).ToList();

    private GameLibrary? ResolveConfiguredLibrary(string? cid)
    {
        if (string.IsNullOrWhiteSpace(cid))
        {
            return null;
        }

        if (Guid.TryParse(cid, out var guid))
        {
            var item = _libraryManager.GetItemById(guid);
            var locs = (item?.PhysicalLocations ?? Array.Empty<string>()).Where(Directory.Exists).ToList();
            if (locs.Count > 0)
            {
                return new GameLibrary { Id = cid!, Name = item?.Name ?? "Games", Locations = locs };
            }
        }

        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            if (!GamesService.SameId(folder.ItemId, cid))
            {
                continue;
            }

            var locs = (folder.Locations ?? Array.Empty<string>()).Where(Directory.Exists).ToList();
            if (locs.Count > 0)
            {
                return new GameLibrary { Id = cid!, Name = folder.Name ?? "Games", Locations = locs };
            }
        }

        return null;
    }
}
