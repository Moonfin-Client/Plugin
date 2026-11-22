using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Moonfin.Romm;

/// <summary>
/// Reads RomM's platform and ROM listings into the models Moonfin serves. Anything Moonfin can't
/// play is dropped here: platforms without a core, games missing from RomM's disk, and games with
/// no file the core accepts.
/// </summary>
public static class RommParser
{
    public static List<RommPlatform> ParsePlatforms(JsonElement platforms)
    {
        var result = new List<RommPlatform>();
        if (platforms.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var platform in platforms.EnumerateArray())
        {
            var id = Int(platform, "id");
            var slug = Text(platform, "slug");
            var core = RommPlatformCores.CoreFor(slug);
            var fsSlug = Text(platform, "fs_slug");
            if (id == null || core == null || fsSlug == null || Bool(platform, "missing_from_fs"))
            {
                continue;
            }

            // Two RomM folders can map to the same system, so the second one adds its folder name.
            var systemId = RommPlatformCores.ShortNameFor(slug)!;
            if (!usedIds.Add(systemId))
            {
                systemId = systemId + " (" + fsSlug + ")";
                usedIds.Add(systemId);
            }

            var parsed = new RommPlatform
            {
                Id = id.Value,
                SystemId = systemId,
                Name = Text(platform, "display_name") ?? Text(platform, "custom_name") ?? Text(platform, "name") ?? systemId,
                Core = core,
            };

            foreach (var firmware in Items(platform, "firmware"))
            {
                var firmwareId = Int(firmware, "id");
                var fileName = Text(firmware, "file_name");
                if (firmwareId != null && fileName != null && !Bool(firmware, "missing_from_fs"))
                {
                    parsed.Firmware.Add(new RommFile
                    {
                        Id = firmwareId.Value,
                        FileName = fileName,
                        SizeBytes = Long(firmware, "file_size_bytes") ?? 0,
                    });
                }
            }

            result.Add(parsed);
        }

        return result;
    }

    public static List<RommGame> ParseRoms(
        JsonElement items,
        IReadOnlyDictionary<int, RommPlatform> platforms,
        RommFileRules rules)
    {
        var result = new List<RommGame>();
        if (items.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var rom in items.EnumerateArray())
        {
            var game = ParseRom(rom, platforms, rules);
            if (game != null)
            {
                result.Add(game);
            }
        }

        return result;
    }

