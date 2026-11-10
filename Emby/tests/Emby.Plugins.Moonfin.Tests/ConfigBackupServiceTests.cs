using System;
using System.IO;
using System.Reflection;
using Emby.Plugins.Moonfin.Models;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Model.Logging;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// Getting the reset detector wrong one way overwrites the last good backup with defaults, and the
/// other way nags an admin who hasn't configured anything yet.
/// </summary>
public class ConfigBackupServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moonfin-config-backup-" + Guid.NewGuid().ToString("N"));
    private readonly string _live;
    private readonly string _backup;

    public ConfigBackupServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _live = Path.Combine(_dir, "Emby.Plugins.Moonfin.xml");
        _backup = Path.Combine(_dir, "Moonfin", "config-backup", "Emby.Plugins.Moonfin.xml");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ConfigBackupService NewService() => new(_live, _backup, NoOpLogger.Create());

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

        Assert.True(ConfigBackupService.IsAllDefaults(config));
    }

    [Theory]
    [InlineData("SeerrUrl", "http://seerr.local:5055")]
    [InlineData("MdblistApiKey", "abc123")]
    [InlineData("PublicServerUrl", "https://emby.example.com")]
    public void AnyConfiguredStringMakesItNotDefault(string propertyName, string value)
    {
        var config = new PluginConfiguration();
        typeof(PluginConfiguration).GetProperty(propertyName)!.SetValue(config, value);

        Assert.False(ConfigBackupService.IsAllDefaults(config));
    }

    [Fact]
    public void AdminDefaultsMakeItNotDefault()
    {
        var config = new PluginConfiguration { DefaultUserSettings = new MoonfinSettingsProfile { CinemaModeEnabled = true } };

        Assert.False(ConfigBackupService.IsAllDefaults(config));
    }

    [Fact]
    public void ANullConfigurationIsNotTreatedAsAWipe()
    {
        Assert.False(ConfigBackupService.IsAllDefaults(null));
    }

    [Fact]
    public void ARealConfigurationRefreshesTheBackup()
    {
        File.WriteAllText(_live, "<real/>");
        var service = NewService();

        service.Evaluate(new PluginConfiguration { SeerrEnabled = true });

        Assert.Equal("<real/>", File.ReadAllText(_backup));
        Assert.False(service.ConfigurationLooksReset);
    }

    [Fact]
    public void ADefaultConfigurationLeavesTheBackupAloneAndFlagsAReset()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_backup)!);
        File.WriteAllText(_backup, "<real/>");
        File.WriteAllText(_live, "<defaults/>");
        var service = NewService();

        service.Evaluate(new PluginConfiguration());

        Assert.Equal("<real/>", File.ReadAllText(_backup));
        Assert.True(service.ConfigurationLooksReset);
    }

    [Fact]
    public void AFreshInstallWithNoBackupIsNotAReset()
    {
        File.WriteAllText(_live, "<defaults/>");
        var service = NewService();

        service.Evaluate(new PluginConfiguration());

        Assert.False(service.ConfigurationLooksReset);
        Assert.False(File.Exists(_backup));
    }

    [Fact]
    public void RestoreCopiesTheBackupOverTheLiveFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_backup)!);
        File.WriteAllText(_backup, "<real/>");
        File.WriteAllText(_live, "<defaults/>");
        var service = NewService();
        service.Evaluate(new PluginConfiguration());

        Assert.True(service.Restore());

        Assert.Equal("<real/>", File.ReadAllText(_live));
        Assert.False(service.ConfigurationLooksReset);
    }

    [Fact]
    public void RestoreWithNoBackupAnswersFalse()
    {
        File.WriteAllText(_live, "<defaults/>");

        Assert.False(NewService().Restore());
        Assert.Equal("<defaults/>", File.ReadAllText(_live));
    }

    private class NoOpLogger : DispatchProxy
    {
        public static ILogger Create() => Create<ILogger, NoOpLogger>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
}
