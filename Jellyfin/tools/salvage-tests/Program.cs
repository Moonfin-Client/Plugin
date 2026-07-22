using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Moonfin.Server.Models;
using Moonfin.Server.Services;

// Assert-style harness for JsonSalvage and FileHealer. The repo has no test framework, so
// this mirrors tools/verify-plugin: run it, non-zero exit means failure.
//
//   dotnet run --project Jellyfin/tools/salvage-tests -c Release

var failures = 0;

void Check(bool condition, string name)
{
    if (!condition)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}");
    }
}

// Mirrors MoonfinSettingsService._jsonOptions.
var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

MoonfinUserSettings? Parse(string text)
{
    try
    {
        return JsonSerializer.Deserialize<MoonfinUserSettings>(text, jsonOptions);
    }
    catch
    {
        return null;
    }
}

// Mirrors the validation floor in MoonfinSettingsService.HealDataFilesAsync.
bool ValidEnvelope(string text)
{
    var env = Parse(text);
    if (env == null || env.SchemaVersion is < 1 or > 2)
    {
        return false;
    }

    return env.Global != null || env.Desktop != null || env.Mobile != null || env.Tv != null ||
        env.NeedsMigration;
}

// ─── Fixture: a full v2 envelope whose strings stress the scanner ──────────────

var envelope = new MoonfinUserSettings
{
    SchemaVersion = 2,
    LastUpdated = 1721000000000,
    LastUpdatedBy = "client-with-\"quotes\", {braces} and \\ backslashes",
    SyncEnabled = true,
    // Braces and brackets stay out of global's strings so the plain IndexOf below finds
    // global's real closing brace. The nasty strings before and after global still stress
    // the scanner across the exhaustive sweep.
    Global = new MoonfinSettingsProfile { SeerrApiKey = "key,\"quoted\",end" },
    Desktop = new MoonfinSettingsProfile { SeerrBlockNsfw = true },
    Mobile = new MoonfinSettingsProfile { SeerrEnabled = false },
    Tv = new MoonfinSettingsProfile { SeerrApiKey = "tv{}[]\\," },
};

var full = JsonSerializer.Serialize(envelope, jsonOptions);
var original = Parse(full)!;
var originalGlobal = JsonSerializer.Serialize(original.Global, jsonOptions);

// Offset just past the closing brace of "global": { ... }.
var globalStart = full.IndexOf("\"global\"", StringComparison.Ordinal);
Check(globalStart > 0, "fixture contains global");
var globalEnd = full.IndexOf('}', globalStart) + 1;

// ─── Group 1: exhaustive truncation sweep ───────────────────────────────────────

for (var i = 0; i < full.Length; i++)
{
    var cut = full.Substring(0, i);
    bool ok;
    string healed = string.Empty;
    try
    {
        ok = JsonSalvage.TrySalvage(cut, ValidEnvelope, out healed);
    }
    catch (Exception ex)
    {
        Check(false, $"offset {i}: threw {ex.GetType().Name}");
        continue;
    }

    if (ok)
    {
        Check(ValidEnvelope(healed), $"offset {i}: healed text fails floor");
    }

    if (i > globalEnd)
    {
        Check(ok, $"offset {i}: salvage failed past global");
        if (ok)
        {
            var salvagedGlobal = JsonSerializer.Serialize(Parse(healed)!.Global, jsonOptions);
            Check(salvagedGlobal == originalGlobal, $"offset {i}: global not preserved");
        }
    }
}

// Untruncated input round-trips unchanged in meaning.
{
    Check(JsonSalvage.TrySalvage(full, ValidEnvelope, out var healed), "full doc salvages");
    Check(Parse(healed)!.Tv?.SeerrApiKey == "tv{}[]\\,", "full doc keeps tv profile");
}

// ─── Group 2: edge fixtures ─────────────────────────────────────────────────────

Check(!JsonSalvage.TrySalvage(string.Empty, ValidEnvelope, out _), "empty rejected");
Check(!JsonSalvage.TrySalvage("   \n\t ", ValidEnvelope, out _), "whitespace rejected");
Check(!JsonSalvage.TrySalvage(new string('\0', 4096), ValidEnvelope, out _), "all-NUL rejected");
Check(!JsonSalvage.TrySalvage("[1,2,3]", ValidEnvelope, out _), "non-object rejected");

// NUL tail after a valid prefix parses like the plain truncation at that point.
{
    var withNulTail = full.Substring(0, globalEnd + 5) + new string('\0', 512);
    Check(
        JsonSalvage.TrySalvage(withNulTail, ValidEnvelope, out var healed) &&
        Parse(healed)!.Global != null,
        "NUL tail salvaged with global intact");
}

// BOM-prefixed, valid and truncated.
Check(JsonSalvage.TrySalvage("\uFEFF" + full, ValidEnvelope, out _), "BOM valid salvages");
Check(
    JsonSalvage.TrySalvage("\uFEFF" + full.Substring(0, globalEnd + 5), ValidEnvelope, out _),
    "BOM truncated salvages");

// Valid document followed by trailing garbage keeps every profile.
{
    Check(
        JsonSalvage.TrySalvage(full + "garbage{{{", ValidEnvelope, out var healed) &&
        Parse(healed)!.Tv != null,
        "trailing garbage stripped");
}

