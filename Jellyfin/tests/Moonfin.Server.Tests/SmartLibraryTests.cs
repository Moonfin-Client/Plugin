using System.Collections.Generic;
using System.Linq;
using Moonfin.SmartSearch;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// The text each library entry is embedded as. A rebuild only embeds entries whose text changed,
/// so these also guard against changes that would quietly redo every vector.
/// </summary>
public sealed class SmartLibraryTests
{
    private static readonly IReadOnlyList<string> NoTitles = new string[0];

    [Fact]
    public void TitlesAndTheirPeopleGetTheirOwnTexts()
    {
        var library = new SmartLibrary();
        library.AddTitle("1", false, "Cast Away", 2000, null, "8358", new[] { "Drama" }, new[] { "island" }, new[] { "Tom Hanks" }, new[] { "Robert Zemeckis" }, "Stranded.");

        var built = library.Build((name, max) => name == "tom hanks" ? new[] { "Big", "Cast Away" } : NoTitles);

        var title = built.Single(b => b.Entry.Key == "1");
        Assert.Equal("Cast Away (2000). Movie. Drama. island. Starring Tom Hanks. Stranded. Directed by Robert Zemeckis.", title.Text);
        Assert.Equal(new[] { "tom hanks", "robert zemeckis" }, title.Entry.People);
        Assert.Equal("Tom Hanks. Actor in Cast Away. Known for Big.", built.Single(b => b.Entry.Key == "person:Tom Hanks").Text);
        Assert.Equal("Robert Zemeckis. Director of Cast Away.", built.Single(b => b.Entry.Key == "person:Robert Zemeckis").Text);
    }

    [Fact]
    public void AnArtistListsTheirAlbumsAndTakesTheirYears()
    {
        var library = new SmartLibrary();
        library.AddAlbum("a2", "Discovery", 2001, new[] { "Daft Punk" }, new[] { "House" }, null);
        library.AddAlbum("a1", "Homework", 1997, new[] { "Daft Punk" }, new[] { "Electronic" }, null);
        library.AddArtist("d", "Daft Punk", null, "French duo.");

        var built = library.Build((_, _) => NoTitles);

        Assert.Equal("Discovery (2001). Album. By Daft Punk. House.", built.Single(b => b.Entry.Key == "a2").Text);
        var artist = built.Single(b => b.Entry.Key == "d");
        Assert.Equal("Daft Punk. Music artist. House, Electronic. Albums: Homework (1997), Discovery (2001). French duo.", artist.Text);
        Assert.Equal((1997, 2001), (artist.Entry.YearMin, artist.Entry.YearMax));
    }

    [Fact]
    public void EachBuildMakesFreshEntries()
    {
        var library = new SmartLibrary();
        library.AddBook("b", "Dune", 1965, new[] { "Frank Herbert" }, null, null);

        var first = library.Build((_, _) => NoTitles).Single().Entry;
        var second = library.Build((_, _) => NoTitles).Single().Entry;

        Assert.NotSame(first, second);
        Assert.Equal(first.People, second.People);
    }
}
