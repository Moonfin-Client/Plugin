using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Moonfin.Server.Models;
using SharpCompress.Archives;

namespace Moonfin.Server.Services;

/// <summary>
/// Scans Jellyfin libraries that hold retro game ROMs and exposes a normalized games
/// model for Moonfin clients (EmulatorJS).
///
/// ROM files (.sfc, .nes, ...) are not recognized media types, so Jellyfin never indexes
/// them as library items. Instead of relying on the item database, this service reads the
/// library's physical folder roots and walks them on disk using the convention:
///
///     &lt;Library root&gt;/&lt;System&gt;/             top-level folder per console (e.g. "SNES")
///         &lt;bios files&gt;                       loose BIOS files for that system
///         &lt;Game name&gt;/                       one folder per game
///             &lt;rom file&gt;.&lt;ext&gt;
/// </summary>
public class GamesService
{
    private readonly ILibraryManager _libraryManager;
    private readonly RdbService? _rdb;
    private readonly LaunchBoxService? _launchBox;

    private static readonly Regex AutoDetectLibraryName =
        new("game|rom|emulat", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Recognized ROM file extensions -> EmulatorJS core (fallback when the system folder
    // name is not recognized). Ambiguous extensions (.bin/.cue/.zip/.iso) are resolved by
    // the system folder name instead and intentionally omitted here.
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
            [".ngc"] = "ngp",
            [".pce"] = "pce",
            // Single-file disc images. Extension is ambiguous across disc systems (.pbp/.iso
            // are used by both PSX and PSP), so the system folder name is the real resolver;
            // these are only fallbacks. Multi-file .cue/.bin and multi-disc .m3u are not handled.
            [".chd"] = "psx",
            [".pbp"] = "psx",
            [".cso"] = "psp",
            [".iso"] = "psp",
        };

    // System folder name aliases -> EmulatorJS core (primary resolution; more reliable than
    // extension for disc-based / ambiguous formats).
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
            // PSX (pcsx_rearmed, single-threaded, BIOS from the system folder) and PSP (ppsspp,
            // needs cross-origin isolation for threads, no BIOS) are supported via single-file
            // disc images only. Saturn / Sega CD / 32X / Arcade / MAME remain omitted (heavier
            // threaded cores, multi-file sets) rather than advertising unplayable systems.
        };

