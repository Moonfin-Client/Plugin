using System.Text.Json;
using Moonfin.Server.Models;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// The mobile bottom navbar's style and pinned tabs travel in the synced profile. Older app
/// versions share the same profile without knowing either field, so their pushes must not wipe
/// what a newer device stored.
/// </summary>
public sealed class BottomNavbarProfileTests : IDisposable
{
    private static readonly JsonSerializerOptions CamelCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _dataPath;
    private readonly MoonfinSettingsService _service;

    public BottomNavbarProfileTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), "moonfin-bottom-nav-tests-" + Guid.NewGuid().ToString("N"));
        _service = new MoonfinSettingsService(new NoOpLogger<MoonfinSettingsService>(), _dataPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    private MoonfinUserSettings Read(Guid userId) =>
        JsonSerializer.Deserialize<MoonfinUserSettings>(
            File.ReadAllText(Path.Combine(_dataPath, userId + ".json")),
            CamelCase)!;

    [Fact]
    public void TheFieldsUseTheClientsWireNames()
    {
        var json = JsonSerializer.Serialize(new MoonfinSettingsProfile
        {
            BottomNavbarStyle = "split",
            BottomNavbarTabs = new List<string> { "libraries", "liveTv" },
        });

        Assert.Contains("\"bottomNavbarStyle\":\"split\"", json);
        Assert.Contains("\"bottomNavbarTabs\":[\"libraries\",\"liveTv\"]", json);

        var back = JsonSerializer.Deserialize<MoonfinSettingsProfile>(json)!;
        Assert.Equal("split", back.BottomNavbarStyle);
        Assert.Equal(new[] { "libraries", "liveTv" }, back.BottomNavbarTabs);
    }

    [Fact]
    public async Task APushWithoutTheFieldsKeepsWhatWasStored()
    {
        var userId = Guid.NewGuid();
        await _service.SaveProfileAsync(
            userId,
            "mobile",
            new MoonfinSettingsProfile
            {
                BottomNavbarStyle = "strip",
                BottomNavbarTabs = new List<string> { "search" },
            },
            "new-client",
            notifySettingsChanged: false);

        // What an app from before the bottom navbar styles sends.
        await _service.SaveProfileAsync(
            userId,
            "mobile",
            new MoonfinSettingsProfile { NavbarPosition = "bottom" },
            "old-client",
            notifySettingsChanged: false);

        var mobile = Read(userId).GetProfile("mobile")!;
        Assert.Equal("bottom", mobile.NavbarPosition);
        Assert.Equal("strip", mobile.BottomNavbarStyle);
        Assert.Equal(new[] { "search" }, mobile.BottomNavbarTabs);
    }

    [Fact]
    public async Task AnEmptyTabListIsKeptAsAutomatic()
    {
        var userId = Guid.NewGuid();
        await _service.SaveProfileAsync(
            userId,
            "mobile",
            new MoonfinSettingsProfile { BottomNavbarTabs = new List<string> { "genres" } },
            "client",
            notifySettingsChanged: false);

        // The client sends an empty list once the user resets to automatic.
        await _service.SaveProfileAsync(
            userId,
            "mobile",
            new MoonfinSettingsProfile { BottomNavbarTabs = new List<string>() },
            "client",
            notifySettingsChanged: false);

        Assert.Empty(Read(userId).GetProfile("mobile")!.BottomNavbarTabs!);
    }
}
