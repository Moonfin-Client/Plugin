using System.Text;
using Moonfin.Server.Models;

namespace Moonfin.Server.Services;

/// <summary>Resolves opaque game tokens and enforces game-library path containment.</summary>
internal sealed class GamePathResolver
{
    private readonly Func<IReadOnlyList<GameLibrary>> _getLibraries;

    internal GamePathResolver(Func<IReadOnlyList<GameLibrary>> getLibraries)
    {
        _getLibraries = getLibraries;
    }

    internal ResolvedGameFile? ResolveGameFile(string libraryId, string token)
    {
        var path = DecodeToken(token);
        return path == null ? null : ResolveAbsoluteGameFile(libraryId, path, requireExisting: true);
    }

    internal ResolvedGameFile? ResolveAbsoluteGameFile(string libraryId, string path, bool requireExisting)
    {
        var library = _getLibraries().FirstOrDefault(library => SameId(library.Id, libraryId));
        if (library == null || !IsWithinLibrary(library, path) || (requireExisting && !File.Exists(path)))
        {
            return null;
        }

        var systemDir = FindSystemDir(library, path);
        return new ResolvedGameFile(library, path, systemDir, systemDir == null ? string.Empty : Path.GetFileName(systemDir));
    }

    internal string? ResolveFilePath(string libraryId, string token, bool allowBios)
    {
        var resolved = ResolveGameFile(libraryId, token);
        if (resolved == null)
        {
            return null;
        }

        // Preserve the existing controller contract: known ROM and BIOS extensions are always
        // accepted, while allowBios permits the BIOS endpoint to stream other in-library files.
        var isKnownGameFile = GameSystemCoreResolver.IsRomFile(resolved.RomPath) ||
            GameSystemCoreResolver.IsBiosFile(resolved.RomPath);
        return !isKnownGameFile && !allowBios ? null : resolved.RomPath;
    }

    internal static string EncodeToken(string absolutePath)
    {
        var bytes = Encoding.UTF8.GetBytes(absolutePath);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
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

            return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? FindSystemDir(GameLibrary library, string romPath)
    {
        foreach (var root in library.Locations)
        {
            var rootFull = Path.GetFullPath(root);
            var current = Path.GetDirectoryName(Path.GetFullPath(romPath));
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

    private static bool IsWithinLibrary(GameLibrary library, string path) =>
        library.Locations.Any(root => IsWithinRoot(root, path));

    internal static bool IsWithinRoot(string root, string path)
    {
        var full = Path.GetFullPath(path);
        var rootFull = Path.GetFullPath(root);
        var rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull : rootFull + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(rootWithSeparator, comparison) || PathsEqual(full, rootFull);
    }

    private static bool SameId(string? left, string? right)
    {
        var x = left?.Trim();
        var y = right?.Trim();
        if (string.IsNullOrEmpty(x) || string.IsNullOrEmpty(y))
        {
            return false;
        }

        return Guid.TryParse(x, out var leftGuid) && Guid.TryParse(y, out var rightGuid)
            ? leftGuid == rightGuid
            : string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), comparison);
    }
}

internal sealed record ResolvedGameFile(GameLibrary Library, string RomPath, string? SystemDir, string SystemName);
