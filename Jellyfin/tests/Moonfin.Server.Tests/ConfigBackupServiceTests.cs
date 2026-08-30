using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// Getting the reset detector wrong one way overwrites the last good backup with defaults, and the
/// other way nags an admin who has not configured anything yet.
/// </summary>
public class ConfigBackupServiceTests
{
    [Fact]
    public void AFreshConfigurationReadsAsAllDefaults()
    {
        Assert.True(ConfigBackupService.IsAllDefaults(new PluginConfiguration()));
    }

    [Fact]
    public void AMintedWebhookSecretDoesNotCountAsConfigured()
    {
        var config = new PluginConfiguration();
        config.EnsureWebhookSecret();

        // The plugin mints this on first load, so a fresh install and a just-reset one both have
        // one. Treating it as a real setting would mean never noticing a wipe.
        Assert.True(ConfigBackupService.IsAllDefaults(config));
    }

    [Theory]
    [InlineData("SeerrUrl", "http://seerr.local:5055")]
    [InlineData("MdblistApiKey", "abc123")]
    [InlineData("TmdbApiKey", "def456")]
    [InlineData("PublicServerUrl", "https://jellyfin.example.com")]
    public void AnyConfiguredStringMakesItNotDefault(string propertyName, string value)
    {
        var config = new PluginConfiguration();
        typeof(PluginConfiguration).GetProperty(propertyName)!.SetValue(config, value);

        Assert.False(ConfigBackupService.IsAllDefaults(config));
    }

    [Fact]
    public void AChangedBooleanMakesItNotDefault()
    {
        var config = new PluginConfiguration { SeerrEnabled = !new PluginConfiguration().SeerrEnabled };

        Assert.False(ConfigBackupService.IsAllDefaults(config));
    }

    [Fact]
    public void AdminDefaultsMakeItNotDefault()
    {
        var config = new PluginConfiguration
        {
            DefaultUserSettings = new Models.MoonfinSettingsProfile { CinemaModeEnabled = true },
        };

        Assert.False(ConfigBackupService.IsAllDefaults(config));
    }

    [Fact]
    public void AStoredMessageMakesItNotDefault()
    {
        var config = new PluginConfiguration();
        config.Messages.Add(new ServerMessage { Id = "m1", Title = "Hi", Body = "There" });

        Assert.False(ConfigBackupService.IsAllDefaults(config));
    }

    [Fact]
    public void AnUploadedThemeMakesItNotDefault()
    {
        var config = new PluginConfiguration();
        config.UploadedThemes.Add(new UploadedThemeEntry { Id = "t1", DisplayName = "Mine" });

        Assert.False(ConfigBackupService.IsAllDefaults(config));
    }

    [Fact]
    public void ANullConfigurationIsNotTreatedAsAWipe()
    {
        // The plugin instance not being up yet must not be read as "everything was reset",
        // or a restart with a slow host would offer to restore over a perfectly good file.
        Assert.False(ConfigBackupService.IsAllDefaults(null));
    }
}