    private static readonly HashSet<string> BiosExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".bin", ".bios", ".rom", ".img", ".sys", ".bs" };

    // Compressed single-ROM archives. The core is resolved from the system folder name, so these
    // need no extension->core mapping; EmulatorJS decompresses them on the client. Multi-file sets
    // (disc bin/cue) inside an archive are not supported.
    private static readonly HashSet<string> ArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z" };

    // A file the scanner treats as a playable ROM: a known ROM extension or a supported archive.
    private static bool IsRomFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ExtensionToCore.ContainsKey(ext) || ArchiveExtensions.Contains(ext);
    }

    /// <summary>True when the path is a supported archive (.zip/.7z) rather than a raw ROM.</summary>
    public static bool IsArchive(string path) => ArchiveExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Extracts the playable ROM from a .zip/.7z into memory so every client receives raw ROM bytes
    /// and never has to unpack the archive itself. The archive on disk is left untouched. Prefers the
    /// entry with a recognized ROM extension, otherwise the largest file. Returns null when the
    /// archive holds no usable ROM.
    /// </summary>
    public static byte[]? ExtractRomFromArchive(string archivePath)
    {
        // .zip goes through the built-in reader (always loadable, in the shared framework). .7z needs
        // SharpCompress, isolated in its own method so a zip never takes a dependency on it (Jellyfin's
        // plugin load context has historically failed to load SharpCompress.dll).
        return ".7z".Equals(Path.GetExtension(archivePath), StringComparison.OrdinalIgnoreCase)
            ? ExtractRomWithSharpCompress(archivePath)
            : ExtractRomFromZip(archivePath);
    }

    // .zip via System.IO.Compression (shared framework; no third-party assembly to load).
    private static byte[]? ExtractRomFromZip(string archivePath)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(archivePath);
        System.IO.Compression.ZipArchiveEntry? romEntry = null;
        System.IO.Compression.ZipArchiveEntry? largest = null;
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue; // directory entry
            }

            if (ExtensionToCore.ContainsKey(Path.GetExtension(entry.Name)))
            {
                romEntry = entry;
                break;
            }

            if (largest == null || entry.Length > largest.Length)
            {
                largest = entry;
            }
        }

        var chosen = romEntry ?? largest;
        if (chosen == null)
        {
            return null;
        }

        using var entryStream = chosen.Open();
        using var ms = new MemoryStream();
        entryStream.CopyTo(ms);
        return ms.ToArray();
    }

    // .7z via SharpCompress. Kept in its own method so the assembly only needs to load when a .7z is
    // actually served; if it cannot load, the caller catches and returns 404 (zip is unaffected).
    private static byte[]? ExtractRomWithSharpCompress(string archivePath)
    {
        using var archive = ArchiveFactory.Open(archivePath);
        IArchiveEntry? romEntry = null;
        IArchiveEntry? largest = null;
        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            if (ExtensionToCore.ContainsKey(Path.GetExtension(entry.Key ?? string.Empty)))
            {
                romEntry = entry;
                break;
            }

            if (largest == null || entry.Size > largest.Size)
            {
                largest = entry;
            }
        }

        var chosen = romEntry ?? largest;
        if (chosen == null)
        {
            return null;
        }

        using var entryStream = chosen.OpenEntryStream();
        using var ms = new MemoryStream();
        entryStream.CopyTo(ms);
        return ms.ToArray();
    }

    public GamesService(
        ILibraryManager libraryManager,
        RdbService? rdb = null,
        LaunchBoxService? launchBox = null)
    {
        _libraryManager = libraryManager;
        _rdb = rdb;
        _launchBox = launchBox;
    }

    /// <summary>Returns the libraries Moonbase treats as game (ROM) libraries.</summary>
    public IReadOnlyList<GameLibrary> GetGameLibraries()
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        var configuredIds = config?.GameLibraryIds ?? new List<string>();

        var result = new List<GameLibrary>();

        if (configuredIds.Count > 0)
        {
            // Resolve each configured id directly. The picker stores the id the client sees
            // (a user-view id), which can differ from VirtualFolderInfo.ItemId, so resolve via
            // GetItemById first and only fall back to a virtual-folder id match.
            foreach (var cid in configuredIds)
            {
                var resolved = ResolveConfiguredLibrary(cid);
                if (resolved != null)
                {
                    result.Add(resolved);
                }
            }

            return result;
        }

        // Auto-detect by name when nothing is explicitly configured.
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            if (string.IsNullOrEmpty(folder.Name) || !AutoDetectLibraryName.IsMatch(folder.Name))
            {
                continue;
            }

            var locations = (folder.Locations ?? Array.Empty<string>()).Where(Directory.Exists).ToList();
            if (locations.Count == 0)
            {
                continue;
            }

            result.Add(new GameLibrary
            {
                Id = folder.ItemId ?? string.Empty,
                Name = folder.Name,
                Locations = locations
            });
        }

        return result;
    }

    private GameLibrary? ResolveConfiguredLibrary(string? cid)
    {
        if (string.IsNullOrWhiteSpace(cid))
        {
            return null;
        }

        // 1) Resolve by the exact id the client/picker provided and read its disk paths.
        if (Guid.TryParse(cid, out var guid))
        {
            var item = _libraryManager.GetItemById(guid);
            var locs = (item?.PhysicalLocations ?? Array.Empty<string>()).Where(Directory.Exists).ToList();
            if (locs.Count > 0)
            {
                return new GameLibrary { Id = cid!, Name = item?.Name ?? "Games", Locations = locs };
            }
        }

        // 2) Fall back to matching a virtual folder by id (handles id-format differences).
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            if (!SameId(folder.ItemId, cid))
            {
                continue;
            }

            var locs = (folder.Locations ?? Array.Empty<string>()).Where(Directory.Exists).ToList();
            if (locs.Count > 0)
            {
                return new GameLibrary { Id = cid!, Name = folder.Name ?? "Games", Locations = locs };
            }
        }

        return null;
    }

    /// <summary>
    /// Diagnostic snapshot for troubleshooting why a library isn't detected. Exposes the raw
    /// virtual folders, configured ids, and resolved game libraries (with their disk paths).
    /// </summary>
    public object GetDiagnostics()
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        return new
        {
            gamesEnabled = config?.GamesEnabled ?? false,
            configuredIds = config?.GameLibraryIds ?? new List<string>(),
            virtualFolders = _libraryManager.GetVirtualFolders().Select(f => new
            {
                name = f.Name,
                itemId = f.ItemId,
                collectionType = f.CollectionType?.ToString(),
                locations = f.Locations,
                locationsExist = (f.Locations ?? Array.Empty<string>()).Select(Directory.Exists).ToArray()
            }).ToList(),
            resolvedLibraries = GetGameLibraries().Select(l => new
            {
                l.Id,
                l.Name,
                l.Locations
            }).ToList()
        };
    }

    /// <summary>Lists the top-level system folders inside a game library.</summary>
    public IReadOnlyList<GameSystem> GetSystems(string libraryId)
    {
        var library = GetGameLibraries().FirstOrDefault(l =>
            SameId(l.Id, libraryId));
        if (library == null)
        {
            return Array.Empty<GameSystem>();
        }

        var systems = new List<GameSystem>();
        foreach (var root in library.Locations)
        {
            foreach (var systemDir in SafeEnumerateDirectories(root))
            {
                var name = Path.GetFileName(systemDir);
                if (string.IsNullOrEmpty(name) || name.StartsWith('.'))
                {
                    continue;
                }

                var games = GetGamesInSystem(systemDir);
                if (games.Count == 0)
                {
                    continue;
                }

                systems.Add(new GameSystem
                {
                    Id = name,
                    Name = name,
                    Core = ResolveSystemCore(name, games),
                    GameCount = games.Count
                });
            }
        }

        return systems
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Lists the games inside a system folder (optionally a single system).</summary>
    public IReadOnlyList<GameSummary> GetGames(string libraryId, string? systemId)
    {
        var library = GetGameLibraries().FirstOrDefault(l =>
            SameId(l.Id, libraryId));
        if (library == null)
        {
            return Array.Empty<GameSummary>();
        }

        var result = new List<GameSummary>();
        foreach (var root in library.Locations)
        {
            foreach (var systemDir in SafeEnumerateDirectories(root))
            {
                var systemName = Path.GetFileName(systemDir);
                if (string.IsNullOrEmpty(systemName) || systemName.StartsWith('.'))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(systemId) &&
                    !string.Equals(systemName, systemId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var games = GetGamesInSystem(systemDir);
                var core = ResolveSystemCore(systemName, games);
                foreach (var rom in games)
                {
                    result.Add(new GameSummary
                    {
                        Id = EncodeToken(rom),
                        Title = ResolveTitle(systemDir, rom),
                        System = systemName,
                        Core = core,
                        FileName = Path.GetFileName(rom)
                    });
                }
            }
        }

        return result
            .OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Resolves the full detail for a single game by its opaque id token.</summary>
    public GameDetail? GetGame(string libraryId, string gameId)
    {
        var library = GetGameLibraries().FirstOrDefault(l =>
            SameId(l.Id, libraryId));
        if (library == null)
        {
            return null;
        }

        var romPath = DecodeToken(gameId);
        if (romPath == null || !IsWithinLibrary(library, romPath) || !File.Exists(romPath))
        {
            return null;
        }

        var systemDir = FindSystemDir(library, romPath);
        var systemName = systemDir == null ? string.Empty : Path.GetFileName(systemDir);
        var core = ResolveSystemCore(systemName, new List<string> { romPath });
        var bios = systemDir == null ? new List<GameBios>() : GetBiosFiles(systemDir);
        var title = ResolveTitle(systemDir, romPath);

        var detail = new GameDetail
        {
            Id = gameId,
            Title = title,
            System = systemName,
            Core = core,
            FileName = Path.GetFileName(romPath),
            SizeBytes = SafeFileLength(romPath),
            Bios = bios
        };

        // LaunchBox is the primary source (overview + rich fields); the libretro .rdb fills any
        // gaps and supplies region, which LaunchBox does not carry.
        var lb = _launchBox?.TryLookup(core, title, detail.FileName);
        var rdb = _rdb?.TryLookup(core, romPath, title);

        detail.Overview = lb?.Overview;
        detail.Genre = lb?.Genre ?? rdb?.Genre;
        detail.Developer = lb?.Developer ?? rdb?.Developer;
        detail.Publisher = lb?.Publisher ?? rdb?.Publisher;
        detail.Franchise = rdb?.Franchise;
        detail.Region = rdb?.Region;
        detail.Year = lb?.Year ?? rdb?.ReleaseYear;
        detail.Players = lb?.Players ?? rdb?.Users;
        detail.Rating = lb?.Rating;

        return detail;
    }

    /// <summary>
    /// The core and ROM filename a game's art is keyed on. GetGame would answer this too, but it
    /// also hashes the ROM for the metadata lookup, which is far too much work for an image
    /// request that arrives once per poster on screen.
    /// </summary>
    public (string Core, string FileName)? ResolveThumbSource(string libraryId, string gameId)
    {
        var library = GetGameLibraries().FirstOrDefault(l =>
            SameId(l.Id, libraryId));
        if (library == null)
        {
            return null;
        }

        var romPath = DecodeToken(gameId);
        if (romPath == null || !IsWithinLibrary(library, romPath) || !File.Exists(romPath))
        {
            return null;
        }

        var systemDir = FindSystemDir(library, romPath);
        var systemName = systemDir == null ? string.Empty : Path.GetFileName(systemDir);
        var core = ResolveSystemCore(systemName, new List<string> { romPath });
        
        // Resolve the exact No-Intro name from Rdb if available so region codes/tags (like (USA)) are included in the art URL.
        var title = ResolveTitle(systemDir, romPath);
        var rdb = _rdb?.TryLookup(core, romPath, title);
        var name = rdb?.Name ?? Path.GetFileNameWithoutExtension(romPath);

        return (core, name);
    }

    /// <summary>
    /// Resolves an opaque ROM/BIOS token to an absolute on-disk path, validating it lives
    /// inside the given library and has an allowed extension. Returns null on any mismatch.
    /// </summary>
    public string? ResolveFilePath(string libraryId, string token, bool allowBios)
    {
        var library = GetGameLibraries().FirstOrDefault(l =>
            SameId(l.Id, libraryId));
        if (library == null)
        {
            return null;
        }

        var path = DecodeToken(token);
        if (path == null || !IsWithinLibrary(library, path) || !File.Exists(path))
        {
            return null;
        }

        var isRom = IsRomFile(path) || BiosExtensions.Contains(Path.GetExtension(path));
        if (!isRom && !allowBios)
        {
            return null;
        }

        return path;
    }

    // -- internal helpers ---------------------------------------------------

    private static List<string> GetGamesInSystem(string systemDir)
    {
        var roms = new List<string>();

        // One folder per game: pick the first recognized ROM inside each subfolder.
        foreach (var gameDir in SafeEnumerateDirectories(systemDir))
        {
            var rom = SafeEnumerateFiles(gameDir).FirstOrDefault(IsRomFile);
            if (rom != null)
            {
                roms.Add(rom);
            }
        }

        // Also accept loose ROMs sitting directly in the system folder.
        foreach (var file in SafeEnumerateFiles(systemDir))
        {
            if (IsRomFile(file))
            {
                roms.Add(file);
            }
        }

        return roms;
    }

    private static List<GameBios> GetBiosFiles(string systemDir)
    {
        // BIOS files are loose files at the top of the system folder that are NOT ROMs.
        var bios = new List<GameBios>();
        foreach (var file in SafeEnumerateFiles(systemDir))
        {
            var ext = Path.GetExtension(file);
            if (IsRomFile(file))
            {
                continue;
            }

            if (BiosExtensions.Contains(ext))
            {
                bios.Add(new GameBios
                {
                    Id = EncodeToken(file),
                    FileName = Path.GetFileName(file),
                    SizeBytes = SafeFileLength(file)
                });
            }
        }

        return bios;
    }

    private static bool IsLooseRom(string? systemDir, string romPath)
    {
        if (systemDir == null)
        {
            return true;
        }

        var parent = Path.GetDirectoryName(romPath);
        return string.Equals(parent, systemDir, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveTitle(string? systemDir, string romPath)
    {
        return IsLooseRom(systemDir, romPath)
            ? Path.GetFileNameWithoutExtension(romPath)
            : Path.GetFileName(Path.GetDirectoryName(romPath)!);
    }

    private static string ResolveSystemCore(string systemName, IReadOnlyList<string> games)
    {
        var normalized = NormalizeSystemName(systemName);
        if (SystemNameToCore.TryGetValue(normalized, out var core))
        {
            return core;
        }

        foreach (var rom in games)
        {
            if (ExtensionToCore.TryGetValue(Path.GetExtension(rom), out var byExt))
            {
                return byExt;
            }
        }

        return "nes"; // safe default; client can still let the user override
    }

    private static string NormalizeSystemName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    private string? FindSystemDir(GameLibrary library, string romPath)
    {
        foreach (var root in library.Locations)
        {
            var rootFull = Path.GetFullPath(root);
            var current = Path.GetDirectoryName(Path.GetFullPath(romPath));
            // Walk up until the parent is the library root: that node is the system folder.
            while (!string.IsNullOrEmpty(current))
            {
                var parent = Path.GetDirectoryName(current);
                if (parent != null && PathsEqual(parent, rootFull))
                {
                    return current;
                }

                if (PathsEqual(current, rootFull))
                {
                    break;
                }

                current = parent;
            }
        }

        return null;
    }

    private static bool IsWithinLibrary(GameLibrary library, string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var root in library.Locations)
        {
            var rootFull = Path.GetFullPath(root);
            var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (full.StartsWith(rootWithSep, comparison) || PathsEqual(full, rootFull))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Compares two library ids tolerant of GUID formatting. Jellyfin reports library ids in
    /// different formats across APIs (e.g. dashed "D" vs compact "N"), so a plain string
    /// compare between a client-supplied id and <c>VirtualFolderInfo.ItemId</c> can miss.
    /// </summary>
    private static bool SameId(string? a, string? b)
    {
        var x = a?.Trim();
        var y = b?.Trim();
        if (string.IsNullOrEmpty(x) || string.IsNullOrEmpty(y))
        {
            return false;
        }

        if (Guid.TryParse(x, out var gx) && Guid.TryParse(y, out var gy))
        {
            return gx == gy;
        }

        return string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            comparison);
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static long SafeFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string EncodeToken(string absolutePath)
    {
        var bytes = Encoding.UTF8.GetBytes(absolutePath);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static string? DecodeToken(string token)
    {
        try
        {
            var padded = token.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }

            var bytes = Convert.FromBase64String(padded);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
