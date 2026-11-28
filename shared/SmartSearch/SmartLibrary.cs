using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Moonfin.SmartSearch;

/// <summary>
/// A server's library in the form the index takes. Hosts add titles, albums, artists and books
/// as they read them, albums before artists since an artist's text lists their albums. The
/// people credited on titles become entries of their own when the list is built.
/// </summary>
public sealed class SmartLibrary
{
    private const int CastPerTitle = 10;

    private readonly List<(Func<SmartEntry> Entry, string Text)> _items = new List<(Func<SmartEntry>, string)>();
    private readonly Dictionary<string, (string Name, List<string> Acted, List<string> Directed)> _credits =
        new Dictionary<string, (string, List<string>, List<string>)>(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(string Name, int Year, List<string> Genres)>> _albumsByArtist =
        new Dictionary<string, List<(string, int, List<string>)>>(StringComparer.Ordinal);

    public void AddTitle(
        string id,
        bool isSeries,
        string title,
        int year,
        int? endYear,
        string? tmdbId,
        IEnumerable<string>? genres,
        IEnumerable<string>? tags,
        IEnumerable<string> actors,
        IEnumerable<string> directors,
        string? overview)
    {
        var cast = actors.Take(CastPerTitle).ToList();
        var directing = directors.ToList();
        var text = new StringBuilder(SmartIndex.TitleText(title, year, isSeries ? "TV series" : "Movie", genres, tags, cast, overview));
        SmartIndex.AppendList(text, " Directed by ", directing, 2);
        var people = cast.Concat(directing).Select(SmartQuery.NormalizeName).Distinct().ToArray();
        _items.Add((() => new SmartEntry
        {
            Key = id,
            Kind = isSeries ? SmartQuery.Series : SmartQuery.Movie,
            Title = title,
            YearMin = year,
            YearMax = endYear ?? year,
            TmdbId = tmdbId ?? string.Empty,
            People = people,
        }, text.ToString()));

        foreach (var name in cast)
        {
            Credit(name).Acted.Add(title);
        }

        foreach (var name in directing)
        {
            Credit(name).Directed.Add(title);
        }
    }

    public void AddAlbum(string id, string name, int year, IReadOnlyList<string> artists, IEnumerable<string>? genres, string? overview)
    {
        var genreList = genres?.ToList() ?? new List<string>();
        AddWork(id, SmartQuery.MusicAlbum, "Album", name, year, artists, genreList, overview);
        foreach (var artist in artists)
        {
            var key = SmartQuery.NormalizeName(artist);
            if (!_albumsByArtist.TryGetValue(key, out var albums))
            {
                _albumsByArtist[key] = albums = new List<(string, int, List<string>)>();
            }

            albums.Add((name, year, genreList));
        }
    }

    public void AddArtist(string id, string name, IEnumerable<string>? genres, string? overview)
    {
        var key = SmartQuery.NormalizeName(name);
        var albums = _albumsByArtist.TryGetValue(key, out var list) ? list : new List<(string Name, int Year, List<string> Genres)>();
        var years = albums.Select(a => a.Year).Where(y => y > 0).ToList();
        var text = new StringBuilder(name).Append(". Music artist.");
        SmartIndex.AppendList(text, " ", (genres ?? Array.Empty<string>()).Concat(albums.SelectMany(a => a.Genres)).Distinct(StringComparer.OrdinalIgnoreCase), 5);
        SmartIndex.AppendList(text, " Albums: ", albums.OrderBy(a => a.Year).Select(a => a.Year > 0 ? $"{a.Name} ({a.Year})" : a.Name), 8);
        AppendOverview(text, overview);
        _items.Add((() => new SmartEntry
        {
            Key = id,
            Kind = SmartQuery.MusicArtist,
            Title = name,
            YearMin = years.Count > 0 ? years.Min() : 0,
            YearMax = years.Count > 0 ? years.Max() : 0,
            People = new[] { key },
        }, text.ToString()));
    }

    public void AddBook(string id, string name, int year, IEnumerable<string> authors, IEnumerable<string>? genres, string? overview) =>
        AddWork(id, SmartQuery.Book, "Book", name, year, authors.Take(3).ToList(), genres, overview);

    /// <summary>
    /// Fresh entries for everything added, plus one per credited person. A person's text names
    /// what the catalog says they're known for, which is why this can run again once it changes.
    /// </summary>
    public List<(SmartEntry Entry, string Text)> Build(Func<string, int, IReadOnlyList<string>> knownFor)
    {
        var result = _items.Select(i => (i.Entry(), i.Text)).ToList();
        foreach (var (name, acted, directed) in _credits.Values)
        {
            var key = SmartQuery.NormalizeName(name);
            var text = new StringBuilder(name).Append('.');
            SmartIndex.AppendList(text, " Actor in ", acted, 10);
            SmartIndex.AppendList(text, " Director of ", directed, 10);
            SmartIndex.AppendList(text, " Known for ", knownFor(key, 6).Except(acted), 6);
            result.Add((new SmartEntry
            {
                Key = "person:" + name,
                Kind = SmartQuery.Person,
                Title = name,
                People = new[] { key },
            }, text.ToString()));
        }

        return result;
    }

    private void AddWork(string id, string kind, string kindWord, string name, int year, IReadOnlyList<string> by, IEnumerable<string>? genres, string? overview)
    {
        var text = new StringBuilder(name);
        if (year > 0)
        {
            text.Append(" (").Append(year).Append(')');
        }

        text.Append(". ").Append(kindWord).Append('.');
        SmartIndex.AppendList(text, " By ", by, 3);
        SmartIndex.AppendList(text, " ", genres, 4);
        AppendOverview(text, overview);
        var people = by.Select(SmartQuery.NormalizeName).ToArray();
        _items.Add((() => new SmartEntry
        {
            Key = id,
            Kind = kind,
            Title = name,
            YearMin = year,
            YearMax = year,
            People = people,
        }, text.ToString()));
    }

    private (string Name, List<string> Acted, List<string> Directed) Credit(string name)
    {
        var key = SmartQuery.NormalizeName(name);
        if (!_credits.TryGetValue(key, out var credit))
        {
            _credits[key] = credit = (name, new List<string>(), new List<string>());
        }

        return credit;
    }

    private static void AppendOverview(StringBuilder text, string? overview)
    {
        if (!string.IsNullOrWhiteSpace(overview))
        {
            text.Append(' ').Append(overview!.Trim());
        }
    }
}
