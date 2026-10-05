using System.Text.Json;
using Moonfin.Server.Models;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// Pins the JSON the clients read for the seasonal row and for the rating the custom rows
/// now carry, since the clients parse these names by hand.
/// </summary>
public sealed class SeasonalRowContractTests
{
    [Fact]
    public void TheRowSerializesWithTheNamesTheClientsRead()
    {
        var response = new SeasonalRowResponse
        {
            Holiday = "christmas",
            Country = "US",
            Items = [new { Id = "abc", Name = "Elf" }],
            Suggestions = [new CustomRowItem { Id = 10719, Name = "Elf", Type = "Movie", OfficialRating = "PG", Overview = "Buddy", Rating = 6.7, Genres = ["Comedy"], RunTimeTicks = 97 * TimeSpan.TicksPerMinute }]
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(response));
        var root = doc.RootElement;

        Assert.Equal("christmas", root.GetProperty("holiday").GetString());
        Assert.Equal("US", root.GetProperty("country").GetString());
        Assert.Equal("Elf", root.GetProperty("items")[0].GetProperty("Name").GetString());
        var suggestion = root.GetProperty("suggestions")[0];
        Assert.Equal(10719, suggestion.GetProperty("id").GetInt64());
        Assert.Equal("PG", suggestion.GetProperty("officialRating").GetString());
        Assert.Equal("Movie", suggestion.GetProperty("type").GetString());
        Assert.Equal("Buddy", suggestion.GetProperty("overview").GetString());
        Assert.Equal(6.7, suggestion.GetProperty("rating").GetDouble());
        Assert.Equal("Comedy", suggestion.GetProperty("genres")[0].GetString());
        Assert.Equal(97 * TimeSpan.TicksPerMinute, suggestion.GetProperty("runTimeTicks").GetInt64());
    }

    [Fact]
    public void AnEmptyRowStillCarriesEveryKey()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new SeasonalRowResponse()));
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("holiday").ValueKind);
        Assert.Equal(0, root.GetProperty("items").GetArrayLength());
        Assert.Equal(0, root.GetProperty("suggestions").GetArrayLength());
    }

    [Fact]
    public void ACustomRowItemWithoutARatingWritesNullNotNothing()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new CustomRowItem { Name = "Unrated" }));

        Assert.True(doc.RootElement.TryGetProperty("officialRating", out var rating));
        Assert.Equal(JsonValueKind.Null, rating.ValueKind);
    }
}
