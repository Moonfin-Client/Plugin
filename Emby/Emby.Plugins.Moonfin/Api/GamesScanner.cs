using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Emby.Plugins.Moonfin.Models;
using MediaBrowser.Controller.Library;
using SharpCompress.Archives;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>
    /// Scans "Mixed Content" Emby libraries holding retro game ROMs and exposes a normalized
    /// games model. ROMs are not recognized media types, so this reads the library's physical
    /// folder roots directly using the convention System/&lt;game&gt;/rom.ext with BIOS files
    /// loose in each system folder.
    /// </summary>
    internal class GamesScanner
    {
        private readonly ILibraryManager _libraryManager;

        private static readonly Regex AutoDetectLibraryName =
            new Regex("game|rom|emulat", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Dictionary<string, string> ExtensionToCore =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [".nes"] = "nes", [".fds"] = "nes",
                [".sfc"] = "snes", [".smc"] = "snes",
                [".gb"] = "gb", [".gbc"] = "gb",
                [".gba"] = "gba",
                [".md"] = "segaMD", [".gen"] = "segaMD", [".smd"] = "segaMD",
                [".sms"] = "segaMS", [".sg"] = "segaMS",
                [".gg"] = "segaGG",
                [".n64"] = "n64", [".z64"] = "n64", [".v64"] = "n64",
                [".nds"] = "nds",
                [".vb"] = "vb",
                [".a26"] = "atari2600", [".a78"] = "atari7800",
                [".lnx"] = "lynx",
                [".ws"] = "ws", [".wsc"] = "ws",
                [".ngp"] = "ngp", [".ngc"] = "ngp",
                [".pce"] = "pce",
                // Single-file disc images. Extension is ambiguous across disc systems
                // (.pbp/.iso are used by both PSX and PSP), so the system folder name is the real
                // resolver. These are fallbacks. Multi-file .cue/.bin and .m3u are not handled.
                [".chd"] = "psx", [".pbp"] = "psx",
                [".cso"] = "psp", [".iso"] = "psp",
            };

        private static readonly Dictionary<string, string> SystemNameToCore =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["nes"] = "nes", ["famicom"] = "nes",
                ["snes"] = "snes", ["superfamicom"] = "snes", ["supernintendo"] = "snes",
                ["gb"] = "gb", ["gameboy"] = "gb", ["gbc"] = "gb", ["gameboycolor"] = "gb",
                ["gba"] = "gba", ["gameboyadvance"] = "gba",
                ["genesis"] = "segaMD", ["megadrive"] = "segaMD", ["segagenesis"] = "segaMD",
                ["mastersystem"] = "segaMS", ["sms"] = "segaMS",
                ["gamegear"] = "segaGG", ["gg"] = "segaGG",
                ["n64"] = "n64", ["nintendo64"] = "n64",
                ["nds"] = "nds", ["nintendods"] = "nds",
                ["virtualboy"] = "vb",
                ["atari2600"] = "atari2600", ["atari7800"] = "atari7800",
                ["lynx"] = "lynx",
                ["wonderswan"] = "ws",
                ["neogeopocket"] = "ngp",
                ["pcengine"] = "pce", ["turbografx16"] = "pce",
                ["psx"] = "psx", ["ps1"] = "psx", ["psone"] = "psx", ["playstation"] = "psx",
                ["psp"] = "psp", ["playstationportable"] = "psp",
            };

        private static readonly HashSet<string> BiosExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".bin", ".bios", ".rom", ".img", ".sys", ".bs" };

        // Compressed single-ROM archives. The core is resolved from the system folder name, and the
        // Rom endpoint extracts the inner ROM in memory. Multi-file disc sets in an archive are not
        // supported.
        private static readonly HashSet<string> ArchiveExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z" };

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
        /// entry with a recognized ROM extension, otherwise the largest file. Returns null when none.
        /// </summary>
        public static byte[]? ExtractRomFromArchive(string archivePath)
        {
            // .zip uses the built-in ZipArchive. .7z needs SharpCompress. Picking the entry lives
            // in PickRom and copying it in PickAndCopyRom, so the two adapters only translate the
            // entry API.
            return ".7z".Equals(Path.GetExtension(archivePath), StringComparison.OrdinalIgnoreCase)
                ? ExtractRomWithSharpCompress(archivePath)
                : ExtractRomFromZip(archivePath);
        }

        /// <summary>
        /// The size the extracted ROM will have, read from the archive index rather than by
        /// unpacking it, so answering a HEAD costs nothing. Null when the archive holds no usable
        /// ROM or can't be read.
        /// </summary>
        public static long? GetExtractedRomLength(string archivePath)
        {
            try
            {
                if (".7z".Equals(Path.GetExtension(archivePath), StringComparison.OrdinalIgnoreCase))
                {
                    using var archive = ArchiveFactory.Open(archivePath);
                    return PickRom(archive.Entries, e => e.IsDirectory, e => e.Key ?? string.Empty, e => e.Size)?.Size;
                }

                using var fs = File.OpenRead(archivePath);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
                return PickRom(zip.Entries, e => string.IsNullOrEmpty(e.Name), e => e.Name, e => e.Length)?.Length;
            }
            catch
            {
                return null;
            }
        }

        // Picks the first entry with a recognized ROM extension, otherwise the largest entry.
        // Entry access is via delegates so the SharpCompress types stay inside their adapter.
        private static T? PickRom<T>(
            IEnumerable<T> entries,
            Func<T, bool> isDirectory,
            Func<T, string> nameOf,
            Func<T, long> sizeOf) where T : class
        {
            T? largest = null;
            foreach (var entry in entries)
            {
                if (isDirectory(entry)) continue;
                if (ExtensionToCore.ContainsKey(Path.GetExtension(nameOf(entry)))) return entry;
                if (largest == null || sizeOf(entry) > sizeOf(largest)) largest = entry;
            }

            return largest;
        }

        private static byte[]? PickAndCopyRom<T>(
            IEnumerable<T> entries,
            Func<T, bool> isDirectory,
            Func<T, string> nameOf,
            Func<T, long> sizeOf,
            Func<T, Stream> openStream) where T : class
        {
            var chosen = PickRom(entries, isDirectory, nameOf, sizeOf);
            if (chosen == null) return null;

            using var es = openStream(chosen);
            using var ms = new MemoryStream();
            es.CopyTo(ms);
            return ms.ToArray();
        }

        private static byte[]? ExtractRomFromZip(string archivePath)
        {
            using var fs = File.OpenRead(archivePath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            return PickAndCopyRom(
                zip.Entries,
                e => string.IsNullOrEmpty(e.Name),
                e => e.Name,
                e => e.Length,
                e => e.Open());
        }

        private static byte[]? ExtractRomWithSharpCompress(string archivePath)
        {
            using var archive = ArchiveFactory.Open(archivePath);
            return PickAndCopyRom(
                archive.Entries,
                e => e.IsDirectory,
                e => e.Key ?? string.Empty,
                e => e.Size,
                e => e.OpenEntryStream());
        }

        public GamesScanner(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public IReadOnlyList<GameLibrary> GetGameLibraries()
        {
            var config = Plugin.Instance?.Configuration;
            var configuredIds = config?.GameLibraryIds ?? new List<string>();

            var folders = _libraryManager.GetVirtualFolders();
            var result = new List<GameLibrary>();

            if (configuredIds.Count > 0)
            {
                // The picker stores the id the client sees (a user-view id), which can differ
                // from VirtualFolderInfo.ItemId. Match by ItemId first. If that misses, resolve
                // the id to its library name via GetItemById and match a folder by name.
                foreach (var cid in configuredIds)
                {
                    var folder = folders.FirstOrDefault(f => SameId(f.ItemId, cid));
                    if (folder == null)
                    {
                        var name = ResolveItemName(cid);
                        if (!string.IsNullOrEmpty(name))
                        {
                            folder = folders.FirstOrDefault(f =>
                                string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                        }
                    }

                    if (folder == null) continue;

                    var locations = (folder.Locations ?? new string[0]).Where(Directory.Exists).ToList();
                    if (locations.Count == 0) continue;

                    result.Add(new GameLibrary { Id = cid, Name = folder.Name ?? "Games", Locations = locations });
                }

                return result;
            }

            // Auto-detect by name when nothing is explicitly configured.
            foreach (var folder in folders)
            {
                if (string.IsNullOrEmpty(folder.Name) || !AutoDetectLibraryName.IsMatch(folder.Name)) continue;
                var locations = (folder.Locations ?? new string[0]).Where(Directory.Exists).ToList();
                if (locations.Count == 0) continue;
                result.Add(new GameLibrary { Id = folder.ItemId ?? string.Empty, Name = folder.Name, Locations = locations });
            }

            return result;
        }

        private string? ResolveItemName(string cid)
        {
            if (!Guid.TryParse(cid, out var guid)) return null;
            try { return _libraryManager.GetItemById(guid)?.Name; }
            catch { return null; }
        }

        /// <summary>Library-detection diagnostics for the admin Debug endpoint.</summary>
        public object GetDiagnostics()
        {
            var config = Plugin.Instance?.Configuration;
            return new
            {
                gamesEnabled = config?.GamesEnabled ?? false,
                configuredIds = config?.GameLibraryIds ?? new List<string>(),
                virtualFolders = _libraryManager.GetVirtualFolders().Select(f => new
                {
                    name = f.Name,
                    itemId = f.ItemId,
                    collectionType = f.CollectionType,
                    locations = f.Locations,
                    locationsExist = (f.Locations ?? new string[0]).Select(Directory.Exists).ToArray()
                }).ToList(),
                resolvedLibraries = GetGameLibraries().Select(l => new
                {
                    l.Id,
                    l.Name,
                    l.Locations
                }).ToList()
            };
        }

        public IReadOnlyList<GameSystem> GetSystems(string libraryId)
        {
            var library = GetGameLibraries().FirstOrDefault(l => SameId(l.Id, libraryId));
            if (library == null) return new List<GameSystem>();

            var systems = new List<GameSystem>();
            foreach (var root in library.Locations)
            {
                foreach (var systemDir in SafeEnumerateDirectories(root))
                {
                    var name = Path.GetFileName(systemDir);
                    if (string.IsNullOrEmpty(name) || name.StartsWith(".")) continue;

                    var games = GetGamesInSystem(systemDir);
                    if (games.Count == 0) continue;

                    systems.Add(new GameSystem
                    {
                        Id = name,
                        Name = name,
                        Core = ResolveSystemCore(name, games),
                        GameCount = games.Count
                    });
                }
            }

            return systems.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public IReadOnlyList<GameSummary> GetGames(string libraryId, string? systemId)
        {
            var library = GetGameLibraries().FirstOrDefault(l => SameId(l.Id, libraryId));
            if (library == null) return new List<GameSummary>();

            var result = new List<GameSummary>();
            foreach (var root in library.Locations)
            {
                foreach (var systemDir in SafeEnumerateDirectories(root))
                {
                    var systemName = Path.GetFileName(systemDir);
                    if (string.IsNullOrEmpty(systemName) || systemName.StartsWith(".")) continue;
                    if (!string.IsNullOrEmpty(systemId) &&
                        !string.Equals(systemName, systemId, StringComparison.OrdinalIgnoreCase)) continue;

                    var games = GetGamesInSystem(systemDir);
                    var core = ResolveSystemCore(systemName, games);
                    foreach (var rom in games)
                    {
                        result.Add(new GameSummary
                        {
                            Id = EncodeToken(rom),
                            Title = TitleFor(systemDir, rom),
                            System = systemName,
                            Core = core,
                            FileName = Path.GetFileName(rom)
                        });
                    }
                }
            }

            return result.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public GameDetail? GetGame(string libraryId, string gameId)
        {
            var library = GetGameLibraries().FirstOrDefault(l => SameId(l.Id, libraryId));
            if (library == null) return null;

            var romPath = DecodeToken(gameId);
            if (romPath == null || !IsWithinLibrary(library, romPath) || !File.Exists(romPath)) return null;

            var systemDir = FindSystemDir(library, romPath);
            var systemName = systemDir == null ? string.Empty : Path.GetFileName(systemDir);
            var games = systemDir == null ? new List<string> { romPath } : GetGamesInSystem(systemDir);
            var core = ResolveSystemCore(systemName, games);
            var bios = systemDir == null ? new List<GameBios>() : GetBiosFiles(systemDir);
            var title = TitleFor(systemDir, romPath);

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

            // LaunchBox is the primary source (overview + rich fields). The libretro .rdb fills
            // any gaps and supplies region, which LaunchBox does not carry.
            var lb = GameLaunchBoxHelper.TryLookup(core, title, detail.FileName);
            var rdb = GameMetadataHelper.TryLookup(core, romPath, title);

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
        /// The core and ROM filename a game's art is keyed on. GetGame answers this too, but it
        /// also walks the system folder for its other fields, which is more work than an image
        /// request that arrives once per poster on screen needs.
        /// </summary>
        public (string Core, string FileName)? ResolveThumbSource(string libraryId, string gameId)
        {
            var library = GetGameLibraries().FirstOrDefault(l => SameId(l.Id, libraryId));
            if (library == null) return null;

            var romPath = DecodeToken(gameId);
            if (romPath == null || !IsWithinLibrary(library, romPath) || !File.Exists(romPath)) return null;

            var systemDir = FindSystemDir(library, romPath);
            var systemName = systemDir == null ? string.Empty : Path.GetFileName(systemDir);
            var core = ResolveSystemCore(systemName, new List<string> { romPath });
            return (core, Path.GetFileName(romPath));
        }

        public string? ResolveFilePath(string libraryId, string token, bool allowBios)
        {
            var library = GetGameLibraries().FirstOrDefault(l => SameId(l.Id, libraryId));
            if (library == null) return null;

            var path = DecodeToken(token);
            if (path == null || !IsWithinLibrary(library, path) || !File.Exists(path)) return null;

            var isRom = IsRomFile(path) || BiosExtensions.Contains(Path.GetExtension(path));
            if (!isRom && !allowBios) return null;

            return path;
        }

        // -- helpers ------------------------------------------------------------

        private static string TitleFor(string? systemDir, string romPath)
        {
            if (IsLooseRom(systemDir, romPath))
                return Path.GetFileNameWithoutExtension(romPath);
            return Path.GetFileName(Path.GetDirectoryName(romPath)!);
        }

        private static List<string> GetGamesInSystem(string systemDir)
        {
            var roms = new List<string>();
            foreach (var gameDir in SafeEnumerateDirectories(systemDir))
            {
                var rom = SafeEnumerateFiles(gameDir).FirstOrDefault(IsRomFile);
                if (rom != null) roms.Add(rom);
            }
            foreach (var file in SafeEnumerateFiles(systemDir))
                if (IsRomFile(file)) roms.Add(file);
            return roms;
        }

        private static List<GameBios> GetBiosFiles(string systemDir)
        {
            var bios = new List<GameBios>();
            foreach (var file in SafeEnumerateFiles(systemDir))
            {
                var ext = Path.GetExtension(file);
                if (IsRomFile(file)) continue;
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
            if (systemDir == null) return true;
            var parent = Path.GetDirectoryName(romPath);
            return string.Equals(parent, systemDir, StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveSystemCore(string systemName, IReadOnlyList<string> games)
        {
            var normalized = NormalizeAlphanumericLower(systemName);
            if (SystemNameToCore.TryGetValue(normalized, out var core)) return core;
            foreach (var rom in games)
                if (ExtensionToCore.TryGetValue(Path.GetExtension(rom), out var byExt)) return byExt;
            return "nes";
        }

        internal static string NormalizeAlphanumericLower(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        private string? FindSystemDir(GameLibrary library, string romPath)
        {
            foreach (var root in library.Locations)
            {
                var rootFull = Path.GetFullPath(root);
                var current = Path.GetDirectoryName(Path.GetFullPath(romPath));
                while (!string.IsNullOrEmpty(current))
                {
                    var parent = Path.GetDirectoryName(current);
                    if (parent != null && PathsEqual(parent, rootFull)) return current;
                    if (PathsEqual(current!, rootFull)) break;
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
                var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? rootFull : rootFull + Path.DirectorySeparatorChar;
                var comparison = IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (full.StartsWith(rootWithSep, comparison) || PathsEqual(full, rootFull)) return true;
            }
            return false;
        }

        private static bool PathsEqual(string a, string b)
        {
            var comparison = IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                comparison);
        }

        private static bool SameId(string? a, string? b)
        {
            var x = a?.Trim();
            var y = b?.Trim();
            if (string.IsNullOrEmpty(x) || string.IsNullOrEmpty(y)) return false;
            if (Guid.TryParse(x, out var gx) && Guid.TryParse(y, out var gy)) return gx == gy;
            return string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private static IEnumerable<string> SafeEnumerateDirectories(string path)
        {
            try { return Directory.EnumerateDirectories(path); }
            catch { return new string[0]; }
        }

        private static IEnumerable<string> SafeEnumerateFiles(string path)
        {
            try { return Directory.EnumerateFiles(path); }
            catch { return new string[0]; }
        }

        private static long SafeFileLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }

        private static string EncodeToken(string absolutePath)
        {
            var bytes = Encoding.UTF8.GetBytes(absolutePath);
            return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        private static string? DecodeToken(string token)
        {
            try
            {
                var padded = token.Replace('-', '+').Replace('_', '/');
                switch (padded.Length % 4) { case 2: padded += "=="; break; case 3: padded += "="; break; }
                return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            }
            catch { return null; }
        }
    }
}
