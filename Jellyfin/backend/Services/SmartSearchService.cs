using System.Reflection;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moonfin.SmartSearch;

namespace Moonfin.Server.Services;

/// <summary>
/// Feeds the shared smart search engine from this Jellyfin library: movies, shows, the people
/// credited on them, music artists and albums, and books.
/// </summary>
public class SmartSearchService
{
    private const int PageSize = 1000;

    // GetPeople returns List on 10.10 and IReadOnlyList from 10.11, so it's bound by name.
    private static readonly MethodInfo? _getPeople = typeof(ILibraryManager).GetMethod("GetPeople", [typeof(BaseItem)]);

    private readonly ILibraryManager _libraryManager;
    private readonly GamesService _games;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SmartSearchService> _logger;
    private readonly SemaphoreSlim _buildGate = new(1, 1);

    // Built on first use, so a server with smart search turned off never loads the index files.
    private readonly Lazy<SmartSearchEngine> _engine = new(() => new SmartSearchEngine(
        MoonfinPlugin.ResolveDataFolderPath(),
        () => SmartModel.Named(MoonfinPlugin.Instance?.Configuration?.SmartSearchModel)));

    public SmartSearchService(ILibraryManager libraryManager, GamesService games, IHttpClientFactory httpClientFactory, ILogger<SmartSearchService> logger)
    {
        _libraryManager = libraryManager;
        _games = games;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public static bool Enabled => MoonfinPlugin.Instance?.Configuration?.SmartSearchEnabled == true;

    public SmartSearchEngine Engine => _engine.Value;

    public async Task RebuildAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!await _buildGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("Smart search: a rebuild is already running");
            return;
        }

        try
        {
            var http = _httpClientFactory.CreateClient();
            await Engine.EnsureModelAsync(http, cancellationToken).ConfigureAwait(false);
            progress.Report(5);

            var started = DateTime.UtcNow;
            var library = CollectLibrary(cancellationToken);
            var items = library.Build(Engine.KnownFor);
            Engine.RebuildLibrary(items, new Progress<double>(p => progress.Report(5 + 45 * p)), cancellationToken);
            _logger.LogInformation("Smart search: library index has {Count} entries ({Seconds:F0}s)", items.Count, (DateTime.UtcNow - started).TotalSeconds);
            progress.Report(50);

            var apiKey = MoonfinPlugin.Instance?.Configuration?.TmdbApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogInformation("Smart search: no server-wide TMDB key, so there are no Seerr suggestions");
            }
            else
            {
                started = DateTime.UtcNow;
                await Engine.RebuildCatalogAsync(http, apiKey, new Progress<double>(p => progress.Report(50 + 45 * p)), cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Smart search: catalog index has {Count} titles ({Seconds:F0}s)", Engine.CatalogCount, (DateTime.UtcNow - started).TotalSeconds);

                // People's texts name what the catalog says they're known for, so they're redone
                // against the fresh catalog. Unchanged entries keep their vectors, so this is quick.
                Engine.RebuildLibrary(library.Build(Engine.KnownFor), null, cancellationToken);
            }

            progress.Report(100);
        }
        finally
        {
            _buildGate.Release();
        }
    }

    /// <summary>The library item a hit stands for, or null when it's gone. People are looked up by name.</summary>
    public BaseItem? Resolve(SmartEntry entry)
    {
        if (entry.Kind == SmartQuery.Person)
        {
            return _libraryManager.GetItemsResult(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Person],
                Name = entry.Title,
                Limit = 1,
            }).Items.FirstOrDefault();
        }

        return Guid.TryParse(entry.Key, out var id) ? _libraryManager.GetItemById(id) : null;
    }

    private SmartLibrary CollectLibrary(CancellationToken cancellationToken)
    {
        var library = new SmartLibrary();
        var gameRoots = _games.GetGameLibraries().SelectMany(l => l.Locations).ToList();
        foreach (var item in Page([BaseItemKind.Movie, BaseItemKind.Series], cancellationToken))
        {
            // ROMs in a game library are stored as movies, but they aren't something to watch.
            if (item.Path != null && gameRoots.Any(root => item.Path.StartsWith(root, StringComparison.Ordinal)))
            {
                continue;
            }

            var people = GetPeople(item);
            library.AddTitle(
                item.Id.ToString("N"),
                item is Series,
                item.Name,
                item.ProductionYear ?? 0,
                item is Series ? item.EndDate?.Year : null,
                item.GetProviderId(MetadataProvider.Tmdb),
                item.Genres,
                item.Tags,
                people.Where(p => p.Type is PersonKind.Actor or PersonKind.GuestStar).Select(p => p.Name),
                people.Where(p => p.Type == PersonKind.Director).Select(p => p.Name),
                item.Overview);
        }

        foreach (var album in Page([BaseItemKind.MusicAlbum], cancellationToken).OfType<MusicAlbum>())
        {
            var artists = album.AlbumArtists.Count > 0 ? album.AlbumArtists : album.Artists;
            library.AddAlbum(album.Id.ToString("N"), album.Name, album.ProductionYear ?? 0, artists, album.Genres, album.Overview);
        }

        foreach (var artist in Page([BaseItemKind.MusicArtist], cancellationToken))
        {
            library.AddArtist(artist.Id.ToString("N"), artist.Name, artist.Genres, artist.Overview);
        }

        // Audiobooks are left out: Jellyfin keeps one AudioBook item per chapter file, and the
        // books themselves already come through as albums by their author.
        foreach (var book in Page([BaseItemKind.Book], cancellationToken))
        {
            library.AddBook(book.Id.ToString("N"), book.Name, book.ProductionYear ?? 0, GetPeople(book).Select(p => p.Name), book.Genres, book.Overview);
        }

        return library;
    }

    private IEnumerable<BaseItem> Page(BaseItemKind[] kinds, CancellationToken cancellationToken)
    {
        for (var start = 0; ; start += PageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = _libraryManager.GetItemsResult(new InternalItemsQuery
            {
                IncludeItemTypes = kinds,
                Recursive = true,
                IsVirtualItem = false,
                StartIndex = start,
                Limit = PageSize,
            }).Items;
            foreach (var item in page)
            {
                yield return item;
            }

            if (page.Count < PageSize)
            {
                yield break;
            }
        }
    }

    private IReadOnlyList<PersonInfo> GetPeople(BaseItem item)
    {
        try
        {
            return _getPeople?.Invoke(_libraryManager, [item]) is IEnumerable<PersonInfo> people
                ? people as IReadOnlyList<PersonInfo> ?? people.ToList()
                : Array.Empty<PersonInfo>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Smart search: could not read people for {ItemId}", item.Id);
            return Array.Empty<PersonInfo>();
        }
    }
}
