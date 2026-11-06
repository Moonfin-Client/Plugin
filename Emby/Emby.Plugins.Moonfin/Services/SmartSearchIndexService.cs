using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using Moonfin.SmartSearch;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Feeds the shared smart search engine from this Emby library: movies, shows, the people
    /// credited on them, music artists and albums, and books.
    /// </summary>
    public class SmartSearchIndexService
    {
        private const int PageSize = 1000;

        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _buildGate = new SemaphoreSlim(1, 1);

        // Built on first use, so a server with smart search turned off never loads the index files.
        private readonly Lazy<SmartSearchEngine> _engine;

        public SmartSearchIndexService(ILibraryManager libraryManager, string dataFolder, ILogger logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _engine = new Lazy<SmartSearchEngine>(() => new SmartSearchEngine(
                dataFolder,
                () => SmartModel.Named(Plugin.Instance?.Configuration?.SmartSearchModel)));
        }

        public static bool Enabled => Plugin.Instance?.Configuration?.SmartSearchEnabled == true;

        public SmartSearchEngine Engine => _engine.Value;

        public async Task RebuildAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (!await _buildGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                _logger.Info("Smart search: a rebuild is already running");
                return;
            }

            try
            {
                using var http = MoonfinHttp.CreateClient(TimeSpan.FromSeconds(100), "Moonfin/1.0");
                await Engine.EnsureModelAsync(http, cancellationToken).ConfigureAwait(false);
                progress.Report(5);

                var started = DateTime.UtcNow;
                var library = CollectLibrary(cancellationToken);
                var items = library.Build(Engine.KnownFor);
                Engine.RebuildLibrary(items, new Progress<double>(p => progress.Report(5 + 45 * p)), cancellationToken);
                _logger.Info("Smart search: library index has {0} entries ({1}s)", items.Count, (int)(DateTime.UtcNow - started).TotalSeconds);
                progress.Report(50);

                var apiKey = Plugin.Instance?.Configuration?.TmdbApiKey;
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    _logger.Info("Smart search: no server-wide TMDB key, so there are no Seerr suggestions");
                }
                else
                {
                    started = DateTime.UtcNow;
                    await Engine.RebuildCatalogAsync(http, apiKey!, new Progress<double>(p => progress.Report(50 + 45 * p)), cancellationToken).ConfigureAwait(false);
                    _logger.Info("Smart search: catalog index has {0} titles ({1}s)", Engine.CatalogCount, (int)(DateTime.UtcNow - started).TotalSeconds);

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
                    IncludeItemTypes = new[] { "Person" },
                    Name = entry.Title,
                    Limit = 1
                }).Items.FirstOrDefault();
            }

            return Guid.TryParse(entry.Key, out var id) ? _libraryManager.GetItemById(id) : null;
        }

        private SmartLibrary CollectLibrary(CancellationToken cancellationToken)
        {
            var library = new SmartLibrary();
            var gameRoots = new GamesScanner(_libraryManager, Plugin.Instance?.Romm).GetGameLibraries().SelectMany(l => l.Locations).ToList();
            foreach (var item in Page(new[] { "Movie", "Series" }, cancellationToken))
            {
                // ROMs in a game library are stored as movies, but they aren't something to watch.
                if (item.Path != null && gameRoots.Any(root => item.Path.StartsWith(root, StringComparison.Ordinal)))
                {
                    continue;
                }

                var people = GetPeople(item);
                string? tmdbId = null;
                item.ProviderIds?.TryGetValue("Tmdb", out tmdbId);
                library.AddTitle(
                    item.Id.ToString("N"),
                    item is Series,
                    item.Name,
                    item.ProductionYear ?? 0,
                    item is Series ? item.EndDate?.Year : null,
                    tmdbId,
                    item.Genres,
                    item.Tags,
                    people.Where(p => p.Type == PersonType.Actor || p.Type == PersonType.GuestStar).Select(p => p.Name),
                    people.Where(p => p.Type == PersonType.Director).Select(p => p.Name),
                    item.Overview);
            }

            foreach (var album in Page(new[] { "MusicAlbum" }, cancellationToken).OfType<MusicAlbum>())
            {
                var artists = album.AlbumArtists?.Length > 0 ? album.AlbumArtists : album.Artists ?? Array.Empty<string>();
                library.AddAlbum(album.Id.ToString("N"), album.Name, album.ProductionYear ?? 0, artists, album.Genres, album.Overview);
            }

            foreach (var artist in Page(new[] { "MusicArtist" }, cancellationToken))
            {
                library.AddArtist(artist.Id.ToString("N"), artist.Name, artist.Genres, artist.Overview);
            }

            // Emby has no audiobook item type, its audiobooks are plain audio tracks.
            foreach (var book in Page(new[] { "Book" }, cancellationToken))
            {
                library.AddBook(book.Id.ToString("N"), book.Name, book.ProductionYear ?? 0, GetPeople(book).Select(p => p.Name), book.Genres, book.Overview);
            }

            return library;
        }

        private IEnumerable<BaseItem> Page(string[] kinds, CancellationToken cancellationToken)
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
                    Limit = PageSize
                }).Items;
                foreach (var item in page)
                {
                    yield return item;
                }

                if (page.Length < PageSize)
                {
                    yield break;
                }
            }
        }

        private List<PersonInfo> GetPeople(BaseItem item)
        {
            try
            {
                return _libraryManager.GetItemPeople(item) ?? new List<PersonInfo>();
            }
            catch (Exception ex)
            {
                _logger.Debug("Smart search: could not read people for " + item.Id + ": " + ex.Message);
                return new List<PersonInfo>();
            }
        }
    }
}