    private static RommGame? ParseRom(JsonElement rom, IReadOnlyDictionary<int, RommPlatform> platforms, RommFileRules rules)
    {
        var id = Int(rom, "id");
        var platformId = Int(rom, "platform_id");
        if (id == null || platformId == null || Bool(rom, "missing_from_fs") ||
            !platforms.TryGetValue(platformId.Value, out var platform))
        {
            return null;
        }

        var game = new RommGame
        {
            Id = id.Value,
            PlatformId = platformId.Value,
            Title = Text(rom, "name") ?? Text(rom, "fs_name_no_ext") ?? Text(rom, "fs_name") ?? id.Value.ToString(CultureInfo.InvariantCulture),
            CoverPath = Text(rom, "path_cover_large"),
            ScreenshotPath = Strings(rom, "merged_screenshots").FirstOrDefault(),
            UpdatedAt = Text(rom, "updated_at") ?? string.Empty,
            CreatedAt = Text(rom, "created_at"),
            Summary = Text(rom, "summary"),
            Region = Join(Strings(rom, "regions")),
        };

        if (!PickRom(rom, platform.Core, rules, game))
        {
            return null;
        }

        if (rom.TryGetProperty("metadatum", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            game.Genre = Join(Strings(meta, "genres"));
            game.Developer = Join(Strings(meta, "developers"));
            game.Publisher = Join(Strings(meta, "publishers"));
            game.Franchise = Strings(meta, "franchises").FirstOrDefault();
            game.Players = MostPlayers(Text(meta, "player_count"));
            game.Year = ReleaseYear(Long(meta, "first_release_date"));
            game.Rating = FiveStarRating(Double(meta, "average_rating"));
        }

        return game;
    }

    /// <summary>
    /// Chooses the file to stream. A RomM game folder often holds extras next to the ROM (a
    /// readme, a cover), so the pick follows the folder-library rule: a file the core plays,
    /// a plain ROM ahead of an archive, then the largest.
    /// </summary>
    private static bool PickRom(JsonElement rom, string core, RommFileRules rules, RommGame game)
    {
        JsonElement? chosen = null;
        var chosenIsArchive = false;
        long chosenSize = -1;
        int? sheetId = null;
        string? sheetName = null;
        var setIds = new List<int>();
        long setSize = 0;
        foreach (var file in Items(rom, "files"))
        {
            var name = Text(file, "file_name");
            var category = Text(file, "category");
            var fileId = Int(file, "id");
            if (name == null || fileId == null ||
                (category != null && !string.Equals(category, "game", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            setIds.Add(fileId.Value);
            setSize += Long(file, "file_size_bytes") ?? 0;
            if (sheetId == null && rules.IsDiscSheet(core, name))
            {
                sheetId = fileId;
                sheetName = name;
            }

            if (!rules.IsPlayableForCore(core, name))
            {
                continue;
            }

            var isArchive = rules.IsArchive(name);
            var size = Long(file, "file_size_bytes") ?? 0;
            var better = chosen == null ||
                (chosenIsArchive && !isArchive) ||
                (chosenIsArchive == isArchive && size > chosenSize);
            if (better)
            {
                chosen = file;
                chosenIsArchive = isArchive;
                chosenSize = size;
            }
        }

        // A .cue needs its tracks and an .m3u its discs, so RomM is asked for every file of the
        // game at once, which it sends as one zip the way its own player gets it.
        if (sheetId != null && setIds.Count > 1)
        {
            var setName = Text(rom, "fs_name") ?? Path.GetFileNameWithoutExtension(sheetName!);
            game.Rom = new RommFile { Id = sheetId.Value, FileName = setName };
            game.FileIds = setIds;
            game.ServedName = setName + ".zip";
            game.ServedSizeBytes = setSize;
            return true;
        }

        if (chosen == null)
        {
            return false;
        }

        var picked = chosen.Value;
        game.Rom = new RommFile
        {
            Id = Int(picked, "id")!.Value,
            FileName = Text(picked, "file_name")!,
            SizeBytes = chosenSize,
        };

        if (!chosenIsArchive || rules.KeepArchiveIntact(core, game.Rom.FileName))
        {
            game.ServedName = game.Rom.FileName;
            game.ServedSizeBytes = game.Rom.SizeBytes;
            return true;
        }

        if (rules.RepackArchive(core, game.Rom.FileName))
        {
            game.ServedName = Path.ChangeExtension(game.Rom.FileName, ".zip");
            game.ServedSizeBytes = Items(picked, "archive_members").Sum(member => Long(member, "size") ?? 0);
            game.RepackArchive = true;
            return true;
        }

        // The plugin unpacks the ROM before sending it, so the app has to be told the inner
        // file's name. RomM lists archive contents when it scans, and an archive it hasn't
        // listed is skipped rather than served under a name that doesn't match its bytes.
        string? memberName = null;
        long memberSize = -1;
        foreach (var member in Items(picked, "archive_members"))
        {
            var name = LastSegment(Text(member, "name"));
            if (name == null)
            {
                continue;
            }

            var size = Long(member, "size") ?? 0;
            if (rules.IsKnownRomExtension(name))
            {
                memberName = name;
                memberSize = size;
                break;
            }

            if (size > memberSize)
            {
                memberName = name;
                memberSize = size;
            }
        }

        if (memberName == null)
        {
            return false;
        }

        game.ServedName = memberName;
        game.ServedSizeBytes = memberSize;
        game.UnpackArchive = true;
        return true;
    }

    public static List<RommAsset> ParseAssets(JsonElement assets) =>
        assets.ValueKind == JsonValueKind.Array
            ? assets.EnumerateArray().Select(ParseAsset).OfType<RommAsset>().ToList()
            : new List<RommAsset>();

    /// <summary>A save or state, or null without an id or a readable update time.</summary>
    public static RommAsset? ParseAsset(JsonElement asset)
    {
        var id = Int(asset, "id");
        var updated = Text(asset, "updated_at");
        if (id == null || updated == null ||
            !DateTime.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var updatedUtc))
        {
            return null;
        }

        return new RommAsset
        {
            Id = id.Value,
            FileName = Text(asset, "file_name") ?? string.Empty,
            UpdatedUtc = updatedUtc,
        };
    }

    private static int? MostPlayers(string? playerCount)
    {
        if (string.IsNullOrWhiteSpace(playerCount))
        {
            return null;
        }

        int? most = null;
        var current = 0;
        var inNumber = false;
        foreach (var character in playerCount + " ")
        {
            if (character >= '0' && character <= '9')
            {
                current = current * 10 + (character - '0');
                inNumber = true;
            }
            else if (inNumber)
            {
                most = most == null ? current : Math.Max(most.Value, current);
                current = 0;
                inNumber = false;
            }
        }

        return most is > 0 ? most : null;
    }

    private static int? ReleaseYear(long? releaseDate)
    {
        if (releaseDate == null || releaseDate <= 0)
        {
            return null;
        }

        // RomM stores milliseconds. A plain seconds value is far smaller, so both read correctly.
        var milliseconds = releaseDate.Value > 100_000_000_000L ? releaseDate.Value : releaseDate.Value * 1000;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime.Year;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static double? FiveStarRating(double? averageRating)
    {
        if (averageRating == null || averageRating <= 0)
        {
            return null;
        }

        return Math.Round(Math.Min(averageRating.Value, 100) / 20, 1);
    }

    private static string? LastSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var name = path!.Replace('\\', '/').TrimEnd('/');
        var slash = name.LastIndexOf('/');
        name = slash >= 0 ? name.Substring(slash + 1) : name;
        return name.Length == 0 ? null : name;
    }

    private static string? Join(IEnumerable<string> values)
    {
        var joined = string.Join(", ", values);
        return joined.Length == 0 ? null : joined;
    }

    private static IEnumerable<JsonElement> Items(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    private static IEnumerable<string> Strings(JsonElement obj, string name)
    {
        foreach (var item in Items(obj, name))
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString()?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    yield return text!;
                }
            }
        }
    }

    internal static string? Text(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object ||
            !obj.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static bool Bool(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    internal static int? Int(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : (int?)null;

    private static long? Long(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : (long?)null;

    private static double? Double(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number)
            ? number
            : (double?)null;
}
