using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moonfin.Server.Api;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class NotificationTestEndpointTests
{
    private readonly MoonfinSettingsService _settings =
        new(NullLogger<MoonfinSettingsService>.Instance, Path.GetTempPath());

    private NotificationsController CreateController(params Claim[] claims)
    {
        var store = new NotificationStore(NullLogger<NotificationStore>.Instance);
        var http = new ThrowingHttpClientFactory("These tests never send a push.");
        var pushDelivery = new PushDeliveryService(
            store,
            new FcmSender(http, NullLogger<FcmSender>.Instance),
            new RelaySender(http, NullLogger<RelaySender>.Instance),
            NullLogger<PushDeliveryService>.Instance);

        return new NotificationsController(store, null!, null!, pushDelivery, _settings)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims))
                }
            }
        };
    }

    [Fact]
    public void SendTestNotification_UnauthorizedWhenNoClaims()
    {
        Assert.IsType<UnauthorizedObjectResult>(CreateController().SendTestNotification());
    }

    [Fact]
    public void SendTestNotification_SendsInAppNotificationAndReturnsSuccess()
    {
        var userId = Guid.NewGuid();
        var channel = _settings.RegisterSseChannel(userId);

        var result = CreateController(new Claim("Jellyfin-UserId", userId.ToString())).SendTestNotification();

        var ok = Assert.IsType<OkObjectResult>(result);
        using var response = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(0, response.RootElement.GetProperty("devicesCount").GetInt32());

        Assert.True(channel.Reader.TryRead(out var eventJson));
        using var evt = JsonDocument.Parse(eventJson!);
        Assert.Equal("Moonfin Push Test", evt.RootElement.GetProperty("title").GetString());
        Assert.Equal("", evt.RootElement.GetProperty("route").GetString());
    }
}
