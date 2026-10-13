using System.Reflection;
using Emby.Plugins.Moonfin.Api;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Net;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

public sealed class CinemaMediaResolverTests
{
    [Theory]
    [InlineData("Dune_438631_trailer.mp4", "movie", 438631)]
    [InlineData("Silo_tvdb403245_trailer.mp4", "tv", 403245)]
    public void FilenameIdsPreserveProviderType(string filename, string mediaType, int id)
    {
        var parsed = CinemaMediaResolver.ParseTrailerFilenameIdentity(filename);
        Assert.NotNull(parsed);
        Assert.Equal(mediaType, parsed.Value.MediaType);
        Assert.Equal(id, parsed.Value.TmdbId ?? parsed.Value.TvdbId);
    }

    [Fact]
    public void MatchingExtraOwnersMustBeUnambiguous()
    {
        var movie = new Movie { Id = Guid.NewGuid() };
        Assert.Same(movie, CinemaMediaResolver.SingleOwner([movie, new Movie { Id = movie.Id }]));
        Assert.Null(CinemaMediaResolver.SingleOwner([movie, new Series { Id = Guid.NewGuid() }]));
        Assert.Null(CinemaMediaResolver.SingleOwner([]));
    }

    [Fact]
    public void EndpointRequiresAuthentication()
    {
        Assert.NotNull(typeof(ResolveCinemaMediaRequest).GetCustomAttribute<AuthenticatedAttribute>());
    }
}
