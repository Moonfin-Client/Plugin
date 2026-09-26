using System.Text.Json;
using Moonfin.Server.Models;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class SubtitleSelectionProfileTests
{
    private static readonly JsonSerializerOptions CamelCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void SubtitleSelectionFieldsUseClientWireNames()
    {
        var json = JsonSerializer.Serialize(new MoonfinSettingsProfile
        {
            PreferSdhSubtitles = true,
            PreferTextSubtitles = true,
            PreferExternalSubtitles = true,
        });

        Assert.Contains("\"preferSdhSubtitles\":true", json);
        Assert.Contains("\"preferTextSubtitles\":true", json);
        Assert.Contains("\"preferExternalSubtitles\":true", json);

        var back = JsonSerializer.Deserialize<MoonfinSettingsProfile>(json, CamelCase)!;
        Assert.True(back.PreferSdhSubtitles);
        Assert.True(back.PreferTextSubtitles);
        Assert.True(back.PreferExternalSubtitles);
    }

    [Fact]
    public void SubtitleSelectionFieldsRoundTripFalseAndNull()
    {
        var jsonFalse = JsonSerializer.Serialize(new MoonfinSettingsProfile
        {
            PreferTextSubtitles = false,
            PreferExternalSubtitles = false,
        });

        Assert.Contains("\"preferTextSubtitles\":false", jsonFalse);
        Assert.Contains("\"preferExternalSubtitles\":false", jsonFalse);

        var backFalse = JsonSerializer.Deserialize<MoonfinSettingsProfile>(jsonFalse, CamelCase)!;
        Assert.False(backFalse.PreferTextSubtitles);
        Assert.False(backFalse.PreferExternalSubtitles);

        var emptyProfile = new MoonfinSettingsProfile();
        var jsonEmpty = JsonSerializer.Serialize(emptyProfile);
        var backEmpty = JsonSerializer.Deserialize<MoonfinSettingsProfile>(jsonEmpty, CamelCase)!;
        Assert.Null(backEmpty.PreferTextSubtitles);
        Assert.Null(backEmpty.PreferExternalSubtitles);
    }
}
