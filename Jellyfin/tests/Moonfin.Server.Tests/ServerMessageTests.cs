using Moonfin.Server;
using Moonfin.Server.Api;
using Xunit;

namespace Moonfin.Server.Tests;

public class ServerMessageTests
{
    private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ServerMessage Message() => new()
    {
        Id = "m1",
        Title = "Hello",
        Body = "Body",
        CreatedUtc = Now
    };

    [Fact]
    public void IsVisibleTo_ShowsPlainMessageToEveryone()
    {
        var message = Message();

        Assert.True(message.IsVisibleTo(Alice, isAdmin: false, Now));
        Assert.True(message.IsVisibleTo(Bob, isAdmin: true, Now));
    }

    [Fact]
    public void IsVisibleTo_HidesMessageBeforeItStarts()
    {
        var message = Message();
        message.StartUtc = Now.AddHours(1);

        Assert.False(message.IsVisibleTo(Alice, isAdmin: false, Now));
        Assert.True(message.IsVisibleTo(Alice, isAdmin: false, Now.AddHours(2)));
    }

    [Fact]
    public void IsVisibleTo_HidesMessageOnceItEnds()
    {
        var message = Message();
        message.EndUtc = Now.AddHours(1);

        Assert.True(message.IsVisibleTo(Alice, isAdmin: false, Now));
        Assert.False(message.IsVisibleTo(Alice, isAdmin: false, Now.AddHours(1)));
        Assert.False(message.IsVisibleTo(Alice, isAdmin: false, Now.AddHours(2)));
    }

    [Fact]
    public void IsVisibleTo_AdminsOnlyMessageStaysHiddenFromUsers()
    {
        var message = Message();
        message.Audience = ServerMessage.AudienceAdmins;

        Assert.False(message.IsVisibleTo(Alice, isAdmin: false, Now));
        Assert.True(message.IsVisibleTo(Alice, isAdmin: true, Now));
    }

    [Fact]
    public void IsVisibleTo_TargetedMessageReachesOnlyItsTargets()
    {
        var message = Message();
        message.Audience = ServerMessage.AudienceUsers;
        message.TargetUserIds = new List<string> { Alice.ToString() };

        Assert.True(message.IsVisibleTo(Alice, isAdmin: false, Now));
        Assert.False(message.IsVisibleTo(Bob, isAdmin: false, Now));

        // Being an admin does not grant access to someone else's message.
        Assert.False(message.IsVisibleTo(Bob, isAdmin: true, Now));
    }

    [Fact]
    public void Sanitize_FallsBackToDefaultsOnUnknownValues()
    {
        var message = Message();
        message.Color = "chartreuse";
        message.Delivery = "carrier-pigeon";
        message.Audience = "nobody";

        message.Sanitize();

        Assert.Equal(ServerMessage.ColorWhite, message.Color);
        Assert.Equal(ServerMessage.DeliveryInbox, message.Delivery);
        Assert.Equal(ServerMessage.AudienceAll, message.Audience);
    }

    [Theory]
    [InlineData(ServerMessage.ColorGreen)]
    [InlineData(ServerMessage.ColorRed)]
    [InlineData(ServerMessage.ColorYellow)]
    [InlineData(ServerMessage.ColorBlue)]
    [InlineData(ServerMessage.ColorWhite)]
    public void Sanitize_KeepsEveryColourTheAdminCanPick(string colour)
    {
        var message = Message();
        message.Color = colour;

        message.Sanitize();

        Assert.Equal(colour, message.Color);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("moonfin://open")]
    [InlineData("not a url")]
    public void Sanitize_DropsLinksThatAreNotHttp(string url)
    {
        var message = Message();
        message.ActionLabel = "Open";
        message.ActionUrl = url;

        message.Sanitize();

        Assert.Null(message.ActionUrl);
        Assert.Null(message.ActionLabel);
    }

    [Fact]
    public void Sanitize_KeepsHttpAndHttpsLinks()
    {
        var message = Message();
        message.ActionLabel = "Open";
        message.ActionUrl = "https://example.com/news";

        message.Sanitize();

        Assert.Equal("https://example.com/news", message.ActionUrl);
        Assert.Equal("Open", message.ActionLabel);
    }

    [Fact]
    public void Sanitize_DropsLabelWithoutLinkAndLinkWithoutLabel()
    {
        var labelOnly = Message();
        labelOnly.ActionLabel = "Open";
        labelOnly.Sanitize();
        Assert.Null(labelOnly.ActionLabel);

        var linkOnly = Message();
        linkOnly.ActionUrl = "https://example.com";
        linkOnly.Sanitize();
        Assert.Null(linkOnly.ActionUrl);
    }

    [Fact]
    public void Sanitize_CutsBodyToTheCap()
    {
        var message = Message();
        message.Body = new string('x', ServerMessage.MaxBodyLength + 500);

        message.Sanitize();

        Assert.Equal(ServerMessage.MaxBodyLength, message.Body.Length);
    }

    [Fact]
    public void Sanitize_ClearsTargetsWhenAudienceIsNotUsers()
    {
        var message = Message();
        message.Audience = ServerMessage.AudienceAll;
        message.TargetUserIds = new List<string> { Alice.ToString() };

        message.Sanitize();

        Assert.Empty(message.TargetUserIds);
    }

    [Fact]
    public void Sanitize_DropsTargetsThatAreNotUserIds()
    {
        var message = Message();
        message.Audience = ServerMessage.AudienceUsers;
        message.TargetUserIds = new List<string> { Alice.ToString(), "everyone" };

        message.Sanitize();

        Assert.Equal(new[] { Alice.ToString() }, message.TargetUserIds);
    }

    [Fact]
    public void Sanitize_ClearsEndDateThatWouldHideTheMessageForever()
    {
        var message = Message();
        message.StartUtc = Now;
        message.EndUtc = Now.AddHours(-1);

        message.Sanitize();

        Assert.Null(message.EndUtc);
    }

    [Fact]
    public void PruneMessages_RemovesExpiredMessages()
    {
        var fresh = Message();
        var expired = Message();
        expired.Id = "old";
        expired.EndUtc = DateTime.UtcNow.AddDays(-1);

        var messages = new List<ServerMessage> { fresh, expired };
        ServerMessage.Prune(messages);

        Assert.Equal(new[] { "m1" }, messages.Select(m => m.Id));
    }

    [Fact]
    public void PruneMessages_DropsTheOldestFirst()
    {
        var messages = new List<ServerMessage>();

        for (var i = 0; i < ServerMessage.MaxStored + 5; i++)
        {
            var message = Message();
            message.Id = $"m{i}";
            message.CreatedUtc = Now.AddMinutes(i);
            messages.Add(message);
        }

        ServerMessage.Prune(messages);

        Assert.Equal(ServerMessage.MaxStored, messages.Count);
        Assert.DoesNotContain(messages, m => m.Id == "m0");
        Assert.DoesNotContain(messages, m => m.Id == "m4");
        Assert.Contains(messages, m => m.Id == "m5");
    }
}
