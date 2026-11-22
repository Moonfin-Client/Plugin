using System.Text.Json;
using Jellyfin.Data.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moonfin.Server.Models;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class RecentRowsHideWatchedTests
{
    [Fact]
    public void ProfileSerializesHideWatchedUnderTheClientWireKey()
    {
        var profile = new MoonfinSettingsProfile
        {
            HideWatchedInRecentRows = true
        };

        var json = JsonSerializer.Serialize(profile);
        Assert.Contains("\"hideWatchedInRecentRows\":true", json);

        var roundTripped = JsonSerializer.Deserialize<MoonfinSettingsProfile>(json)!;
        Assert.True(roundTripped.HideWatchedInRecentRows);
    }

    [Fact]
    public void ResolveHideWatchedWithDefaultsHandlesDeviceOnlyAndConflictingDefaults()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "moonfin-device-defaults-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var settings = new MoonfinSettingsService(NullLogger<MoonfinSettingsService>.Instance, tempRoot);
            var tvDefaults = new MoonfinSettingsProfile();
            var desktopDefaults = new MoonfinSettingsProfile();
            var mobileDefaults = new MoonfinSettingsProfile();
            var globalDefaults = new MoonfinSettingsProfile();
            settings.AdminDefaultsOverrideForTesting = profile => profile.ToLowerInvariant() switch
            {
                "tv" => tvDefaults,
                "desktop" => desktopDefaults,
                "mobile" => mobileDefaults,
                _ => globalDefaults,
            };

            var unsetUser = new MoonfinUserSettings();

            // Only the TV default is set.
            tvDefaults.HideWatchedInRecentRows = true;
            Assert.True(settings.ResolveHideWatchedWithDefaults(unsetUser, "global"));

            // TV and Desktop disagree, and hiding wins.
            desktopDefaults.HideWatchedInRecentRows = false;
            Assert.True(settings.ResolveHideWatchedWithDefaults(unsetUser, "global"));

            // Only the Desktop default is set.
            tvDefaults.HideWatchedInRecentRows = null;
            Assert.False(settings.ResolveHideWatchedWithDefaults(unsetUser, "global"));

            // The user's own value beats the admin defaults.
            tvDefaults.HideWatchedInRecentRows = true;
            var userFalse = new MoonfinUserSettings
            {
                Global = new MoonfinSettingsProfile { HideWatchedInRecentRows = false }
            };
            Assert.False(settings.ResolveHideWatchedWithDefaults(userFalse, "global"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DefaultSweepLeavesAUserWhoSavesMidSweepAlone()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "moonfin-sweep-gen0-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var user = new User("unsaved-user", "auth", "reset");
            var config = new MediaBrowser.Model.Configuration.UserConfiguration { HidePlayedInLatest = true };
            var userDto = new MediaBrowser.Model.Dto.UserDto { Id = user.Id, Configuration = config };

            var userManager = RecordingHostProxy.Create<IUserManager>((method, args) =>
            {
                if (method.Name == "GetUsers" || method.Name == "get_Users")
                {
                    return new List<User> { user };
                }

                if (method.Name == "GetUserById")
                {
                    return user;
                }

                if (method.Name == "GetUserDto")
                {
                    return userDto;
                }

                if (method.Name == "UpdateConfigurationAsync" && args is { Length: 2 } && args[1] is MediaBrowser.Model.Configuration.UserConfiguration updated)
                {
                    config.HidePlayedInLatest = updated.HidePlayedInLatest;
                    return Task.CompletedTask;
                }

                return null;
            });

            var sp = RecordingHostProxy.Create<IServiceProvider>((_, args) =>
                (Type)args![0]! == typeof(IUserManager) ? userManager : null);

            var settings = new MoonfinSettingsService(NullLogger<MoonfinSettingsService>.Instance, tempRoot, sp)
            {
                AdminDefaultsOverrideForTesting = profile =>
                    profile == "global" ? new MoonfinSettingsProfile { HideWatchedInRecentRows = true } : null
            };

            var concurrentSaveTriggered = 0;
            settings.BeforeNativeSyncWaitForTesting = id =>
            {
                if (id == user.Id && Interlocked.CompareExchange(ref concurrentSaveTriggered, 1, 0) == 0)
                {
                    // A save lands while the sweep waits for this user.
                    settings.SaveProfileAsync(
                        user.Id,
                        "global",
                        new MoonfinSettingsProfile { HideWatchedInRecentRows = false },
                        notifySettingsChanged: false).GetAwaiter().GetResult();
                }
            };

            await settings.SyncHideWatchedForAffectedUsersAsync();

            // The save that landed mid-sweep wins, so the default doesn't put true back.
            Assert.False(config.HidePlayedInLatest);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
