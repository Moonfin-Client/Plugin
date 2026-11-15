using Emby.Plugins.Moonfin.Api;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// Ping reports recording owners only once the owners service runs: during Emby's startup the
/// owners endpoints answer 503, and a client told "supported" then would fail to record.
/// </summary>
public class RecordingOwnersPingTests
{
    [Fact]
    public void Ping_ReportsRecordingOwnersOnlyOnceTheServiceRuns()
    {
        var on = new PluginConfiguration { LiveTvRecordingOwners = true };
        var off = new PluginConfiguration { LiveTvRecordingOwners = false };

        Assert.False(SettingsService.RecordingOwnersSupported(on, serviceReady: false));
        Assert.True(SettingsService.RecordingOwnersSupported(on, serviceReady: true));
        Assert.False(SettingsService.RecordingOwnersSupported(off, serviceReady: true));

        // No configuration yet: the setting's default, on.
        Assert.True(SettingsService.RecordingOwnersSupported(null, serviceReady: true));
    }
}
