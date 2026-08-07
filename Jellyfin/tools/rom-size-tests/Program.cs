using System.IO.Compression;
using Moonfin.Server.Services;

// Checks the archived-ROM size path. A HEAD reports GetExtractedRomLength while the GET sends
// ExtractRomFromArchive, and a client compares the two to decide whether its cached copy is
// still good, so they have to agree exactly. Assert-style, non-zero exit means failure.
//
//   dotnet run --project Jellyfin/tools/rom-size-tests -c Release

var failures = 0;

void Check(bool condition, string name)
{
    if (!condition)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}");
    }
}

var work = Directory.CreateTempSubdirectory("rom_size_tests");

// A trailing slash writes a directory entry.
string WriteZip(string name, params (string Entry, int Bytes)[] entries)
{
    var path = Path.Combine(work.FullName, name);
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var (entry, bytes) in entries)
    {
        if (entry.EndsWith('/'))
        {
            zip.CreateEntry(entry);
            continue;
        }

        using var stream = zip.CreateEntry(entry, CompressionLevel.Optimal).Open();
        // Zeroes compress to almost nothing, so a size read off the compressed field would
        // stand out rather than happen to match.
        stream.Write(new byte[bytes]);
    }

    return path;
}

void CheckAgrees(string path, string name)
{
    var extracted = GamesService.ExtractRomFromArchive(path);
    var reported = GamesService.GetExtractedRomLength(path);
    Check(extracted != null, $"{name}: extracts");
    Check(reported == extracted?.Length, $"{name}: reported {reported} vs extracted {extracted?.Length}");
}

// The entry with a known ROM extension wins even when another entry is larger.
CheckAgrees(WriteZip("rom.zip", ("readme.txt", 4096), ("game.nes", 512)), "rom extension");

// With nothing recognizable, the largest entry is served.
CheckAgrees(WriteZip("largest.zip", ("a.dat", 128), ("b.dat", 8192)), "largest entry");

// Real ROM zips carry directory entries, so the fixture has one too.
CheckAgrees(
    WriteZip("nested.zip", ("disc/", 0), ("disc/game.sfc", 2048), ("disc/notes.txt", 16)),
    "nested rom");

// A file that only looks like an archive reports no size rather than throwing.
var broken = Path.Combine(work.FullName, "broken.zip");
File.WriteAllText(broken, "not really a zip");
Check(GamesService.GetExtractedRomLength(broken) == null, "unreadable archive reports null");

work.Delete(recursive: true);

Console.WriteLine(failures == 0 ? "PASS" : $"FAIL ({failures})");
return failures == 0 ? 0 : 1;
