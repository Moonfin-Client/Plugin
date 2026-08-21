using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Moonfin.Server.Tests;

/// <summary>
/// Minimal <see cref="ILibraryManager"/> stand-in for GamesService tests. GamesService's library
/// resolution (<c>GetGameLibraries</c>) only calls <see cref="ILibraryManager.GetVirtualFolders()"/>
/// when no library ids are explicitly configured, which is always true in this test process (no
/// plugin host runs here, so <c>MoonfinPlugin.Instance</c> stays null and configuredIds is empty).
/// Every other member of the (large, mostly server-internal) interface is stubbed to throw, since
/// GamesService never reaches them from this path; a test that somehow does exercise one will fail
/// loudly rather than silently returning bogus data.
/// </summary>
internal sealed class FakeLibraryManager : ILibraryManager
{
    private readonly List<VirtualFolderInfo> _folders;

    public FakeLibraryManager(List<VirtualFolderInfo> folders)
    {
        _folders = folders;
    }

    List<VirtualFolderInfo> ILibraryManager.GetVirtualFolders() => _folders;

    List<VirtualFolderInfo> ILibraryManager.GetVirtualFolders(bool includeRefreshState) => _folders;

    event System.EventHandler<MediaBrowser.Controller.Library.ItemChangeEventArgs>? ILibraryManager.ItemAdded
    {
        add { }
        remove { }
    }

    event System.EventHandler<MediaBrowser.Controller.Library.ItemChangeEventArgs>? ILibraryManager.ItemUpdated
    {
        add { }
        remove { }
    }

    event System.EventHandler<MediaBrowser.Controller.Library.ItemChangeEventArgs>? ILibraryManager.ItemRemoved
    {
        add { }
        remove { }
    }

    MediaBrowser.Controller.Entities.AggregateFolder ILibraryManager.RootFolder
    {
        get => throw new NotImplementedException();
    }

    System.Boolean ILibraryManager.IsScanRunning
    {
        get => throw new NotImplementedException();
    }

    MediaBrowser.Controller.Entities.BaseItem ILibraryManager.ResolvePath(MediaBrowser.Model.IO.FileSystemMetadata fileInfo, MediaBrowser.Controller.Entities.Folder? parent, MediaBrowser.Controller.Providers.IDirectoryService? directoryService)
    {
        throw new NotImplementedException();
    }

    System.Collections.Generic.IEnumerable<MediaBrowser.Controller.Entities.BaseItem> ILibraryManager.ResolvePaths(System.Collections.Generic.IEnumerable<MediaBrowser.Model.IO.FileSystemMetadata> files, MediaBrowser.Controller.Providers.IDirectoryService directoryService, MediaBrowser.Controller.Entities.Folder parent, MediaBrowser.Model.Configuration.LibraryOptions libraryOptions, System.Nullable<Jellyfin.Data.Enums.CollectionType> collectionType)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Controller.Entities.Person ILibraryManager.GetPerson(System.String name)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Controller.Entities.BaseItem ILibraryManager.FindByPath(System.String path, System.Nullable<System.Boolean> isFolder)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Controller.Entities.Audio.MusicArtist ILibraryManager.GetArtist(System.String name)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Controller.Entities.Audio.MusicArtist ILibraryManager.GetArtist(System.String name, MediaBrowser.Controller.Dto.DtoOptions options)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Controller.Entities.Studio ILibraryManager.GetStudio(System.String name)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Controller.Entities.Genre ILibraryManager.GetGenre(System.String name)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Controller.Entities.Audio.MusicGenre ILibraryManager.GetMusicGenre(System.String name)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Controller.Entities.Year ILibraryManager.GetYear(System.Int32 value)
    {
        throw new NotImplementedException();
    }

