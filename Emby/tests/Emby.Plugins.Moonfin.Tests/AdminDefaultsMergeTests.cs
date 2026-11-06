using System.Reflection;
using Emby.Plugins.Moonfin.Models;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Model.Logging;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// A device profile wins over global, so Apply Defaults has to reach the device profiles a user
/// has, without wiping their own lists with the empty ones XmlSerializer makes for unset defaults.
/// </summary>
public sealed class AdminDefaultsMergeTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "moonfin-emby-merge-tests-" + Guid.NewGuid().ToString("N"));
    private readonly MoonfinSettingsService _service;

    public AdminDefaultsMergeTests()
    {
        _service = new MoonfinSettingsService(NoOpLogger.Create(), _dataPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    [Fact]
    public async Task MergingDefaultsReachesTheDeviceProfilesAUserHas()
    {
        var alice = Guid.NewGuid();
        await _service.SaveProfileAsync(
            alice,
            "desktop",
            new MoonfinSettingsProfile { CinemaModeEnabled = false, MediaBarLibraryIds = new List<string> { "lib-a" } },
            "test-seed",
            notifySettingsChanged: false);

        await _service.MergeDefaultsAsync(
            new PluginConfiguration { DefaultUserSettings = new MoonfinSettingsProfile { CinemaModeEnabled = true, MediaBarLibraryIds = new List<string>() } },
            new[] { "global" },
            null,
            null);

        var settings = (await _service.GetUserSettingsAsync(alice))!;
        Assert.True(settings.Desktop?.CinemaModeEnabled);
        Assert.True(settings.Global?.CinemaModeEnabled);
        Assert.Equal(new[] { "lib-a" }, settings.Desktop?.MediaBarLibraryIds);
        Assert.Null(settings.Mobile);
        Assert.Null(settings.Tv);
    }

    [Fact]
    public async Task MergingDefaultsToASingleUserReachesTheirDeviceProfiles()
    {
        var alice = Guid.NewGuid();
        await _service.SaveProfileAsync(
            alice,
            "tv",
            new MoonfinSettingsProfile { CinemaModeEnabled = false },
            "test-seed",
            notifySettingsChanged: false);

        await _service.MergeDefaultsAsync(
            new PluginConfiguration { DefaultUserSettings = new MoonfinSettingsProfile { CinemaModeEnabled = true } },
            new[] { "global" },
            null,
            new[] { alice });

        Assert.True((await _service.GetUserSettingsAsync(alice))!.Tv?.CinemaModeEnabled);
    }

    /// <summary>An Emby logger that drops everything.</summary>
    internal class NoOpLogger : DispatchProxy
    {
        public static ILogger Create() => Create<ILogger, NoOpLogger>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
}
