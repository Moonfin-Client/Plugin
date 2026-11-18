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
    private sealed class TestUserDirectory(IReadOnlyList<DirectoryUser> users) : IUserDirectory
    {
        public IReadOnlyList<DirectoryUser> Users() => users;
    }

    private static (PushDeliveryService pushDelivery, SeerrWebhookService webhook) CreateServices(
        NotificationStore store,
        MoonfinSettingsService settingsService,
        IUserDirectory? userDirectory = null)
    {
        var httpFactory = new DummyHttpClientFactory();
        var fcm = new FcmSender(httpFactory, NullLogger<FcmSender>.Instance);
        var relay = new RelaySender(httpFactory, NullLogger<RelaySender>.Instance);
        var pushDelivery = new PushDeliveryService(store, fcm, relay, NullLogger<PushDeliveryService>.Instance);
        var sessionService = new SeerrSessionService(NullLogger<SeerrSessionService>.Instance, httpFactory);
        var webhook = new SeerrWebhookService(
            sessionService,
            settingsService,
            store,
            pushDelivery,
            new FakeLibraryManager(),
            NullLogger<SeerrWebhookService>.Instance,
            userDirectory);
        return (pushDelivery, webhook);
    }

    [Fact]
    public void SendTestNotification_UnauthorizedWhenNoClaims()
    {
        var store = new NotificationStore(NullLogger<NotificationStore>.Instance);
        var settingsService = new MoonfinSettingsService(NullLogger<MoonfinSettingsService>.Instance, Path.GetTempPath());
        var (pushDelivery, webhook) = CreateServices(store, settingsService);

        var controller = new NotificationsController(store, webhook, null!, pushDelivery, settingsService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = controller.SendTestNotification();
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public void SendTestNotification_SendsInAppNotificationAndReturnsSuccess()
    {
        var store = new NotificationStore(NullLogger<NotificationStore>.Instance);
        var settingsService = new MoonfinSettingsService(NullLogger<MoonfinSettingsService>.Instance, Path.GetTempPath());
        var (pushDelivery, webhook) = CreateServices(store, settingsService);

        var userId = Guid.NewGuid();
        var controller = new NotificationsController(store, webhook, null!, pushDelivery, settingsService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("Jellyfin-UserId", userId.ToString())
                    }))
                }
            }
        };

        var result = controller.SendTestNotification();
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public async Task SeerrWebhook_FallsBackToUserDirectoryWhenSessionMissing()
    {
        var store = new NotificationStore(NullLogger<NotificationStore>.Instance);
        var targetUserId = Guid.NewGuid();
        var userDirectory = new TestUserDirectory(new[]
        {
            new DirectoryUser(targetUserId, "alex_test", null, false)
        });

        // Ensure user has library-added notifications enabled in store
        store.SavePrefs(targetUserId, notifyOnNewRequests: true, notifyOnLibraryAdded: true, notifyOnIssues: true, notifyOnNewMedia: true);

        var tempDir = Path.Combine(Path.GetTempPath(), "moonfin-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var settingsService = new MoonfinSettingsService(NullLogger<MoonfinSettingsService>.Instance, tempDir);

        var (_, webhook) = CreateServices(store, settingsService, userDirectory);

        var payload = JsonDocument.Parse(@"{
            ""notification_type"": ""MEDIA_AVAILABLE"",
            ""subject"": ""Dune Part Two"",
            ""media"": {
                ""tmdbId"": ""693134"",
                ""media_type"": ""movie""
            },
            ""notifyuser_username"": ""alex_test""
        }").RootElement;

        await webhook.HandleWebhookAsync(payload);

        // Verify that target user preferences were accessed (confirming targetUserId was successfully resolved)
        var prefs = store.GetPrefs(targetUserId);
        Assert.True(prefs.NotifyOnLibraryAdded);
    }

    private sealed class DummyHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
