using System.Reflection;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// The parental limit reads the user policy and the rating table through members that
/// changed shape between Jellyfin 10.10 and 12. A rename would leave a lookup null and quietly
/// turn every limit into "no limit", which nothing else here would notice.
/// </summary>
public sealed class ServerRatingLimitBindingTests
{
    [Theory]
    [InlineData("GetUserDtoMethod")]
    [InlineData("GetParentalRatingsMethod")]
    public void TheReflectedMembersBindAgainstTheReferencedServer(string field)
    {
        var binder = typeof(ServerRatingLimitService).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing " + field);

        Assert.NotNull((MethodInfo?)binder.GetValue(null));
    }
}
