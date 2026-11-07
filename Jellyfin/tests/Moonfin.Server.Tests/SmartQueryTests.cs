using System;
using System.Linq;
using Moonfin.SmartSearch;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// Smart search reads years, kinds and names out of a sentence with rules, so these pin down
/// the phrasings people actually type.
/// </summary>
public sealed class SmartQueryTests
{
    private static readonly DateTime Today = new DateTime(2026, 11, 6);

    private static PeopleDirectory People(params string[] names)
    {
        var people = new PeopleDirectory();
        foreach (var name in names)
        {
            people.Add(SmartQuery.NormalizeName(name));
        }

        return people;
    }

    [Theory]
    [InlineData("90s movie about a board game", 1990, 1999)]
    [InlineData("matt damon movies from the 2000s", 2000, 2009)]
    [InlineData("late 80s horror", 1986, 1989)]
    [InlineData("early 2010s show", 2010, 2013)]
    [InlineData("a '70s cop show", 1970, 1979)]
    [InlineData("movie from 1995", 1995, 1995)]
    [InlineData("westerns before 1960", 1800, 1959)]
    public void ReadsYearRanges(string text, int min, int max)
    {
        var query = SmartQuery.Parse(text, People(), Today);

        Assert.Equal(min, query.YearMin);
        Assert.Equal(max, query.YearMax);
    }

    [Theory]
    [InlineData("new sci-fi movies", 20251106, 20261106)]
    [InlineData("latest marvel movie", 20251106, 20261106)]
    [InlineData("brand new horror shows", 20251106, 20261106)]
    [InlineData("upcoming horror movies", 20261107, 99991231)]
    [InlineData("sci-fi coming soon", 20261107, 99991231)]
    [InlineData("star wars a new hope", 0, 0)]
    [InlineData("new york crime shows", 0, 0)]
    [InlineData("new movies from the 90s", 0, 0)]
    public void ReadsHowRecent(string text, int min, int max)
    {
        var query = SmartQuery.Parse(text, People(), Today);

        Assert.Equal((min, max), (query.DateMin, query.DateMax));
    }

    [Fact]
    public void NewAndUpcomingLeaveOutEverythingElse()
    {
        var index = new SmartIndex { Model = SmartModel.English.Id };
        foreach (var (key, kind, year, released) in new[]
        {
            ("old", SmartQuery.Movie, 1986, 19860718), ("new", SmartQuery.Movie, 2026, 20260320), ("soon", SmartQuery.Movie, 2026, 20261218),
            ("library", SmartQuery.Movie, 2026, 0), ("long running show", SmartQuery.Series, 1989, 0), ("no date", SmartQuery.Movie, 0, 0), ("person", SmartQuery.Person, 0, 0),
        })
        {
            index.Entries.Add(new SmartEntry { Key = key, Kind = kind, Title = key, YearMin = year, YearMax = kind == SmartQuery.Series ? 2026 : year, Released = released, Vector = new float[384] });
        }

        string[] Kept(string text) => index.Rank(new float[384], SmartQuery.Parse(text, People(), Today), SmartModel.English, false)
            .Select(h => h.Entry.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "library", "new" }, Kept("new sci-fi movies"));
        Assert.Equal(new[] { "soon" }, Kept("upcoming sci-fi movies"));
        Assert.Equal(7, Kept("sci-fi movies").Length);
    }

    [Fact]
    public void TwentiesMeanThisCenturyOnceTheDecadeHasStarted()
    {
        Assert.Equal(2020, SmartQuery.Parse("20s sitcom", People(), Today).YearMin);
        Assert.Equal(1930, SmartQuery.Parse("30s gangster film", People(), Today).YearMin);
    }

    [Fact]
    public void ReadsKinds()
    {
        Assert.Equal(new[] { SmartQuery.Movie }, SmartQuery.Parse("matt damon movies", People(), Today).Kinds);
        Assert.Contains(SmartQuery.MusicArtist, SmartQuery.Parse("electronic music duo from the 90s", People(), Today).Kinds);
        Assert.Empty(SmartQuery.Parse("kid who sees dead people", People(), Today).Kinds);
    }

    [Fact]
    public void FindsNamesWithATypo()
    {
        var people = People("Jim Carrey", "Tom Hanks", "Matt Damon");

        Assert.Equal(new[] { "jim carrey" }, SmartQuery.Parse("jim carey movie about a mask", people, Today).People);
        Assert.Equal(new[] { "matt damon" }, SmartQuery.Parse("matt damon movies from the 2000s", people, Today).People);
        Assert.Empty(SmartQuery.Parse("tom stranded on an island", people, Today).People);
    }

    [Fact]
    public void TellsANameFromAPlotWithAName()
    {
        var people = People("Tom Hanks");

        Assert.True(SmartQuery.Parse("tom hanks", people, Today).NameOnly);
        Assert.False(SmartQuery.Parse("tom hanks stranded on an island", people, Today).NameOnly);
    }

    [Fact]
    public void IgnoresASurnameThatIsAlsoATitleWord()
    {
        var people = People("Eric Christmas", "Eric Christmas", "Eric Christmas");
        people.AddTitleWords(SmartQuery.NormalizeName("A Christmas Story"));

        Assert.Empty(SmartQuery.Parse("christmas movies from the 80s", people, Today).People);
    }

    [Fact]
    public void WhoPlayedAsksForAPerson()
    {
        Assert.Contains(SmartQuery.Person, SmartQuery.Parse("the guy who played iron man", People(), Today).Kinds);
        Assert.DoesNotContain(SmartQuery.Person, SmartQuery.Parse("movie where kids play a board game", People(), Today).Kinds);
    }

    [Fact]
    public void FindsAWellKnownSurnameOnItsOwn()
    {
        var people = People("Leonardo DiCaprio", "Leonardo DiCaprio", "Leonardo DiCaprio", "Tom Hanks");

        Assert.Equal(new[] { "leonardo dicaprio" }, SmartQuery.Parse("dicaprio detective on an island asylum", people, Today).People);
    }
}
