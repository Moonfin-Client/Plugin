using Moonfin.SmartSearch;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// Smart search reads years, kinds and names out of a sentence with rules, so these pin down
/// the phrasings people actually type.
/// </summary>
public sealed class SmartQueryTests
{
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
        var query = SmartQuery.Parse(text, People(), 2026);

        Assert.Equal(min, query.YearMin);
        Assert.Equal(max, query.YearMax);
    }

    [Fact]
    public void TwentiesMeanThisCenturyOnceTheDecadeHasStarted()
    {
        Assert.Equal(2020, SmartQuery.Parse("20s sitcom", People(), 2026).YearMin);
        Assert.Equal(1930, SmartQuery.Parse("30s gangster film", People(), 2026).YearMin);
    }

    [Fact]
    public void ReadsKinds()
    {
        Assert.Equal(new[] { SmartQuery.Movie }, SmartQuery.Parse("matt damon movies", People(), 2026).Kinds);
        Assert.Contains(SmartQuery.MusicArtist, SmartQuery.Parse("electronic music duo from the 90s", People(), 2026).Kinds);
        Assert.Empty(SmartQuery.Parse("kid who sees dead people", People(), 2026).Kinds);
    }

    [Fact]
    public void FindsNamesWithATypo()
    {
        var people = People("Jim Carrey", "Tom Hanks", "Matt Damon");

        Assert.Equal(new[] { "jim carrey" }, SmartQuery.Parse("jim carey movie about a mask", people, 2026).People);
        Assert.Equal(new[] { "matt damon" }, SmartQuery.Parse("matt damon movies from the 2000s", people, 2026).People);
        Assert.Empty(SmartQuery.Parse("tom stranded on an island", people, 2026).People);
    }

    [Fact]
    public void TellsANameFromAPlotWithAName()
    {
        var people = People("Tom Hanks");

        Assert.True(SmartQuery.Parse("tom hanks", people, 2026).NameOnly);
        Assert.False(SmartQuery.Parse("tom hanks stranded on an island", people, 2026).NameOnly);
    }

    [Fact]
    public void IgnoresASurnameThatIsAlsoATitleWord()
    {
        var people = People("Eric Christmas", "Eric Christmas", "Eric Christmas");
        people.AddTitleWords(SmartQuery.NormalizeName("A Christmas Story"));

        Assert.Empty(SmartQuery.Parse("christmas movies from the 80s", people, 2026).People);
    }

    [Fact]
    public void WhoPlayedAsksForAPerson()
    {
        Assert.Contains(SmartQuery.Person, SmartQuery.Parse("the guy who played iron man", People(), 2026).Kinds);
        Assert.DoesNotContain(SmartQuery.Person, SmartQuery.Parse("movie where kids play a board game", People(), 2026).Kinds);
    }

    [Fact]
    public void FindsAWellKnownSurnameOnItsOwn()
    {
        var people = People("Leonardo DiCaprio", "Leonardo DiCaprio", "Leonardo DiCaprio", "Tom Hanks");

        Assert.Equal(new[] { "leonardo dicaprio" }, SmartQuery.Parse("dicaprio detective on an island asylum", people, 2026).People);
    }
}
