using System.Text.Json;
using Moonfin.Server.Models;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// The top bar and sidebar styles travel in the synced profile. Older app versions share the
/// same profile without knowing either field, so their pushes must not wipe what a newer device stored.
/// </summary>
public sealed class NavStyleProfileTests : IDisposable
{
    private static readonly JsonSerializerOptions CamelCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _dataPath;
    private readonly MoonfinSettingsService _service;

    public NavStyleProfileTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), "moonfin-nav-style-tests-" + Guid.NewGuid().ToString("N"));
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
    public void TheFieldsUseTheClientsWireNames()
    {
        var json = JsonSerializer.Serialize(new MoonfinSettingsProfile
        {
            TopNavbarStyle = "atlas",
            SidebarStyle = "mosaic",
        });

        Assert.Contains("\"topNavbarStyle\":\"atlas\"", json);
        Assert.Contains("\"sidebarStyle\":\"mosaic\"", json);

        var back = JsonSerializer.Deserialize<MoonfinSettingsProfile>(json)!;
        Assert.Equal("atlas", back.TopNavbarStyle);
        Assert.Equal("mosaic", back.SidebarStyle);
    }

    [Fact]
    public async Task APushWithoutTheFieldsKeepsWhatWasStored()
    {
        var userId = Guid.NewGuid();
        await _service.SaveProfileAsync(
            userId,
            "tv",
            new MoonfinSettingsProfile { TopNavbarStyle = "tabline", SidebarStyle = "reel" },
            "new-client",
            notifySettingsChanged: false);

        // What an app from before these styles sends.
        await _service.SaveProfileAsync(
            userId,
            "tv",
            new MoonfinSettingsProfile { NavbarPosition = "left" },
            "old-client",
            notifySettingsChanged: false);

        var settings = JsonSerializer.Deserialize<MoonfinUserSettings>(
            File.ReadAllText(Path.Combine(_dataPath, userId + ".json")),
            CamelCase)!;
        var tv = settings.GetProfile("tv")!;
        Assert.Equal("left", tv.NavbarPosition);
        Assert.Equal("tabline", tv.TopNavbarStyle);
        Assert.Equal("reel", tv.SidebarStyle);
    }
}
