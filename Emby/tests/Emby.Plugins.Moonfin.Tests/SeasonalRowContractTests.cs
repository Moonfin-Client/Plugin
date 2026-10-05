using System.Text.Json;
using Emby.Plugins.Moonfin.Api;
using Emby.Plugins.Moonfin.Models;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// Pins the JSON the clients read for the seasonal row, serialized the way the endpoints do it.
/// </summary>
public sealed class SeasonalRowContractTests
{
    [Fact]
    public void TheRowSerializesWithTheNamesTheClientsRead()
    {
        var response = new SeasonalRowResponse
        {
            Holiday = "halloween",
            Country = "CA",
            Items = [new { Id = "abc", Name = "Hocus Pocus" }],
            Suggestions = [new CustomRowItem { Id = 10439, Name = "Hocus Pocus", Type = "Movie", OfficialRating = "PG", Overview = "Witches", Rating = 7.1, Genres = ["Comedy"], RunTimeTicks = 96 * TimeSpan.TicksPerMinute }]
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(response, MoonfinJson.Options));
        var root = doc.RootElement;

        Assert.Equal("halloween", root.GetProperty("holiday").GetString());
        Assert.Equal("CA", root.GetProperty("country").GetString());
        Assert.Equal("Hocus Pocus", root.GetProperty("items")[0].GetProperty("Name").GetString());
        var suggestion = root.GetProperty("suggestions")[0];
        Assert.Equal(10439, suggestion.GetProperty("id").GetInt64());
        Assert.Equal("PG", suggestion.GetProperty("officialRating").GetString());
        Assert.Equal("Witches", suggestion.GetProperty("overview").GetString());
        Assert.Equal(7.1, suggestion.GetProperty("rating").GetDouble());
        Assert.Equal("Comedy", suggestion.GetProperty("genres")[0].GetString());
        Assert.Equal(96 * TimeSpan.TicksPerMinute, suggestion.GetProperty("runTimeTicks").GetInt64());
    }

    [Fact]
    public void AnEmptyRowKeepsItsListsAndDropsTheHoliday()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new SeasonalRowResponse(), MoonfinJson.Options));
        var root = doc.RootElement;

        // MoonfinJson leaves nulls out, so a missing holiday is the "no row" signal here.
        Assert.False(root.TryGetProperty("holiday", out _));
        Assert.Equal(0, root.GetProperty("items").GetArrayLength());
        Assert.Equal(0, root.GetProperty("suggestions").GetArrayLength());
    }
}
