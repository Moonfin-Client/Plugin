using System.Reflection;
using System.Runtime.CompilerServices;
using MediaBrowser.Controller.Entities.Movies;
using Moonfin.Server.Api;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// Pins the trimmed card shape the browse endpoints return, without standing up Jellyfin's
/// service graph, the same way GamesControllerArtworkContractTests does.
/// </summary>
public sealed class MoonfinControllerCardContractTests
{
    private static readonly MethodInfo MapItemToDtoMethod =
        typeof(MoonfinController).GetMethod("MapItemToDto", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("Missing MapItemToDto.");

    // Clients read UserData.Played for the watched checkmark and PlaybackPositionTicks for the
    // resume bar, so dropping the key makes every card these endpoints return look unwatched.
    [Fact]
    public void MapItemToDto_CarriesUserDataSoCardsCanShowPlayedState()
    {
        var controller = (MoonfinController)RuntimeHelpers.GetUninitializedObject(typeof(MoonfinController));

        var dto = MapItemToDtoMethod.Invoke(controller, [new Movie { Name = "Seed" }, null]);

        Assert.NotNull(dto);
        var userData = dto.GetType().GetProperty("UserData");
        Assert.NotNull(userData);

        // With no user to resolve the key is still present and simply carries nothing, so a client
        // reads "unknown" rather than a confident "unwatched".
        Assert.Null(userData.GetValue(dto));
    }

    // A rename on the server side would leave the lookup null and quietly send every card without
    // user data, which nothing else here would notice.
    [Fact]
    public void UserDataLookup_BindsAgainstTheReferencedServer()
    {
        var binder = typeof(MoonfinController)
            .GetField("_getUserDataDto", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing _getUserDataDto.");

        Assert.NotNull((MethodInfo?)binder.GetValue(null));
    }
}
