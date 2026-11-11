using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Moonfin.SmartSearch;

namespace Moonfin.SmartSearchHelper;

/// <summary>
/// Runs the smart search engine for a Go host. Each line on stdin is a request
/// {"id", "op", "model", ...} and each answer is one line on stdout, {"id", "ok"} or
/// {"id", "error"}. Requests run side by side, so a search answers while a rebuild runs.
/// </summary>
public static class Program
{
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly object WriteLock = new object();
    private static readonly StreamWriter Output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
    private static volatile SmartModel _model = SmartModel.English;
    private static SmartSearchEngine _engine = null!;

    public static async Task Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: moonbase-smartsearch <data folder>");
            Environment.Exit(2);
        }

        _engine = new SmartSearchEngine(args[0], () => _model);
        var running = new List<Task>();
        string? line;
        while ((line = await Console.In.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            var request = line;
            running.Add(Task.Run(() => HandleAsync(request)));
            running.RemoveAll(t => t.IsCompleted);
        }

        await Task.WhenAll(running).ConfigureAwait(false);
    }

    private static async Task HandleAsync(string line)
    {
        JsonNode? id = null;
        try
        {
            var request = JsonNode.Parse(line)!.AsObject();
            id = request["id"]?.DeepClone();
            _model = SmartModel.Named((string?)request["model"]);
            var ok = await RunAsync((string?)request["op"], request).ConfigureAwait(false);
            Write(new JsonObject { ["id"] = id, ["ok"] = ok });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Write(new JsonObject { ["id"] = id, ["error"] = ex.Message });
        }
    }

    private static async Task<JsonNode> RunAsync(string? op, JsonObject request)
    {
        switch (op)
        {
            case "status":
                return new JsonObject { ["ready"] = _engine.Ready };
            case "ensureModel":
                await _engine.EnsureModelAsync(Http, CancellationToken.None).ConfigureAwait(false);
                return new JsonObject();
            case "rebuildLibrary":
            {
                var items = LibraryFrom(request["library"]!.AsObject()).Build(_engine.KnownFor);
                var watch = Stopwatch.StartNew();
                _engine.RebuildLibrary(items, null, CancellationToken.None);
                return new JsonObject { ["count"] = items.Count, ["seconds"] = (int)watch.Elapsed.TotalSeconds };
            }

            case "rebuildCatalog":
            {
                var watch = Stopwatch.StartNew();
                await _engine.RebuildCatalogAsync(Http, (string)request["tmdbApiKey"]!, null, CancellationToken.None).ConfigureAwait(false);
                return new JsonObject { ["count"] = _engine.CatalogCount, ["seconds"] = (int)watch.Elapsed.TotalSeconds };
            }

            case "search":
                return Search((string)request["text"]!, (int?)request["candidates"] ?? 120);
            default:
                throw new InvalidOperationException($"Unknown op {op}");
        }
    }

    private static JsonObject Search(string text, int candidates)
    {
        var results = _engine.Search(text, candidates);
        var library = new JsonArray();
        foreach (var hit in results.Library)
        {
            library.Add((JsonNode)new JsonObject
            {
                ["key"] = hit.Entry.Key,
                ["kind"] = hit.Entry.Kind,
                ["title"] = hit.Entry.Title,
                ["year"] = hit.Entry.YearMin,
                ["score"] = hit.Score,
                ["similarity"] = hit.Similarity,
            });
        }

        var catalog = new JsonArray();
        foreach (var hit in results.Catalog)
        {
            if (hit.Entry.Payload.Length == 0)
            {
                continue;
            }

            catalog.Add((JsonNode)new JsonObject
            {
                ["payload"] = JsonNode.Parse(hit.Entry.Payload),
                ["score"] = hit.Score,
                ["similarity"] = hit.Similarity,
            });
        }

        var q = results.Query;
        return new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["yearMin"] = q.YearMin,
                ["yearMax"] = q.YearMax,
                ["kinds"] = new JsonArray(q.Kinds.Select(k => (JsonNode?)k).ToArray()),
                ["people"] = new JsonArray(q.People.Select(p => (JsonNode?)p).ToArray()),
            },
            ["library"] = library,
            ["catalog"] = catalog,
        };
    }

    private static SmartLibrary LibraryFrom(JsonObject json)
    {
        var library = new SmartLibrary();
        foreach (var t in Items(json, "titles"))
        {
            library.AddTitle(
                (string)t["id"]!,
                (bool?)t["series"] ?? false,
                (string)t["title"]!,
                (int?)t["year"] ?? 0,
                null,
                (string?)t["tmdbId"],
                Strings(t, "genres"),
                null,
                Strings(t, "actors"),
                Strings(t, "directors"),
                (string?)t["overview"]);
        }

        // Albums go before artists, since an artist's text lists their albums.
        foreach (var a in Items(json, "albums"))
        {
            library.AddAlbum((string)a["id"]!, (string)a["title"]!, (int?)a["year"] ?? 0, Strings(a, "artists"), Strings(a, "genres"), (string?)a["overview"]);
        }

        foreach (var a in Items(json, "artists"))
        {
            library.AddArtist((string)a["id"]!, (string)a["name"]!, Strings(a, "genres"), (string?)a["overview"]);
        }

        return library;
    }

    private static IEnumerable<JsonObject> Items(JsonObject json, string name) =>
        (json[name] as JsonArray ?? new JsonArray()).OfType<JsonObject>();

    private static string[] Strings(JsonObject json, string name) =>
        (json[name] as JsonArray ?? new JsonArray()).Select(v => (string?)v).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToArray();

    private static void Write(JsonObject message)
    {
        var line = message.ToJsonString();
        lock (WriteLock)
        {
            Output.WriteLine(line);
        }
    }
}
