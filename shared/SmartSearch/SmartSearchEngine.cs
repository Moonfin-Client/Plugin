using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Moonfin.SmartSearch;

/// <summary>
/// Everything smart search keeps on disk and in memory, shared by every server port: the model
/// files, the library index the host fills, and the TMDB catalog index behind the Seerr row.
/// Rebuilds swap whole indexes in, so searches never see one half written.
/// </summary>
public sealed class SmartSearchEngine
{
    private const float RelativeCutoff = 0.35f;

    private readonly string _folder;
    private readonly Func<SmartModel> _model;
    private readonly SemaphoreSlim _modelGate = new SemaphoreSlim(1, 1);
    private readonly object _encoderLock = new object();
    private BertEncoder? _encoder;
    private SmartModel? _encoderModel;
    private volatile Snapshot _snapshot;

    /// <summary>The model is the one the admin picked, read again before every build and search.</summary>
    public SmartSearchEngine(string dataFolder, Func<SmartModel> model)
    {
        _folder = Path.Combine(dataFolder, "smart-search");
        _model = model;
        _snapshot = Snapshot.Create(SafeLoad(LibraryPath), SafeLoad(CatalogPath));
    }

    /// <summary>
    /// True once the picked model has built a library index. The catalog is optional, it needs a
    /// TMDB key. After the admin switches models this is false until the next build.
    /// </summary>
    public bool Ready
    {
        get
        {
            var model = _model();
            return _snapshot.Library.Entries.Count > 0 && _snapshot.Library.Model == model.Id
                && File.Exists(Path.Combine(ModelFolder(model), "model.safetensors"));
        }
    }

    public int CatalogCount => _snapshot.Catalog.Entries.Count;

    private string ModelsFolder => Path.Combine(_folder, "models");

    private string LibraryPath => Path.Combine(_folder, "library.bin");

    private string CatalogPath => Path.Combine(_folder, "catalog.bin");

