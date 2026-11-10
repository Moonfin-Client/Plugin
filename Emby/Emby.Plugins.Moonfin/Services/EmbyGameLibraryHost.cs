using MediaBrowser.Controller.Library;
using Moonfin.Games;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>Finds game libraries among Emby's virtual folders.</summary>
    internal sealed class EmbyGameLibraryHost : IGameLibraryHost
    {
        private readonly ILibraryManager _libraryManager;

        public EmbyGameLibraryHost(ILibraryManager libraryManager) => _libraryManager = libraryManager;

        public List<GameLibrary> FindLibraries(IReadOnlyList<string> configuredIds)
        {
            var folders = _libraryManager.GetVirtualFolders();
            var result = new List<GameLibrary>();

            if (configuredIds.Count > 0)
            {
                // The picker stores the id the client sees (a user-view id), which can differ
                // from VirtualFolderInfo.ItemId. Match by ItemId first. If that misses, resolve
                // the id to its library name via GetItemById and match a folder by name.
                foreach (var cid in configuredIds)
                {
                    var folder = folders.FirstOrDefault(f => GamesService.SameId(f.ItemId, cid));
                    if (folder == null)
                    {
                        var name = ItemName(cid);
                        if (!string.IsNullOrEmpty(name))
                        {
                            folder = folders.FirstOrDefault(f =>
                                string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                        }
                    }

                    if (folder == null) continue;

                    var locations = (folder.Locations ?? Array.Empty<string>()).Where(Directory.Exists).ToList();
                    if (locations.Count == 0) continue;

                    result.Add(new GameLibrary { Id = cid, Name = folder.Name ?? "Games", Locations = locations });
                }

                return result;
            }

            foreach (var folder in folders)
            {
                if (string.IsNullOrEmpty(folder.Name) || !GamesService.AutoDetectLibraryName.IsMatch(folder.Name)) continue;
                var locations = (folder.Locations ?? Array.Empty<string>()).Where(Directory.Exists).ToList();
                if (locations.Count == 0) continue;
                result.Add(new GameLibrary { Id = folder.ItemId ?? string.Empty, Name = folder.Name, Locations = locations });
            }

            return result;
        }

        public string? LibraryName(string libraryId) =>
            ItemName(libraryId)
            ?? _libraryManager.GetVirtualFolders().FirstOrDefault(f => GamesService.SameId(f.ItemId, libraryId))?.Name;

        public object DescribeFolders() =>
            _libraryManager.GetVirtualFolders().Select(f => new
            {
                name = f.Name,
                itemId = f.ItemId,
                collectionType = f.CollectionType,
                locations = f.Locations,
                locationsExist = (f.Locations ?? Array.Empty<string>()).Select(Directory.Exists).ToArray()
            }).ToList();

        private string? ItemName(string id)
        {
            if (!Guid.TryParse(id, out var guid)) return null;
            try
            {
                var name = _libraryManager.GetItemById(guid)?.Name;
                return string.IsNullOrEmpty(name) ? null : name;
            }
            catch
            {
                return null;
            }
        }
    }
}
