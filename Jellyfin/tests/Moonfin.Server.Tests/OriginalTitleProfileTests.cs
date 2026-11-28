using System.Text.Json;
using Moonfin.Server.Models;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class OriginalTitleProfileTests : IDisposable
{
    private static readonly JsonSerializerOptions CamelCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _dataPath;
    private readonly MoonfinSettingsService _service;

    public OriginalTitleProfileTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), "moonfin-original-title-tests-" + Guid.NewGuid().ToString("N"));
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
    public void FieldsUseClientWireNames()
    {
        var json = JsonSerializer.Serialize(new MoonfinSettingsProfile
        {
            DetailOriginalTitleMode = "subtitle",
            LibraryOriginalTitleMode = "parentheses",
        });

        Assert.Contains("\"detailOriginalTitleMode\":\"subtitle\"", json);
        Assert.Contains("\"libraryOriginalTitleMode\":\"parentheses\"", json);

        var roundTripped = JsonSerializer.Deserialize<MoonfinSettingsProfile>(json)!;
        Assert.Equal("subtitle", roundTripped.DetailOriginalTitleMode);
        Assert.Equal("parentheses", roundTripped.LibraryOriginalTitleMode);
    }

    [Fact]
    public async Task PushWithoutFieldsPreservesStoredValues()
    {
        var userId = Guid.NewGuid();
        await _service.SaveProfileAsync(
            userId,
            "tv",
            new MoonfinSettingsProfile
            {
                DetailOriginalTitleMode = "subtitle",
                LibraryOriginalTitleMode = "replace",
            },
            "new-client",
            notifySettingsChanged: false);

        await _service.SaveProfileAsync(
            userId,
            "tv",
            new MoonfinSettingsProfile { DetailScreenStyle = "classic" },
            "old-client",
            notifySettingsChanged: false);

        var stored = Read(userId).GetProfile("tv")!;
        Assert.Equal("subtitle", stored.DetailOriginalTitleMode);
        Assert.Equal("replace", stored.LibraryOriginalTitleMode);
    }
}