    /// <summary>
    /// Downloads the picked model once and checks every file against its hash. Only one model is
    /// kept, since the multilingual one alone is 471 MB.
    /// </summary>
    public async Task EnsureModelAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var model = _model();
        await _modelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var folder = ModelFolder(model);
            Directory.CreateDirectory(folder);
            foreach (var (name, sha) in model.Files)
            {
                var path = Path.Combine(folder, name);
                if (File.Exists(path) && Sha256Of(path) == sha)
                {
                    continue;
                }

                var temp = path + ".download";
                using (var response = await http.GetAsync(model.BaseUrl + name, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var target = File.Create(temp);
                    await source.CopyToAsync(target, 81920, cancellationToken).ConfigureAwait(false);
                }

                if (Sha256Of(temp) != sha)
                {
                    File.Delete(temp);
                    throw new InvalidDataException($"Smart search model file {name} failed its hash check.");
                }

                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(temp, path);
            }

            foreach (var other in Directory.GetDirectories(ModelsFolder).Where(d => Path.GetFileName(d) != model.Id))
            {
                try
                {
                    Directory.Delete(other, true);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Windows won't delete a model a search still has open, so it goes on the next run.
                }
            }
        }
        finally
        {
            _modelGate.Release();
        }
    }

    /// <summary>Replaces the library index, reusing vectors of entries whose text didn't change.</summary>
    public void RebuildLibrary(IReadOnlyList<(SmartEntry Entry, string Text)> items, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var index = Embed(items, _snapshot.Library, progress, cancellationToken);
        index.Save(LibraryPath);
        _snapshot = Snapshot.Create(index, _snapshot.Catalog);
    }

    /// <summary>Refreshes the TMDB catalog. A fetch that comes back far smaller than before keeps the old one.</summary>
    public async Task RebuildCatalogAsync(HttpClient http, string apiKey, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var items = await TmdbCatalog.FetchAsync(http, apiKey, Path.Combine(_folder, "catalog-details.json"), progress, cancellationToken).ConfigureAwait(false);
        if (items.Count < _snapshot.Catalog.Entries.Count / 2)
        {
            throw new InvalidDataException($"TMDB returned {items.Count} titles, fewer than half of the {_snapshot.Catalog.Entries.Count} already indexed.");
        }

        var embedProgress = progress == null ? null : new Progress<double>(p => progress.Report(0.5 + 0.5 * p));
        var index = Embed(items, _snapshot.Catalog, embedProgress, cancellationToken);
        index.Save(CatalogPath);
        _snapshot = Snapshot.Create(_snapshot.Library, index);
    }

    /// <summary>The best library and catalog candidates for the text. Catalog titles already in the library are left out.</summary>
    public SmartResults Search(string text, int candidates)
    {
        var snapshot = _snapshot;
        var model = _model();
        var query = SmartQuery.Parse(text, snapshot.People, DateTime.UtcNow.Year);
        var vector = Encoder(model).Embed(model.QueryPrefix + text);
        var library = For(model, snapshot.Library).Rank(vector, query, model, true, snapshot.Votes, snapshot.Credits).Take(candidates).ToList();
        var catalog = For(model, snapshot.Catalog).Rank(vector, query, model, false)
            .Where(h => !snapshot.LibraryTitles.Contains(SmartIndex.TitleKey(h.Entry.Kind, h.Entry.TmdbId)))
            .Take(candidates)
            .ToList();

        // Anything far below the best match in either row is noise, so "breaking bad" on a server
        // without it shows the Seerr hit and no stand-ins. People named in the query score high
        // on purpose, so they don't set the bar for the titles they're in.
        var best = library.Concat(catalog).Where(h => h.Entry.Kind != SmartQuery.Person).Select(h => h.Score).DefaultIfEmpty(float.MinValue).Max();
        var floor = best - RelativeCutoff;
        return new SmartResults(query, library.Where(h => h.Score >= floor).ToList(), catalog.Where(h => h.Score >= floor).ToList());
    }

    /// <summary>Catalog titles the person is in the top cast of, most voted first, so a person's text can say what they're known for.</summary>
    public IReadOnlyList<string> KnownFor(string normalizedName, int max)
    {
        return _snapshot.KnownFor.TryGetValue(normalizedName, out var titles) ? titles.Take(max).ToList() : (IReadOnlyList<string>)Array.Empty<string>();
    }

    /// <summary>The index, or an empty one when another model built it, since its vectors can't be compared.</summary>
    private static SmartIndex For(SmartModel model, SmartIndex index) => index.Model == model.Id ? index : new SmartIndex();

    private string ModelFolder(SmartModel model) => Path.Combine(ModelsFolder, model.Id);

    private BertEncoder Encoder(SmartModel model)
    {
        lock (_encoderLock)
        {
            if (_encoderModel != model)
            {
                _encoder = BertEncoder.Load(ModelFolder(model));
                _encoderModel = model;
            }

            return _encoder!;
        }
    }

    private SmartIndex Embed(IReadOnlyList<(SmartEntry Entry, string Text)> items, SmartIndex previous, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var model = _model();
        var encoder = Encoder(model);
        var old = For(model, previous).Entries.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var done = 0;
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            // Half the cores, so a rebuild never starves playback and transcodes.
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
        };
        Parallel.For(0, items.Count, options, i =>
        {
            var (entry, text) = items[i];
            entry.Hash = SmartIndex.HashText(text);
            entry.Vector = old.TryGetValue(entry.Key, out var prior) && prior.Hash == entry.Hash
                ? prior.Vector
                : encoder.Embed(model.PassagePrefix + text);
            var n = Interlocked.Increment(ref done);
            if (n % 100 == 0)
            {
                progress?.Report((double)n / items.Count);
            }
        });

        var index = new SmartIndex { Model = model.Id };
        index.Entries.AddRange(items.Select(i => i.Entry));
        return index;
    }

    private static SmartIndex SafeLoad(string path)
    {
        try
        {
            return SmartIndex.Load(path);
        }
        catch (Exception ex) when (ex is IOException || ex is InvalidDataException)
        {
            return new SmartIndex();
        }
    }

    private static string Sha256Of(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
    }

    private sealed class Snapshot
    {
        private Snapshot(SmartIndex library, SmartIndex catalog)
        {
            Library = library;
            Catalog = catalog;
        }

        public SmartIndex Library { get; }

        public SmartIndex Catalog { get; }

        public PeopleDirectory People { get; } = new PeopleDirectory();

        public Dictionary<string, int> Votes { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

        public HashSet<string> LibraryTitles { get; } = new HashSet<string>(StringComparer.Ordinal);

        public Dictionary<string, List<string>> KnownFor { get; } = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        /// <summary>Normalized titles each person is credited on, in the library or the catalog's top cast.</summary>
        public Dictionary<string, HashSet<string>> Credits { get; } = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        private void Credit(SmartEntry e)
        {
            foreach (var p in e.People)
            {
                if (!Credits.TryGetValue(p, out var titles))
                {
                    Credits[p] = titles = new HashSet<string>(StringComparer.Ordinal);
                }

                titles.Add(e.MatchTitle);
            }
        }

        public static Snapshot Create(SmartIndex library, SmartIndex catalog)
        {
            var s = new Snapshot(library, catalog);
            foreach (var e in catalog.Entries.OrderByDescending(e => e.Votes))
            {
                e.MatchTitle = SmartQuery.NormalizeName(e.Title);
                s.People.AddTitleWords(e.MatchTitle);
                s.Credit(e);
                s.Votes[SmartIndex.TitleKey(e.Kind, e.TmdbId)] = e.Votes;
                foreach (var p in e.People)
                {
                    s.People.Add(p);
                    if (!s.KnownFor.TryGetValue(p, out var titles))
                    {
                        s.KnownFor[p] = titles = new List<string>();
                    }

                    titles.Add(e.Title);
                }
            }

            foreach (var e in library.Entries)
            {
                e.MatchTitle = SmartQuery.NormalizeName(e.Title);
                if (e.Kind != SmartQuery.Person)
                {
                    s.People.AddTitleWords(e.MatchTitle);
                    s.Credit(e);
                }
                if (e.TmdbId.Length > 0)
                {
                    s.LibraryTitles.Add(SmartIndex.TitleKey(e.Kind, e.TmdbId));
                }

                foreach (var p in e.People)
                {
                    s.People.Add(p);
                }
            }

            return s;
        }
    }
}

public sealed class SmartResults
{
    public SmartResults(SmartQuery query, List<SmartHit> library, List<SmartHit> catalog)
    {
        Query = query;
        Library = library;
        Catalog = catalog;
    }

    public SmartQuery Query { get; }

    public List<SmartHit> Library { get; }

    public List<SmartHit> Catalog { get; }
}
