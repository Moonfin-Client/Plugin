using System.Text.Json;
using Moonfin.Server.Models;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// The media bar sort order travels in the synced profile. Older app versions share the
/// same profile without knowing this field, so their pushes must not wipe what a newer device stored.
/// </summary>
public sealed class MediaBarSortOrderProfileTests : IDisposable
{
    private static readonly JsonSerializerOptions CamelCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _dataPath;
    private readonly MoonfinSettingsService _service;

    public MediaBarSortOrderProfileTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), "moonfin-media-bar-sort-tests-" + Guid.NewGuid().ToString("N"));
        _service = new MoonfinSettingsService(new NoOpLogger<MoonfinSettingsService>(), _dataPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    [Fact]
    public void TheFieldUsesTheClientsWireName()
    {
        var json = JsonSerializer.Serialize(new MoonfinSettingsProfile
        {
            MediaBarSortOrder = "dateAddedDesc",
        });

        Assert.Contains("\"mediaBarSortOrder\":\"dateAddedDesc\"", json);

        var back = JsonSerializer.Deserialize<MoonfinSettingsProfile>(json)!;
        Assert.Equal("dateAddedDesc", back.MediaBarSortOrder);
    }

    [Fact]
    public async Task APushWithoutTheFieldKeepsWhatWasStored()
    {
        var userId = Guid.NewGuid();
        await _service.SaveProfileAsync(
            userId,
            "tv",
            new MoonfinSettingsProfile { MediaBarSortOrder = "releaseDateDesc" },
            "new-client",
            notifySettingsChanged: false);

        // What an app from before this setting sends.
        await _service.SaveProfileAsync(
            userId,
            "tv",
            new MoonfinSettingsProfile { MediaBarSourceType = "recentlyAdded" },
            "old-client",
            notifySettingsChanged: false);

        var settings = JsonSerializer.Deserialize<MoonfinUserSettings>(
            File.ReadAllText(Path.Combine(_dataPath, userId + ".json")),
            CamelCase)!;
        var tv = settings.GetProfile("tv")!;
        Assert.Equal("recentlyAdded", tv.MediaBarSourceType);
        Assert.Equal("releaseDateDesc", tv.MediaBarSortOrder);
    }
}
