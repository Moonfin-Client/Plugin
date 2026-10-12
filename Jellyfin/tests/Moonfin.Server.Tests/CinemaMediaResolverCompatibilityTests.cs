using System.Reflection;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.AspNetCore.Authorization;
using Moonfin.Server.Api;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class CinemaMediaResolverCompatibilityTests
{
    [Fact]
    public void ResolutionJsonMatchesMoonfinClientContract()
    {
        Assert.Equal("{\"tmdbId\":42,\"mediaType\":\"tv\"}",
            JsonSerializer.Serialize(new CinemaMediaResolver.Resolution(42, "tv")));
    }

    [Fact]
    public async Task MissingItemsAndEpisodesCannotResolve()
    {
        var video = new Video { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = "42", ["TmdbMediaType"] = "movie" } };
        var library = new FakeLibraryManager { ItemByIdHandler = _ => video };
        var resolver = new CinemaMediaResolver(library, null!);
        Assert.Equal(42, (await resolver.ResolveMediaAsync(video.Id, "movie", default)).TmdbId);
        library.ItemByIdHandler = _ => null;
        Assert.Null((await resolver.ResolveMediaAsync(video.Id, "movie", default)).TmdbId);
        library.ItemByIdHandler = _ => new Episode();
        Assert.Null((await resolver.ResolveMediaAsync(video.Id, "movie", default)).TmdbId);
    }

    [Fact]
    public async Task EndpointRequiresAnAuthenticatedUser()
    {
        Assert.NotNull(typeof(CinemaController).GetCustomAttribute<AuthorizeAttribute>());
        var controller = new CinemaController(null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<CinemaController>.Instance)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext(),
            },
        };
        Assert.IsType<Microsoft.AspNetCore.Mvc.UnauthorizedResult>(
            (await controller.ResolveMedia(Guid.NewGuid(), "tv", default)).Result);
    }
}
