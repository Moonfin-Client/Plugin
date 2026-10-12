using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>Finds the movie or series behind a trailer the user can access.</summary>
    public sealed partial class CinemaMediaResolver
    {
        private readonly ILibraryManager _library;
        private readonly IProviderManager _providers;

        public CinemaMediaResolver(ILibraryManager library, IProviderManager providers)
        {
            _library = library;
            _providers = providers;
        }

        public sealed record Resolution(int? TmdbId, string? MediaType = null);

        public async Task<Resolution> ResolveMediaAsync(string itemId, User user,
            string? expectedMediaType, CancellationToken cancellationToken, bool allowFilenameSearch = false)
        {
            var item = _library.GetItemById(itemId) as Video;
            if (item == null || item is Episode || !item.IsVisibleStandalone(user)) return new(null);

            // Find the owning movie or series through Emby's parent hierarchy.
            BaseItem? owner = item.GetParents()
                .FirstOrDefault(parent => parent is Movie or Series or Season or Episode);
            if (owner == null && !string.IsNullOrWhiteSpace(item.Path))
            {
                // Another plugin may register the same trailer file as a separate video.
                owner = SingleOwner(_library.GetItemList(new InternalItemsQuery
                {
                    Path = item.Path,
                    ExtraTypes = new[] { ExtraType.Trailer },
                    GroupByPresentationUniqueKey = false,
                })
                .OfType<Video>()
                .Where(extra => extra.ExtraType == ExtraType.Trailer &&
                    string.Equals(extra.Path, item.Path, StringComparison.Ordinal) &&
                    extra.IsVisibleStandalone(user))
                .Select(extra => extra.GetParents()
                    .FirstOrDefault(parent => (parent is Movie or Series or Season or Episode)
                        && parent.IsVisibleStandalone(user))));
            }
            if (owner is Season or Episode)
            {
                if (owner.SeriesId <= 0) return new(null);
                owner = _library.GetItemById(owner.SeriesId) as Series;
                if (owner == null) return new(null);
            }
            if (owner != null && !owner.IsVisibleStandalone(user)) return new(null);

            return await ResolveItemAsync(item, owner, expectedMediaType,
                name => _library.ParseName(name.AsSpan()), cancellationToken, allowFilenameSearch)
                .ConfigureAwait(false);
        }

        internal static BaseItem? SingleOwner(IEnumerable<BaseItem?> candidates)
        {
            var owners = candidates.OfType<BaseItem>().GroupBy(item => item.Id)
                .Select(group => group.First()).Take(2).ToArray();
            return owners.Length == 1 ? owners[0] : null;
        }

        private static Task<IEnumerable<RemoteSearchResult>> AwaitSearch(
            Task<IEnumerable<RemoteSearchResult>> search, CancellationToken cancellationToken) => search;
    }
}
