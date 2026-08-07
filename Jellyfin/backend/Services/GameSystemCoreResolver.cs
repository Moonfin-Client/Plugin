using System.Text;

namespace Moonfin.Server.Services;

/// <summary>
/// Centralizes the static policy used to identify playable files and select EmulatorJS cores.
/// Playback selection remains strict and single-result; artwork callers may use the separate
/// multi-candidate suggestion method.
/// </summary>
internal static class GameSystemCoreResolver
{
    internal const string DefaultCore = "nes";

    private static readonly IReadOnlyDictionary<string, string> ExtensionToCore =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".nes"] = "nes",
            [".fds"] = "nes",
            [".sfc"] = "snes",
            [".smc"] = "snes",
            [".gb"] = "gb",
            [".gbc"] = "gb",
            [".gba"] = "gba",
            [".md"] = "segaMD",
            [".gen"] = "segaMD",
            [".smd"] = "segaMD",
            [".sms"] = "segaMS",
            [".gg"] = "segaGG",
            [".sg"] = "segaMS",
            [".n64"] = "n64",
            [".z64"] = "n64",
            [".v64"] = "n64",
            [".nds"] = "nds",
            [".vb"] = "vb",
            [".a26"] = "atari2600",
            [".a78"] = "atari7800",
            [".lnx"] = "lynx",
            [".ws"] = "ws",
            [".wsc"] = "ws",
            [".ngp"] = "ngp",
            // OPEN QUESTION (raised during MAME branch review, 2026-08-04 - not yet answered by
            // the repo owner): ".ngc" is mapped to Neo Geo Pocket (ngp) here, but ".ngc" is more
            // commonly a Nintendo GameCube disc image. This mapping is byte-for-byte pre-existing
            // - it predates this review and was NOT changed as part of it. Do not "fix" this
            // unilaterally; it may be intentional (e.g. disambiguating some Neo Geo Pocket rip
            // convention this library actually sees) or it may be a genuine bug that silently
            // misroutes GameCube images to the wrong core. Confirm intent with the repo owner
            // before touching this line.
            [".ngc"] = "ngp",
            [".pce"] = "pce",
            [".chd"] = "psx",
            [".pbp"] = "psx",
            [".cso"] = "psp",
            [".iso"] = "psp",
        };

    private static readonly IReadOnlyDictionary<string, string> SystemNameToCore =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["nes"] = "nes",
            ["famicom"] = "nes",
            ["snes"] = "snes",
            ["superfamicom"] = "snes",
            ["supernintendo"] = "snes",
            ["gb"] = "gb",
            ["gameboy"] = "gb",
            ["gbc"] = "gb",
            ["gameboycolor"] = "gb",
            ["gba"] = "gba",
            ["gameboyadvance"] = "gba",
            ["genesis"] = "segaMD",
            ["megadrive"] = "segaMD",
            ["segagenesis"] = "segaMD",
            ["mastersystem"] = "segaMS",
            ["sms"] = "segaMS",
            ["gamegear"] = "segaGG",
            ["gg"] = "segaGG",
            ["n64"] = "n64",
            ["nintendo64"] = "n64",
            ["nds"] = "nds",
            ["nintendods"] = "nds",
            ["virtualboy"] = "vb",
            ["atari2600"] = "atari2600",
            ["atari7800"] = "atari7800",
            ["lynx"] = "lynx",
            ["wonderswan"] = "ws",
            ["neogeopocket"] = "ngp",
            ["pcengine"] = "pce",
            ["turbografx16"] = "pce",
            ["psx"] = "psx",
            ["ps1"] = "psx",
            ["psone"] = "psx",
            ["playstation"] = "psx",
            ["psp"] = "psp",
            ["playstationportable"] = "psp",
            ["mame"] = "mame",
            ["arcade"] = "arcade",
        };

    private static readonly HashSet<string> BiosExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".bin", ".bios", ".rom", ".img", ".sys", ".bs" };

    private static readonly HashSet<string> ArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z" };

    internal static bool IsRomFile(string path) =>
        ExtensionToCore.ContainsKey(Path.GetExtension(path)) || IsArchive(path);

    internal static bool IsArchive(string path) => ArchiveExtensions.Contains(Path.GetExtension(path));

    internal static bool IsBiosFile(string path) => BiosExtensions.Contains(Path.GetExtension(path));

    internal static bool IsKnownRomExtension(string path) => ExtensionToCore.ContainsKey(Path.GetExtension(path));

    internal static string ResolveSystemCore(string systemName, IReadOnlyList<string> games, out bool wasDefaulted)
    {
        wasDefaulted = false;
        var normalized = NormalizeSystemName(systemName);
        if (SystemNameToCore.TryGetValue(normalized, out var core))
        {
            return core;
        }

        if (TryFuzzyMatchSystemCore(normalized, out var fuzzyCore))
        {
            return fuzzyCore;
        }

        foreach (var rom in games)
        {
            if (ExtensionToCore.TryGetValue(Path.GetExtension(rom), out var byExtension))
            {
                return byExtension;
            }
        }

        wasDefaulted = true;
        return DefaultCore;
    }

    internal static IReadOnlyList<string> FuzzySuggestCores(string systemName, int maxSuggestions)
    {
        if (maxSuggestions <= 0)
        {
            return Array.Empty<string>();
        }

        var normalized = NormalizeSystemName(systemName);
        if (normalized.Length < 3)
        {
            return Array.Empty<string>();
        }

        var scored = new List<(string Core, int Diff)>();
        foreach (var kv in SystemNameToCore)
        {
            if (IsFuzzyMatch(normalized, kv.Key))
            {
                scored.Add((kv.Value, Math.Abs(kv.Key.Length - normalized.Length)));
            }
        }

        return scored.OrderBy(s => s.Diff).Select(s => s.Core)
            .Distinct(StringComparer.Ordinal).Take(maxSuggestions).ToList();
    }

    internal static bool IsArcadeFamilyCore(string core) =>
        string.Equals(core, "mame", StringComparison.Ordinal) ||
        string.Equals(core, "arcade", StringComparison.Ordinal);

    internal static bool ShouldPreserveArchiveForCore(string core, string romPath) =>
        IsArcadeFamilyCore(core) &&
        string.Equals(Path.GetExtension(romPath), ".zip", StringComparison.OrdinalIgnoreCase);

    internal static bool IsPlayableFileForCore(string core, string romPath) =>
        IsRomFile(romPath) && (!IsArcadeFamilyCore(core) ||
            string.Equals(Path.GetExtension(romPath), ".zip", StringComparison.OrdinalIgnoreCase));

    private static bool TryFuzzyMatchSystemCore(string normalizedName, out string core)
    {
        core = string.Empty;
        if (normalizedName.Length < 3)
        {
            return false;
        }

        string? bestCore = null;
        var minDiff = int.MaxValue;
        var ambiguous = false;
        foreach (var kv in SystemNameToCore)
        {
            if (!IsFuzzyMatch(normalizedName, kv.Key))
            {
                continue;
            }

            var diff = Math.Abs(kv.Key.Length - normalizedName.Length);
            if (bestCore == null || diff < minDiff)
            {
                minDiff = diff;
                bestCore = kv.Value;
                ambiguous = false;
            }
            else if (diff == minDiff && !string.Equals(kv.Value, bestCore, StringComparison.Ordinal))
            {
                ambiguous = true;
            }
        }

        if (bestCore == null || ambiguous)
        {
            return false;
        }

        core = bestCore;
        return true;
    }

    private static bool IsFuzzyMatch(string normalizedName, string alias) =>
        alias.Length >= 3 && (normalizedName.Contains(alias, StringComparison.Ordinal) ||
            alias.Contains(normalizedName, StringComparison.Ordinal));

    private static string NormalizeSystemName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }
}