// Truncation inside global itself: the envelope that remains has no profile and no legacy
// fields, so the floor rejects it and the file goes to quarantine.
{
    var insideGlobal = full.Substring(0, globalStart + "\"global\"".Length + 6);
    Check(!JsonSalvage.TrySalvage(insideGlobal, ValidEnvelope, out _), "cut inside global rejected");
}

// Metadata-only envelope is functionally empty.
Check(
    !JsonSalvage.TrySalvage("{\"schemaVersion\":2,\"syncEnabled\":true}", ValidEnvelope, out _),
    "metadata-only rejected");

// Truncated v1 flat envelope passes the floor through NeedsMigration.
{
    var v1 = "{\"schemaVersion\":1,\"navbarEnabled\":true,\"mediaBarEnabled\":false,\"unfinished\":\"cut";
    Check(
        JsonSalvage.TrySalvage(v1, ValidEnvelope, out var healed) && Parse(healed)!.NeedsMigration,
        "truncated v1 salvaged via NeedsMigration");
}

// ─── Group 3: sweep integration in a temp directory ────────────────────────────

var root = Path.Combine(Path.GetTempPath(), $"moonfin-heal-test-{Guid.NewGuid():N}");
var quarantine = Path.Combine(root, "quarantine");
Directory.CreateDirectory(root);

try
{
    string NewUserFile(string contents)
    {
        var path = Path.Combine(root, $"{Guid.NewGuid()}.json");
        File.WriteAllText(path, contents);
        return path;
    }

    var healthyPath = NewUserFile(full);
    var healthyWrite = File.GetLastWriteTimeUtc(healthyPath);

    var truncatedPath = NewUserFile(full.Substring(0, globalEnd + 5));
    var emptyPath = NewUserFile(string.Empty);
    var nulPath = NewUserFile(new string('\0', 2048));

    var bakRecoveryPath = NewUserFile("{\"schemaVersion\":2,\"glo");
    File.WriteAllText(bakRecoveryPath + ".bak", full);

    var tmpPromotePath = Path.Combine(root, $"{Guid.NewGuid()}.json");
    File.WriteAllText(tmpPromotePath + ".tmp", full);

    var staleTmpPath = NewUserFile(full);
    File.WriteAllText(staleTmpPath + ".tmp", "half-written");

    // A file that is not a user guid must be ignored.
    File.WriteAllText(Path.Combine(root, "not-a-user.json"), "{broken");

    (bool, string) Salvage(string raw) =>
        (JsonSalvage.TrySalvage(raw, ValidEnvelope, out var healed), healed);

    var gate = new SemaphoreSlim(1, 1);
    var summary = await FileHealer.HealDirectoryAsync(
        root,
        quarantine,
        gate,
        text => ValidEnvelope(text),
        Salvage,
        CancellationToken.None);

    Check(summary.Scanned == 7, $"scanned 7, got {summary.Scanned}");
    Check(summary.Healthy == 2, $"healthy 2, got {summary.Healthy}");
    Check(summary.Salvaged == 1, $"salvaged 1, got {summary.Salvaged}");
    Check(summary.RecoveredFromBackup == 1, $"bak-recovered 1, got {summary.RecoveredFromBackup}");
    Check(summary.TmpPromoted == 1, $"tmp-promoted 1, got {summary.TmpPromoted}");
    Check(summary.Quarantined == 2, $"quarantined 2, got {summary.Quarantined}");
    Check(summary.Errors == 0, $"errors 0, got {summary.Errors}");

    Check(File.ReadAllText(healthyPath) == full, "healthy file byte-identical");
    Check(File.GetLastWriteTimeUtc(healthyPath) == healthyWrite, "healthy mtime unchanged");

    var salvaged = Parse(File.ReadAllText(truncatedPath));
    Check(salvaged?.Global != null, "salvaged file has global");
    Check(salvaged!.Global!.SeerrApiKey == "key,\"quoted\",end", "salvaged global content intact");

    Check(Parse(File.ReadAllText(bakRecoveryPath))?.Tv != null, "bak recovery restored full envelope");
    Check(Parse(File.ReadAllText(tmpPromotePath))?.Tv != null, "tmp promoted to main");
    Check(!File.Exists(staleTmpPath + ".tmp"), "stale tmp removed from data dir");
    Check(File.ReadAllText(staleTmpPath) == full, "healthy main beside stale tmp untouched");

    Check(!File.Exists(emptyPath), "empty file moved out");
    Check(!File.Exists(nulPath), "NUL file moved out");

    var quarantined = Directory.Exists(quarantine)
        ? Directory.GetFiles(quarantine)
        : Array.Empty<string>();

    // empty + NUL + stale tmp + pre-salvage copy + pre-recovery copy.
    Check(quarantined.Length == 5, $"quarantine holds 5 files, got {quarantined.Length}");
    Check(File.Exists(Path.Combine(root, "not-a-user.json")), "non-guid file untouched");
}
finally
{
    try
    {
        Directory.Delete(root, recursive: true);
    }
    catch
    {
        // Best effort scratch cleanup.
    }
}

if (failures > 0)
{
    Console.Error.WriteLine($"{failures} failure(s)");
    return 1;
}

Console.WriteLine("All salvage tests passed");
return 0;
