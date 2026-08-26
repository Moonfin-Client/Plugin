using System.Text.RegularExpressions;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly ArcadeCompatibilityService? _arcadeCompatibility;
    private readonly ArcadeCoreOverrideService? _arcadeCoreOverrides;
    private readonly GameBackendOverrideService? _gameBackendOverrides;
    private readonly GamePathResolver _paths;
    private readonly ILogger<GamesService> _logger;

    private const string EmulatorJsBackend = "emulatorjs";

    private static readonly Regex AutoDetectLibraryName =
        new("game|rom|emulat", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True when the path is a supported archive (.zip/.7z) rather than a raw ROM.</summary>
    public static bool IsArchive(string path) => GameSystemCoreResolver.IsArchive(path);

    private static bool IsRomFile(string path) => GameSystemCoreResolver.IsRomFile(path);

    // Ceiling on a single extracted archive entry, mirroring GamesController's admin-only
    // MaxDatUploadBytes: an explicit, documented limit rather than an unbounded MemoryStream.
    // Unlike the DAT upload path, this endpoint is reachable by any authenticated non-admin user
    // once per ROM request (GamesController.StreamExtractedRom), so a hostile or malformed archive
    // entry that claims a huge size must not be allowed to force a huge in-memory allocation per
    // request. The known cartridge formats this method ever serves top out well under this (N64
    // carts are ~64 MB, the largest NDS carts are a few hundred MB); this is a generous ceiling for
    // those real ROMs while still bounding worst-case memory use per request.
    public const long MaxExtractedRomBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Extracts the playable ROM from a single-ROM .zip/.7z into memory. The archive on disk is left
    /// untouched. MAME archives bypass this method and are streamed intact. Prefers an entry with a
    /// recognized ROM extension, otherwise the largest file. Returns null when the archive holds no
    /// usable ROM.
    /// </summary>
    /// <exception cref="RomTooLargeException">
    /// The chosen entry's reported (or actual decompressed) size exceeds <see cref="MaxExtractedRomBytes"/>.
    /// </exception>
    public static byte[]? ExtractRomFromArchive(string archivePath)
    {
        // .zip goes through the built-in reader (always loadable, in the shared framework). .7z needs
        // SharpCompress, isolated in its own method so a zip never takes a dependency on it (Jellyfin's
        // plugin load context has historically failed to load SharpCompress.dll).
        return ".7z".Equals(Path.GetExtension(archivePath), StringComparison.OrdinalIgnoreCase)
            ? ExtractRomWithSharpCompress(archivePath)
            : ExtractRomFromZip(archivePath);
    }

    /// <summary>
    /// The name and size the extracted ROM will be served with, read from the archive index
    /// rather than by unpacking it, so answering a HEAD costs nothing. Returns null when the
    /// archive holds no usable ROM or can't be read.
    /// </summary>
    public static ExtractedRomInfo? GetExtractedRomInfo(string archivePath)
    {
        try
        {
            return ".7z".Equals(Path.GetExtension(archivePath), StringComparison.OrdinalIgnoreCase)
                ? SharpCompressRomInfo(archivePath)
                : ZipRomInfo(archivePath);
        }
        catch
        {
            return null;
        }
    }

    // .zip via System.IO.Compression (shared framework; no third-party assembly to load).
    private static byte[]? ExtractRomFromZip(string archivePath)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(archivePath);
        var chosen = ChooseZipEntry(zip);
        if (chosen == null)
        {
            return null;
        }

        // The zip central directory's uncompressed-size field is attacker-controlled data (a
        // crafted/corrupt archive can lie), so it is only a fast pre-check. CopyToLimited below
        // enforces the real ceiling against actual decompressed bytes as they are copied.
        CheckEntrySizeOrThrow(chosen.Length, MaxExtractedRomBytes);

        using var entryStream = chosen.Open();
        using var ms = new MemoryStream();
        CopyToLimited(entryStream, ms, MaxExtractedRomBytes);
        return ms.ToArray();
    }

    private static ExtractedRomInfo? ZipRomInfo(string archivePath)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(archivePath);
        var entry = ChooseZipEntry(zip);
        return entry == null ? null : new ExtractedRomInfo(entry.Name, entry.Length);
    }

    // The entry a client is served: the first with a known ROM extension, else the largest file.
    private static System.IO.Compression.ZipArchiveEntry? ChooseZipEntry(
        System.IO.Compression.ZipArchive zip)
    {
        System.IO.Compression.ZipArchiveEntry? largest = null;
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue; // directory entry
            }

            if (GameSystemCoreResolver.IsKnownRomExtension(entry.Name))
            {
                return entry;
            }

            if (largest == null || entry.Length > largest.Length)
            {
                largest = entry;
            }
        }

        return largest;
    }

    // .7z via SharpCompress. Kept in its own method so the assembly only needs to load when a .7z
    // is actually served, and if it can't load the caller catches and returns 404 (zip is
    // unaffected).
    private static byte[]? ExtractRomWithSharpCompress(string archivePath)
    {
        using var archive = ArchiveFactory.Open(archivePath);
        var chosen = ChooseArchiveEntry(archive);
        if (chosen == null)
        {
            return null;
        }

        // Same rationale as the zip path above: the header's Size is only a cheap pre-check, the
        // actual copy below is what really enforces the ceiling.
        CheckEntrySizeOrThrow(chosen.Size, MaxExtractedRomBytes);

        using var entryStream = chosen.OpenEntryStream();
        using var ms = new MemoryStream();
        CopyToLimited(entryStream, ms, MaxExtractedRomBytes);
        return ms.ToArray();
    }

    private static ExtractedRomInfo? SharpCompressRomInfo(string archivePath)
    {
        using var archive = ArchiveFactory.Open(archivePath);
        var entry = ChooseArchiveEntry(archive);
        if (entry == null)
        {
            return null;
        }

        // An entry key carries its path inside the archive, and only the last segment names
        // the file. Backslashes turn up in archives written on Windows.
        var name = Path.GetFileName((entry.Key ?? string.Empty).Replace('\\', '/'));
        return string.IsNullOrEmpty(name) ? null : new ExtractedRomInfo(name, entry.Size);
    }

    private static IArchiveEntry? ChooseArchiveEntry(IArchive archive)
    {
        IArchiveEntry? largest = null;
        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            if (GameSystemCoreResolver.IsKnownRomExtension(entry.Key ?? string.Empty))
            {
                return entry;
            }

            if (largest == null || entry.Size > largest.Size)
            {
                largest = entry;
            }
        }

        return largest;
    }

    // Shared pre-check for both archive formats' chosen entry, factored out (rather than an inline
    // `if` at each call site) so a focused test can drive it with a small maxBytes without needing
    // to construct a multi-hundred-megabyte fixture archive just to exercise this comparison.
    private static void CheckEntrySizeOrThrow(long entrySize, long maxBytes)
    {
        if (entrySize > maxBytes)
        {
            throw new RomTooLargeException(entrySize, maxBytes);
        }
    }

    // Copies at most maxBytes from source to destination, throwing RomTooLargeException the moment
    // more data appears than the ceiling allows. This is the real enforcement point: it bounds
    // memory even when an entry's advertised header size understates (or lies about) how much data
    // actually decompresses, which a plain entry.Length/Size pre-check alone would not catch.
    private static void CopyToLimited(Stream source, Stream destination, long maxBytes)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new RomTooLargeException(total, maxBytes);
            }

            destination.Write(buffer, 0, read);
        }
    }

    public GamesService(
        ILibraryManager libraryManager,
        RdbService? rdb = null,
        LaunchBoxService? launchBox = null,
        ArcadeCompatibilityService? arcadeCompatibility = null,
        ArcadeCoreOverrideService? arcadeCoreOverrides = null,
        GameBackendOverrideService? gameBackendOverrides = null,
        ILogger<GamesService>? logger = null)
    {
        _libraryManager = libraryManager;
        _rdb = rdb;
        _launchBox = launchBox;
        _arcadeCompatibility = arcadeCompatibility;
        _arcadeCoreOverrides = arcadeCoreOverrides;
        _gameBackendOverrides = gameBackendOverrides;
        _paths = new GamePathResolver(GetGameLibraries);
        _logger = logger ?? NullLogger<GamesService>.Instance;
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
    public virtual IReadOnlyList<GameSummary> GetGames(string libraryId, string? systemId)
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
                    // Compatibility resolution hashes every archive entry. Keep the browse
                    // endpoint inexpensive: only resolve an arcade game's effective core when
                    // its detail page is requested, where the user can also select an override.
                    result.Add(new GameSummary
                    {
                        Id = GamePathResolver.EncodeToken(rom),
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

    /// <summary>
    /// The result of locating a game's ROM on disk and resolving its effective system/arcade
    /// compatibility. Computed once by <see cref="ResolveGameAsync"/> and shared by
    /// <see cref="GetGameAsync"/> and <see cref="SetCoreOverrideAsync"/> so a single detail
    /// request or override write never hashes the same archive twice.
    /// </summary>
    private sealed record GameResolution(
        GameLibrary Library,
        string RomPath,
        string? SystemDir,
        string SystemName,
        string SystemCore,
        ArcadeCoreResolution? Arcade);

    /// <summary>
    /// Locates a game's ROM by its opaque token and resolves its system core and (for arcade
    /// systems) arcade compatibility, exactly once. This is the expensive path (it may hash every
    /// entry of a ZIP archive via <see cref="ResolveArcadeCompatibilityAsync"/>) shared by
    /// <see cref="GetGameAsync"/> and <see cref="SetCoreOverrideAsync"/>. Returns null when the
    /// library is unknown or the token does not resolve to a real file inside it.
    /// <paramref name="cancellationToken"/> reaches all the way into the archive-hashing loop in
    /// <see cref="ArcadeCompatibilityService.ResolveAsync"/>, so a cancelled request can abandon that
    /// work instead of always hashing the whole archive to completion.
    /// </summary>
    private async Task<GameResolution?> ResolveGameAsync(
        string libraryId,
        string gameId,
        CancellationToken cancellationToken = default)
    {
        var resolved = _paths.ResolveGameFile(libraryId, gameId);
        if (resolved == null)
        {
            return null;
        }

        var systemCore = ResolveSystemCore(resolved.SystemName, new List<string> { resolved.RomPath });
        var arcade = await ResolveArcadeCompatibilityAsync(resolved.RomPath, systemCore, cancellationToken).ConfigureAwait(false);
        return new GameResolution(resolved.Library, resolved.RomPath, resolved.SystemDir, resolved.SystemName, systemCore, arcade);
    }

    /// <summary>Resolves the full detail for a single game by its opaque id token.</summary>
    public async Task<GameDetail?> GetGameAsync(
        string libraryId,
        string gameId,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        var resolution = await ResolveGameAsync(libraryId, gameId, cancellationToken).ConfigureAwait(false);
        return resolution == null
            ? null
            : await BuildDetailAsync(gameId, resolution, userId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the <see cref="GameDetail"/> response from an already-resolved game plus the
    /// current user's stored override (if any). Split out of <see cref="GetGameAsync"/> so
    /// <see cref="SetCoreOverrideAsync"/> can build the same response after persisting a change
    /// without re-resolving (and re-hashing) the game.
    /// </summary>
    private async Task<GameDetail> BuildDetailAsync(
        string gameId,
        GameResolution resolution,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        var arcade = resolution.Arcade;
        var overrideCore = arcade != null && userId.HasValue && _arcadeCoreOverrides != null
            ? await _arcadeCoreOverrides.GetAsync(userId.Value, arcade.ContentKey, cancellationToken).ConfigureAwait(false)
            : null;
        var backendOverride = userId.HasValue && _gameBackendOverrides != null
            ? await _gameBackendOverrides.GetAsync(userId.Value, GetBackendPreferenceKey(resolution), cancellationToken).ConfigureAwait(false)
            : null;
        // Only values the current API understands are honored. A stale/corrupt preference
        // falls back to normal native-selection rules rather than preventing launch.
        if (!string.Equals(backendOverride, EmulatorJsBackend, StringComparison.Ordinal))
        {
            backendOverride = null;
        }
        // Preferences are validated against the current DAT result. An old override silently
        // falls back to automatic selection if a ROM is replaced or pinned DATs change. A
        // user may explicitly try FBNeo for a MAME-validated set, but it is never selected
        // automatically because the DAT did not verify that compatibility.
        if (arcade != null &&
            !arcade.AvailableCores.Contains(overrideCore ?? string.Empty, StringComparer.Ordinal) &&
            !IsExperimentalFbneoOverride(arcade, overrideCore))
        {
            overrideCore = null;
        }
        var core = overrideCore ?? arcade?.RecommendedCore ?? resolution.SystemCore;
        var bios = resolution.SystemDir == null ? new List<GameBios>() : GetBiosFiles(resolution.SystemDir);
        var title = ResolveTitle(resolution.SystemDir, resolution.RomPath);

        var archiveName = Path.GetFileName(resolution.RomPath);

        // The ROM endpoint unpacks a single-ROM archive and sends the raw ROM, so the detail has
        // to name what the client will actually receive. Naming the archive left a client saving
        // plain ROM bytes under a .zip name and then failing to unpack them. Arcade sets are
        // streamed intact and keep the archive's name, and the system core decides that because
        // the endpoint resolves it the same way.
        var servedName = archiveName;
        var servedSize = SafeFileLength(resolution.RomPath);
        if (IsArchive(resolution.RomPath) &&
            !ShouldPreserveArchiveForCore(resolution.SystemCore, resolution.RomPath))
        {
            var extracted = GetExtractedRomInfo(resolution.RomPath);
            if (extracted != null)
            {
                servedName = extracted.Value.Name;
                servedSize = extracted.Value.Length;
            }
        }

        var detail = new GameDetail
        {
            Id = gameId,
            Title = title,
            System = resolution.SystemName,
            Core = core,
            FileName = servedName,
            SizeBytes = servedSize,
            Bios = bios,
            RecommendedCore = arcade?.RecommendedCore,
            AvailableCores = arcade?.AvailableCores.ToList(),
            CoreCompatibilityReason = arcade?.Reason,
            UserCoreOverride = overrideCore,
            UserBackendOverride = backendOverride,
            BackendOverrideSupported = _gameBackendOverrides != null
        };

        // LaunchBox is the primary source (overview + rich fields); the libretro .rdb fills any
        // gaps and supplies region, which LaunchBox does not carry.
        var lb = _launchBox?.TryLookup(core, title, archiveName);
        var rdb = _rdb == null ? null : await _rdb.TryLookupAsync(core, resolution.RomPath, title, cancellationToken).ConfigureAwait(false);

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
    /// Stores or clears a per-user arcade core override. DAT-validated cores are accepted by
    /// default; a user may explicitly try FBNeo for a MAME-validated archive at their own risk.
    /// </summary>
    public async Task<GameDetail?> SetCoreOverrideAsync(
        string libraryId,
        string gameId,
        Guid userId,
        string? core,
        CancellationToken cancellationToken)
    {
        if (_arcadeCompatibility == null || _arcadeCoreOverrides == null)
        {
            return null;
        }

        var resolution = await ResolveGameAsync(libraryId, gameId, cancellationToken).ConfigureAwait(false);
        if (resolution == null || resolution.Arcade == null)
        {
            return null;
        }

        var arcade = resolution.Arcade;
        if (!arcade.IsValidated ||
            (!string.IsNullOrWhiteSpace(core) &&
             !arcade.AvailableCores.Contains(core, StringComparer.Ordinal) &&
             !IsExperimentalFbneoOverride(arcade, core)))
        {
            throw new ArgumentException("The requested core is not available for this arcade game.", nameof(core));
        }

        await _arcadeCoreOverrides.SetAsync(userId, arcade.ContentKey, core, cancellationToken).ConfigureAwait(false);
        return await BuildDetailAsync(gameId, resolution, userId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores or clears a per-user player-backend override. This works for every system because
    /// it selects the client player, not a system-specific emulator core.
    /// </summary>
    public async Task<GameDetail?> SetBackendOverrideAsync(
        string libraryId,
        string gameId,
        Guid userId,
        string? backend,
        CancellationToken cancellationToken)
    {
        if (_gameBackendOverrides == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(backend) &&
             !string.Equals(backend, EmulatorJsBackend, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The requested game backend is not available.", nameof(backend));
        }

        var resolution = await ResolveGameAsync(libraryId, gameId, cancellationToken).ConfigureAwait(false);
        if (resolution == null)
        {
            return null;
        }

        await _gameBackendOverrides.SetAsync(
            userId,
            GetBackendPreferenceKey(resolution),
            string.IsNullOrWhiteSpace(backend) ? null : EmulatorJsBackend,
            cancellationToken).ConfigureAwait(false);
        return await BuildDetailAsync(gameId, resolution, userId, cancellationToken).ConfigureAwait(false);
    }

    private static string GetBackendPreferenceKey(GameResolution resolution)
    {
        // Game tokens embed absolute paths and are regenerated on a rename. Keep the preference
        // scoped to its library and relative ROM path instead.
        var root = resolution.Library.Locations
            .FirstOrDefault(location => GamePathResolver.IsWithinRoot(location, resolution.RomPath))
            ?? resolution.Library.Locations.FirstOrDefault()
            ?? string.Empty;
        var relativePath = Path.GetRelativePath(root, resolution.RomPath).Replace('\\', '/');
        return $"{resolution.Library.Id}:{relativePath}";
    }

    /// <summary>
    /// The cores, display title, and full ROM path a game's art is keyed on. Deliberately skips
    /// the full metadata resolution <see cref="GetGameAsync"/> does (which may hash an arcade ZIP)
    /// since this fires once per poster, concurrently for every tile on screen, so it must stay
    /// cheap like <see cref="GetGames"/>; arcade compatibility is read only from
    /// <see cref="ArcadeCompatibilityService.TryGetCached"/>, never freshly hashed.
    ///
    /// Returns the full ROM path (not just filename) since <see cref="GameThumbService.GetThumbPathAsync"/>
    /// needs it to CRC-hash the ROM for canonical-name resolution, required for every system, not
    /// only arcade. Also returns the raw system folder name and whether the core was only a
    /// defaulted guess (see <see cref="ResolveSystemCore(string, IReadOnlyList{string}, out bool)"/>),
    /// both used to build the thumbnail path's multi-candidate platform list
    /// (<see cref="RdbService.GetCandidatePlatforms"/>).
    /// </summary>
    public GameThumbSource? ResolveThumbSource(string libraryId, string gameId)
    {
        var resolved = _paths.ResolveGameFile(libraryId, gameId);
        if (resolved == null)
        {
            return null;
        }

        var core = ResolveSystemCore(resolved.SystemName, new List<string> { resolved.RomPath }, out var coreWasDefaulted);
        var arcade = TryGetCachedArcadeCompatibility(resolved.RomPath, core);
        var cores = arcade is { IsValidated: true } && arcade.AvailableCores.Count > 0
            ? arcade.AvailableCores
            : [core];
        // Artwork does not affect emulation compatibility. A clone may run only under one core
        // while its shared cabinet/box art is catalogued under the other, so use both arcade
        // thumbnail catalogs after the validated playback cores have been tried first.
        if (string.Equals(core, ArcadeCompatibilityService.FbneoCore, StringComparison.Ordinal) ||
            string.Equals(core, ArcadeCompatibilityService.MameCore, StringComparison.Ordinal))
        {
            cores = cores
                .Concat([ArcadeCompatibilityService.FbneoCore, ArcadeCompatibilityService.MameCore])
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        return new GameThumbSource(
            cores,
            ResolveTitle(resolved.SystemDir, resolved.RomPath),
            resolved.RomPath,
            resolved.SystemName,
            coreWasDefaulted);
    }

    /// <summary>
    /// Resolves an opaque ROM/BIOS token to an absolute on-disk path, validating it lives
    /// inside the given library and has an allowed extension. Returns null on any mismatch.
    /// </summary>
    public string? ResolveFilePath(string libraryId, string token, bool allowBios)
    {
        return _paths.ResolveFilePath(libraryId, token, allowBios);
    }

    /// <summary>
    /// Returns true when an archive must be delivered intact instead of extracting a single ROM.
    /// MAME ZIPs contain the complete multi-chip arcade set and the emulator identifies the set by
    /// its archive filename.
    /// </summary>
    public bool ShouldPreserveArchive(string libraryId, string romPath)
    {
        if (!IsArchive(romPath))
        {
            return false;
        }

        var resolved = _paths.ResolveAbsoluteGameFile(libraryId, romPath, requireExisting: false);
        if (resolved == null)
        {
            return false;
        }

        return ShouldPreserveArchiveForCore(
            ResolveSystemCore(resolved.SystemName, new List<string> { romPath }),
            romPath);
    }

    // -- internal helpers ---------------------------------------------------

    private static List<string> GetGamesInSystem(string systemDir)
    {
        var roms = new List<string>();
        var core = ResolveSystemCore(
            Path.GetFileName(systemDir),
            Array.Empty<string>());

        // One folder per game: pick the first recognized ROM inside each subfolder.
        foreach (var gameDir in SafeEnumerateDirectories(systemDir))
        {
            var rom = SafeEnumerateFiles(gameDir)
                .FirstOrDefault(path => IsPlayableFileForCore(core, path));
            if (rom != null)
            {
                roms.Add(rom);
            }
        }

        // Also accept loose ROMs sitting directly in the system folder.
        foreach (var file in SafeEnumerateFiles(systemDir))
        {
            if (IsPlayableFileForCore(core, file))
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
            if (IsRomFile(file))
            {
                continue;
            }

            if (GameSystemCoreResolver.IsBiosFile(file))
            {
                bios.Add(new GameBios
                {
                    Id = GamePathResolver.EncodeToken(file),
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

    internal static string ResolveSystemCore(string systemName, IReadOnlyList<string> games) =>
        GameSystemCoreResolver.ResolveSystemCore(systemName, games, out _);

    /// <summary>
    /// Resolves the EmulatorJS core for a system folder (exact alias, then fuzzy alias, then file
    /// extension, then "nes" default), and reports whether the result is a genuine resolution or
    /// just the safe fallback. Playback core selection stays strict/1:1 (a wrong core means
    /// nothing plays) -- unlike artwork resolution, which tries several platforms since a wrong
    /// guess there just costs an extra lookup. Do not collapse the two.
    /// </summary>
    /// <param name="systemName">The raw system folder name as it exists on disk.</param>
    /// <param name="games">ROM paths inside the folder, used for the extension fallback.</param>
    /// <param name="wasDefaulted">True when nothing recognized the folder and the returned core is
    /// only the blind "nes" fallback. Artwork resolution uses this to avoid trusting a defaulted
    /// core's platform the way it trusts a genuine resolution.</param>
    internal static string ResolveSystemCore(string systemName, IReadOnlyList<string> games, out bool wasDefaulted)
        => GameSystemCoreResolver.ResolveSystemCore(systemName, games, out wasDefaulted);

    /// <summary>
    /// Loosely suggests EmulatorJS cores for a system directory name, for artwork candidate
    /// generation only (see RdbService.GetCandidatePlatforms), never playback core selection.
    /// Unlike strict playback selection, returns multiple candidates (ties included)
    /// ranked by closeness, since a wrong artwork guess only costs an extra lookup.
    /// </summary>
    /// <param name="systemName">The raw system folder name as it exists on disk.</param>
    /// <param name="maxSuggestions">Upper bound on how many distinct cores to return.</param>
    internal static IReadOnlyList<string> FuzzySuggestCores(string systemName, int maxSuggestions)
        => GameSystemCoreResolver.FuzzySuggestCores(systemName, maxSuggestions);

    // MAME and arcade (fbneo) cores both play the same kind of filename-sensitive, multi-chip
    // ZIP set intact - only the EmulatorJS core they're handed to differs.
    private static bool IsArcadeFamilyCore(string core) => GameSystemCoreResolver.IsArcadeFamilyCore(core);

    private static bool IsExperimentalFbneoOverride(ArcadeCoreResolution arcade, string? core)
    {
        return arcade.IsValidated &&
               string.Equals(core, ArcadeCompatibilityService.FbneoCore, StringComparison.Ordinal);
    }

    private async Task<ArcadeCoreResolution?> ResolveArcadeCompatibilityAsync(
        string romPath,
        string systemCore,
        CancellationToken cancellationToken = default)
    {
        if (_arcadeCompatibility == null || !IsArcadeFamilyCore(systemCore) ||
            !string.Equals(Path.GetExtension(romPath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return await _arcadeCompatibility.ResolveAsync(romPath, systemCore, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A cancelled request must propagate as a cancellation, not get reinterpreted as a
            // damaged archive by the catch-all below.
            throw;
        }
        catch (Exception ex)
        {
            // This catch-all previously reinterpreted ANY exception - including a real bug like a
            // NullReferenceException - as "damaged archive" with no trace left behind. Log before
            // swallowing so a genuine defect is still visible, while keeping the legacy fallback
            // behavior (a corrupt ZIP must not fail the whole library list).
            _logger.LogWarning(
                ex,
                "Arcade compatibility resolution failed for {RomPath}; treating as a damaged archive and falling back to the system default core.",
                romPath);
            return new ArcadeCoreResolution(
                systemCore,
                [systemCore],
                "Could not inspect this arcade archive; using the system default.",
                GamePathResolver.EncodeToken(romPath),
                false);
        }
    }

    // Cache-only counterpart to ResolveArcadeCompatibility for the thumbnail path: same
    // arcade-family/zip gating, but consults ArcadeCompatibilityService.TryGetCached instead of
    // Resolve, so a cold cache never triggers an archive hash here.
    private ArcadeCoreResolution? TryGetCachedArcadeCompatibility(string romPath, string systemCore)
    {
        if (_arcadeCompatibility == null || !IsArcadeFamilyCore(systemCore) ||
            !string.Equals(Path.GetExtension(romPath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return _arcadeCompatibility.TryGetCached(romPath, out var resolution) ? resolution : null;
    }

    internal static bool ShouldPreserveArchiveForCore(string core, string romPath)
        => GameSystemCoreResolver.ShouldPreserveArchiveForCore(core, romPath);

    internal static bool IsPlayableFileForCore(string core, string romPath)
        => GameSystemCoreResolver.IsPlayableFileForCore(core, romPath);

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

}

/// <summary>
/// The ROM entry a client is served out of an archive. The name is a bare file name, with no
/// path from inside the archive.
/// </summary>
public readonly record struct ExtractedRomInfo(string Name, long Length);

/// <summary>
/// Thrown when a chosen archive entry (advertised or actual decompressed size) exceeds
/// <see cref="GamesService.MaxExtractedRomBytes"/>. Kept as a distinct type (rather than a bare
/// <see cref="IOException"/>) so <c>GamesController.StreamExtractedRom</c> can map it to a
/// specific 413 response instead of the generic 404 it uses for other extraction failures.
/// </summary>
public sealed class RomTooLargeException : IOException
{
    public RomTooLargeException(long actualBytes, long maxBytes)
        : base($"Archive entry is {actualBytes} bytes, exceeding the {maxBytes} byte limit for extracted ROMs.")
    {
        ActualBytes = actualBytes;
        MaxBytes = maxBytes;
    }

    /// <summary>The entry's advertised or actually-copied size (in bytes) that triggered the limit.</summary>
    public long ActualBytes { get; }

    /// <summary>The configured ceiling (in bytes), i.e. <see cref="GamesService.MaxExtractedRomBytes"/>.</summary>
    public long MaxBytes { get; }
}

/// <summary>Resolved, authorization-checked inputs for a game's thumbnail lookup.</summary>
public sealed record GameThumbSource(
    IReadOnlyList<string> Cores,
    string Title,
    string RomPath,
    string SystemName,
    bool CoreWasDefaulted);
