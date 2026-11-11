using System.Text.Json;
using Moonfin.Server.Models;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// Auto downloads from the Moonfin app, the detail button limit from Smart-TV and Roku, and
/// playlist grouping from Smart-TV travel in the synced profile under the clients' keys.
/// </summary>
public sealed class ClientOnlyKeysProfileTests
{
    [Fact]
    public void TheFieldsSurviveARoundTrip()
    {
        const string sent = "{\"autoDownloadEnabled\":true,\"autoDownloadBackgroundRefresh\":false," +
            "\"autoDownloadKeepUnwatched\":3,\"autoDownloadDeleteAfterHours\":48," +
            "\"detailButtonsMaxVisible\":4,\"playlistsGroupByType\":false}";

        var json = JsonSerializer.Serialize(JsonSerializer.Deserialize<MoonfinSettingsProfile>(sent));

        Assert.Contains("\"autoDownloadEnabled\":true", json);
        Assert.Contains("\"autoDownloadBackgroundRefresh\":false", json);
        Assert.Contains("\"autoDownloadKeepUnwatched\":3", json);
        Assert.Contains("\"autoDownloadDeleteAfterHours\":48", json);
        Assert.Contains("\"detailButtonsMaxVisible\":4", json);
        Assert.Contains("\"playlistsGroupByType\":false", json);
    }
}