    System.Threading.Tasks.Task ILibraryManager.ValidatePeopleAsync(System.IProgress<System.Double> progress, System.Threading.CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    System.Threading.Tasks.Task ILibraryManager.ValidateMediaLibrary(System.IProgress<System.Double> progress, System.Threading.CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    System.Threading.Tasks.Task ILibraryManager.ValidateTopLibraryFolders(System.Threading.CancellationToken cancellationToken, System.Boolean removeRoot)
    {
        throw new NotImplementedException();
    }

    System.Threading.Tasks.Task ILibraryManager.UpdateImagesAsync(MediaBrowser.Controller.Entities.BaseItem item, System.Boolean forceUpdate)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Controller.Entities.BaseItem ILibraryManager.GetItemById(System.Guid id)
    {
        throw new NotImplementedException();
    }

    T ILibraryManager.GetItemById<T>(System.Guid id)
    {
        throw new NotImplementedException();
    }

    T ILibraryManager.GetItemById<T>(System.Guid id, System.Guid userId)
    {
        throw new NotImplementedException();
    }

    T ILibraryManager.GetItemById<T>(System.Guid id, Jellyfin.Data.Entities.User? user)
    {
        throw new NotImplementedException();
    }

    System.Threading.Tasks.Task<System.Collections.Generic.IEnumerable<MediaBrowser.Controller.Entities.Video>> ILibraryManager.GetIntros(MediaBrowser.Controller.Entities.BaseItem item, Jellyfin.Data.Entities.User user)
    {
        throw new NotImplementedException();
    }

    void ILibraryManager.AddParts(System.Collections.Generic.IEnumerable<MediaBrowser.Controller.Resolvers.IResolverIgnoreRule> rules, System.Collections.Generic.IEnumerable<MediaBrowser.Controller.Resolvers.IItemResolver> resolvers, System.Collections.Generic.IEnumerable<MediaBrowser.Controller.Library.IIntroProvider> introProviders, System.Collections.Generic.IEnumerable<MediaBrowser.Controller.Sorting.IBaseItemComparer> itemComparers, System.Collections.Generic.IEnumerable<MediaBrowser.Controller.Library.ILibraryPostScanTask> postscanTasks)
    {
        throw new NotImplementedException();
    }

    System.Collections.Generic.IEnumerable<MediaBrowser.Controller.Entities.BaseItem> ILibraryManager.Sort(System.Collections.Generic.IEnumerable<MediaBrowser.Controller.Entities.BaseItem> items, Jellyfin.Data.Entities.User? user, System.Collections.Generic.IEnumerable<Jellyfin.Data.Enums.ItemSortBy> sortBy, Jellyfin.Data.Enums.SortOrder sortOrder)
    {
        throw new NotImplementedException();
    }

    System.Collections.Generic.IEnumerable<MediaBrowser.Controller.Entities.BaseItem> ILibraryManager.Sort(System.Collections.Generic.IEnumerable<MediaBrowser.Controller.Entities.BaseItem> items, Jellyfin.Data.Entities.User? user, System.Collections.Generic.IEnumerable<System.ValueTuple<Jellyfin.Data.Enums.ItemSortBy, Jellyfin.Data.Enums.SortOrder>> orderBy)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Controller.Entities.Folder ILibraryManager.GetUserRootFolder()
    {
        throw new NotImplementedException();
    }

    void ILibraryManager.CreateItem(MediaBrowser.Controller.Entities.BaseItem item, MediaBrowser.Controller.Entities.BaseItem? parent)
    {
        throw new NotImplementedException();
    }

    void ILibraryManager.CreateItems(System.Collections.Generic.IReadOnlyList<MediaBrowser.Controller.Entities.BaseItem> items, MediaBrowser.Controller.Entities.BaseItem? parent, System.Threading.CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    System.Threading.Tasks.Task ILibraryManager.UpdateItemsAsync(System.Collections.Generic.IReadOnlyList<MediaBrowser.Controller.Entities.BaseItem> items, MediaBrowser.Controller.Entities.BaseItem parent, MediaBrowser.Controller.Library.ItemUpdateType updateReason, System.Threading.CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    System.Threading.Tasks.Task ILibraryManager.UpdateItemAsync(MediaBrowser.Controller.Entities.BaseItem item, MediaBrowser.Controller.Entities.BaseItem parent, MediaBrowser.Controller.Library.ItemUpdateType updateReason, System.Threading.CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Controller.Entities.BaseItem ILibraryManager.RetrieveItem(System.Guid id)
    {
        throw new NotImplementedException();
    }

    System.Nullable<Jellyfin.Data.Enums.CollectionType> ILibraryManager.GetContentType(MediaBrowser.Controller.Entities.BaseItem item)
    {
        throw new NotImplementedException();
    }

    System.Nullable<Jellyfin.Data.Enums.CollectionType> ILibraryManager.GetInheritedContentType(MediaBrowser.Controller.Entities.BaseItem item)
    {
        throw new NotImplementedException();
    }

    System.Nullable<Jellyfin.Data.Enums.CollectionType> ILibraryManager.GetConfiguredContentType(MediaBrowser.Controller.Entities.BaseItem item)
    {
        throw new NotImplementedException();
    }

    System.Nullable<Jellyfin.Data.Enums.CollectionType> ILibraryManager.GetConfiguredContentType(System.String path)
    {
        throw new NotImplementedException();
    }

    System.Collections.Generic.List<MediaBrowser.Model.IO.FileSystemMetadata> ILibraryManager.NormalizeRootPathList(System.Collections.Generic.IEnumerable<MediaBrowser.Model.IO.FileSystemMetadata> paths)
    {
        throw new NotImplementedException();
    }

    void ILibraryManager.RegisterItem(MediaBrowser.Controller.Entities.BaseItem item)
    {
        throw new NotImplementedException();
    }

    void ILibraryManager.DeleteItem(MediaBrowser.Controller.Entities.BaseItem item, MediaBrowser.Controller.Library.DeleteOptions options)
    {
        throw new NotImplementedException();
    }

    void ILibraryManager.DeleteItem(MediaBrowser.Controller.Entities.BaseItem item, MediaBrowser.Controller.Library.DeleteOptions options, System.Boolean notifyParentItem)
    {
        throw new NotImplementedException();
    }

    void ILibraryManager.DeleteItem(MediaBrowser.Controller.Entities.BaseItem item, MediaBrowser.Controller.Library.DeleteOptions options, MediaBrowser.Controller.Entities.BaseItem parent, System.Boolean notifyParentItem)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Controller.Entities.UserView ILibraryManager.GetNamedView(Jellyfin.Data.Entities.User user, System.String name, System.Guid parentId, System.Nullable<Jellyfin.Data.Enums.CollectionType> viewType, System.String sortName)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Controller.Entities.UserView ILibraryManager.GetNamedView(Jellyfin.Data.Entities.User user, System.String name, System.Nullable<Jellyfin.Data.Enums.CollectionType> viewType, System.String sortName)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Controller.Entities.UserView ILibraryManager.GetNamedView(System.String name, Jellyfin.Data.Enums.CollectionType viewType, System.String sortName)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Controller.Entities.UserView ILibraryManager.GetNamedView(System.String name, System.Guid parentId, System.Nullable<Jellyfin.Data.Enums.CollectionType> viewType, System.String sortName, System.String uniqueId)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Controller.Entities.UserView ILibraryManager.GetShadowView(MediaBrowser.Controller.Entities.BaseItem parent, System.Nullable<Jellyfin.Data.Enums.CollectionType> viewType, System.String sortName)
    {
        throw new NotImplementedException();
    }

    System.Nullable<System.Int32> ILibraryManager.GetSeasonNumberFromPath(System.String path)
    {
        throw new NotImplementedException();
    }

    System.Boolean ILibraryManager.FillMissingEpisodeNumbersFromPath(MediaBrowser.Controller.Entities.TV.Episode episode, System.Boolean forceRefresh)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Controller.Providers.ItemLookupInfo ILibraryManager.ParseName(System.String name)
    {
        throw new NotImplementedException();
    }

    System.Guid ILibraryManager.GetNewItemId(System.String key, System.Type type)
    {
        throw new NotImplementedException();
    }

    System.Collections.Generic.IEnumerable<MediaBrowser.Controller.Entities.BaseItem> ILibraryManager.FindExtras(MediaBrowser.Controller.Entities.BaseItem owner, System.Collections.Generic.IReadOnlyList<MediaBrowser.Model.IO.FileSystemMetadata> fileSystemChildren, MediaBrowser.Controller.Providers.IDirectoryService directoryService)
    {
        throw new NotImplementedException();
    }

    System.Collections.Generic.List<MediaBrowser.Controller.Entities.Folder> ILibraryManager.GetCollectionFolders(MediaBrowser.Controller.Entities.BaseItem item)
    {
        throw new NotImplementedException();
    }

    System.Collections.Generic.List<MediaBrowser.Controller.Entities.Folder> ILibraryManager.GetCollectionFolders(MediaBrowser.Controller.Entities.BaseItem item, System.Collections.Generic.IEnumerable<MediaBrowser.Controller.Entities.Folder> allUserRootChildren)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Model.Configuration.LibraryOptions ILibraryManager.GetLibraryOptions(MediaBrowser.Controller.Entities.BaseItem item)
    {
        throw new NotImplementedException();
    }

    System.Collections.Generic.List<MediaBrowser.Controller.Entities.PersonInfo> ILibraryManager.GetPeople(MediaBrowser.Controller.Entities.BaseItem item)
    {
        throw new NotImplementedException();
    }

    System.Collections.Generic.List<MediaBrowser.Controller.Entities.PersonInfo> ILibraryManager.GetPeople(MediaBrowser.Controller.Entities.InternalPeopleQuery query)
    {
        throw new NotImplementedException();
    }

    System.Collections.Generic.List<MediaBrowser.Controller.Entities.Person> ILibraryManager.GetPeopleItems(MediaBrowser.Controller.Entities.InternalPeopleQuery query)
    {
        throw new NotImplementedException();
    }

    void ILibraryManager.UpdatePeople(MediaBrowser.Controller.Entities.BaseItem item, System.Collections.Generic.List<MediaBrowser.Controller.Entities.PersonInfo> people)
    {
        throw new NotImplementedException();
    }

    System.Threading.Tasks.Task ILibraryManager.UpdatePeopleAsync(MediaBrowser.Controller.Entities.BaseItem item, System.Collections.Generic.List<MediaBrowser.Controller.Entities.PersonInfo> people, System.Threading.CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    System.Collections.Generic.List<System.Guid> ILibraryManager.GetItemIds(MediaBrowser.Controller.Entities.InternalItemsQuery query)
    {
        throw new NotImplementedException();
    }

    System.Collections.Generic.List<System.String> ILibraryManager.GetPeopleNames(MediaBrowser.Controller.Entities.InternalPeopleQuery query)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Model.Querying.QueryResult<MediaBrowser.Controller.Entities.BaseItem> ILibraryManager.QueryItems(MediaBrowser.Controller.Entities.InternalItemsQuery query)
    {
        throw new NotImplementedException();
    }

    System.String ILibraryManager.GetPathAfterNetworkSubstitution(System.String path, MediaBrowser.Controller.Entities.BaseItem? ownerItem)
    {
        throw new NotImplementedException();
    }

    System.Threading.Tasks.Task<MediaBrowser.Controller.Entities.ItemImageInfo> ILibraryManager.ConvertImageToLocal(MediaBrowser.Controller.Entities.BaseItem item, MediaBrowser.Controller.Entities.ItemImageInfo image, System.Int32 imageIndex, System.Boolean removeOnFailure)
    {
        throw new NotImplementedException();
    }

    System.Collections.Generic.List<MediaBrowser.Controller.Entities.BaseItem> ILibraryManager.GetItemList(MediaBrowser.Controller.Entities.InternalItemsQuery query)
    {
        throw new NotImplementedException();
    }

    System.Collections.Generic.List<MediaBrowser.Controller.Entities.BaseItem> ILibraryManager.GetItemList(MediaBrowser.Controller.Entities.InternalItemsQuery query, System.Boolean allowExternalContent)
    {
        throw new NotImplementedException();
    }

    System.Collections.Generic.List<MediaBrowser.Controller.Entities.BaseItem> ILibraryManager.GetItemList(MediaBrowser.Controller.Entities.InternalItemsQuery query, System.Collections.Generic.List<MediaBrowser.Controller.Entities.BaseItem> parents)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Model.Querying.QueryResult<MediaBrowser.Controller.Entities.BaseItem> ILibraryManager.GetItemsResult(MediaBrowser.Controller.Entities.InternalItemsQuery query)
    {
        throw new NotImplementedException();
    }

    System.Boolean ILibraryManager.IgnoreFile(MediaBrowser.Model.IO.FileSystemMetadata file, MediaBrowser.Controller.Entities.BaseItem parent)
    {
        throw new NotImplementedException();
    }

    System.Guid ILibraryManager.GetStudioId(System.String name)
    {
        throw new NotImplementedException();
    }

    System.Guid ILibraryManager.GetGenreId(System.String name)
    {
        throw new NotImplementedException();
    }

    System.Guid ILibraryManager.GetMusicGenreId(System.String name)
    {
        throw new NotImplementedException();
    }

    System.Threading.Tasks.Task ILibraryManager.AddVirtualFolder(System.String name, System.Nullable<MediaBrowser.Model.Entities.CollectionTypeOptions> collectionType, MediaBrowser.Model.Configuration.LibraryOptions options, System.Boolean refreshLibrary)
    {
        throw new NotImplementedException();
    }

    System.Threading.Tasks.Task ILibraryManager.RemoveVirtualFolder(System.String name, System.Boolean refreshLibrary)
    {
        throw new NotImplementedException();
    }

    void ILibraryManager.AddMediaPath(System.String virtualFolderName, MediaBrowser.Model.Configuration.MediaPathInfo mediaPath)
    {
        throw new NotImplementedException();
    }

    void ILibraryManager.UpdateMediaPath(System.String virtualFolderName, MediaBrowser.Model.Configuration.MediaPathInfo mediaPath)
    {
        throw new NotImplementedException();
    }

    void ILibraryManager.RemoveMediaPath(System.String virtualFolderName, System.String mediaPath)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Model.Querying.QueryResult<System.ValueTuple<MediaBrowser.Controller.Entities.BaseItem, MediaBrowser.Model.Dto.ItemCounts>> ILibraryManager.GetGenres(MediaBrowser.Controller.Entities.InternalItemsQuery query)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Model.Querying.QueryResult<System.ValueTuple<MediaBrowser.Controller.Entities.BaseItem, MediaBrowser.Model.Dto.ItemCounts>> ILibraryManager.GetMusicGenres(MediaBrowser.Controller.Entities.InternalItemsQuery query)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Model.Querying.QueryResult<System.ValueTuple<MediaBrowser.Controller.Entities.BaseItem, MediaBrowser.Model.Dto.ItemCounts>> ILibraryManager.GetStudios(MediaBrowser.Controller.Entities.InternalItemsQuery query)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Model.Querying.QueryResult<System.ValueTuple<MediaBrowser.Controller.Entities.BaseItem, MediaBrowser.Model.Dto.ItemCounts>> ILibraryManager.GetArtists(MediaBrowser.Controller.Entities.InternalItemsQuery query)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Model.Querying.QueryResult<System.ValueTuple<MediaBrowser.Controller.Entities.BaseItem, MediaBrowser.Model.Dto.ItemCounts>> ILibraryManager.GetAlbumArtists(MediaBrowser.Controller.Entities.InternalItemsQuery query)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Model.Querying.QueryResult<System.ValueTuple<MediaBrowser.Controller.Entities.BaseItem, MediaBrowser.Model.Dto.ItemCounts>> ILibraryManager.GetAllArtists(MediaBrowser.Controller.Entities.InternalItemsQuery query)
    {
        throw new NotImplementedException();
    }

    System.Int32 ILibraryManager.GetCount(MediaBrowser.Controller.Entities.InternalItemsQuery query)
    {
        throw new NotImplementedException();
    }

    System.Threading.Tasks.Task ILibraryManager.RunMetadataSavers(MediaBrowser.Controller.Entities.BaseItem item, MediaBrowser.Controller.Library.ItemUpdateType updateReason)
    {
        throw new NotImplementedException();
    }

    MediaBrowser.Controller.Entities.BaseItem ILibraryManager.GetParentItem(System.Nullable<System.Guid> parentId, System.Nullable<System.Guid> userId)
    {
        throw new NotImplementedException();
    }

    void ILibraryManager.QueueLibraryScan()
    {
        throw new NotImplementedException();
    }
}
