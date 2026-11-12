using System.Text.Json;
using Emby.Plugins.Moonfin.Api;
using Emby.Plugins.Moonfin.Models;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// An MDBList official list sends its movies and shows in separate arrays, and a row made from
/// the list's movies or shows page keeps only that type.
/// </summary>
public class MdbListMediaTypeTests
{
    private const string Page = """
        { "movies": [ { "id": 603, "rank": 2, "title": "The Matrix", "mediatype": "movie" } ],
          "shows": [ { "id": 1399, "rank": 1, "title": "Game of Thrones", "mediatype": "show" } ] }
        """;

    [Theory]
    [InlineData("movie", "The Matrix")]
    [InlineData("show", "Game of Thrones")]
    [InlineData(null, "The Matrix,Game of Thrones")]
    [InlineData("anime", "The Matrix,Game of Thrones")]
    public void KeepsOnlyTheChosenType(string? mediatype, string expected)
    {
        using var page = JsonDocument.Parse(Page);
        var items = new List<CustomRowItem>();

        var count = CustomRowService.AppendMdbListPage(page.RootElement, items, mediatype);

        Assert.Equal(2, count);
        Assert.Equal(expected, string.Join(",", items.Select(i => i.Name)));
    }

    [Fact]
    public void APageOfOnlyTheOtherTypeStillCountsItsTitles()
    {
        using var page = JsonDocument.Parse("""{ "movies": [], "shows": [ { "id": 1399, "rank": 1, "title": "Game of Thrones" } ] }""");
        var items = new List<CustomRowItem>();

        Assert.Equal(1, CustomRowService.AppendMdbListPage(page.RootElement, items, "movie"));
        Assert.Empty(items);
    }
}
