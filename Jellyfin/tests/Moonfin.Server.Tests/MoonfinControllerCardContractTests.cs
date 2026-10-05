using System.Reflection;
using MediaBrowser.Controller.Entities.Movies;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// Pins the trimmed card shape the browse and seasonal endpoints return, without standing up
/// Jellyfin's service graph, the same way GamesControllerArtworkContractTests does.
/// </summary>
public sealed class MoonfinControllerCardContractTests
{
    private static ItemCardDtoMapper Mapper() => new(null!, new NoOpLogger<ItemCardDtoMapper>());

    // Clients read UserData.Played for the watched checkmark and PlaybackPositionTicks for the
    // resume bar, so dropping the key makes every card these endpoints return look unwatched.
    [Fact]
    public void Map_CarriesUserDataSoCardsCanShowPlayedState()
    {
        var dto = Mapper().Map(new Movie { Name = "Seed" }, null);

        var userData = dto.GetType().GetProperty("UserData");
        Assert.NotNull(userData);

        // With no user to resolve the key is still present and simply carries nothing, so a client
        // reads "unknown" rather than a confident "unwatched".
        Assert.Null(userData.GetValue(dto));
    }

    // The seasonal row's owned titles carry the rating the server holds, so a client can apply
    // its own blocked list to them like any library card.
    [Fact]
    public void Map_CarriesTheOfficialRating()
    {
        var dto = Mapper().Map(new Movie { Name = "Seed", OfficialRating = "PG-13" }, null);

        Assert.Equal("PG-13", dto.GetType().GetProperty("OfficialRating")?.GetValue(dto));
    }

    // A rename on the server side would leave the lookup null and quietly send every card without
    // user data, which nothing else here would notice.
    [Fact]
    public void UserDataLookup_BindsAgainstTheReferencedServer()
    {
        var binder = typeof(ItemCardDtoMapper)
            .GetField("_getUserDataDto", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing _getUserDataDto.");

        Assert.NotNull((MethodInfo?)binder.GetValue(null));
    }
}
