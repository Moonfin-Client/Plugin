using System;
using System.Collections.Generic;

namespace Moonfin.Romm;

public sealed class RommFile
{
    public int Id { get; set; }

    public string FileName { get; set; } = string.Empty;

    public long SizeBytes { get; set; }
}

public sealed class RommPlatform
{
    public int Id { get; set; }

    /// <summary>
    /// The Moonfin system id, a short name like SNES. The apps show it under a game's title and
    /// put it in their routes, so it never holds a slash.
    /// </summary>
    public string SystemId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Core { get; set; } = string.Empty;

    public List<RommFile> Firmware { get; set; } = new List<RommFile>();
}

/// <summary>
/// Metadata fields stay null when RomM has nothing for them, so the apps only show what exists.
/// </summary>
public sealed class RommGame
{
    public int Id { get; set; }

    public int PlatformId { get; set; }

    public string Title { get; set; } = string.Empty;

    public RommFile Rom { get; set; } = new RommFile();

    /// <summary>
    /// The name and size the app receives. For a zipped ROM that is the ROM inside the archive,
    /// since the plugin unpacks it before sending it on. A zip made on the way out, by RomM or by
    /// the plugin, is given the size of the files in it, a little under the zip's own length.
    /// </summary>
    public string ServedName { get; set; } = string.Empty;

    public long ServedSizeBytes { get; set; }

    public bool UnpackArchive { get; set; }

    /// <summary>The plugin rewrites the archive as a zip of everything in it, since the apps only unpack zips.</summary>
    public bool RepackArchive { get; set; }

    /// <summary>
    /// Every file of a game that only plays as a set, like a PlayStation .cue and its tracks.
    /// RomM sends them as one zip. Empty when <see cref="Rom"/> alone is sent.
    /// </summary>
    public List<int> FileIds { get; set; } = new List<int>();

    /// <summary>RomM's server path for the large cover, cache-busting query included.</summary>
    public string? CoverPath { get; set; }

    public string? ScreenshotPath { get; set; }

    /// <summary>RomM's last change to the game, part of the artwork identity.</summary>
    public string UpdatedAt { get; set; } = string.Empty;

    public string? Summary { get; set; }

    public string? Genre { get; set; }

    public string? Developer { get; set; }

    public string? Publisher { get; set; }

    public string? Franchise { get; set; }

    public string? Region { get; set; }

    public int? Year { get; set; }

    public int? Players { get; set; }

    /// <summary>Community rating on Moonfin's 0 to 5 scale.</summary>
    public double? Rating { get; set; }
}

public sealed class RommSnapshot
{
    private readonly Dictionary<int, RommPlatform> _platformsById = new Dictionary<int, RommPlatform>();
    private readonly Dictionary<int, RommGame> _gamesById = new Dictionary<int, RommGame>();

    public RommSnapshot(IReadOnlyList<RommPlatform> platforms, IReadOnlyList<RommGame> games, DateTime loadedUtc)
    {
        Platforms = platforms;
        Games = games;
        LoadedUtc = loadedUtc;
        foreach (var platform in platforms)
        {
            _platformsById[platform.Id] = platform;
        }

        foreach (var game in games)
        {
            _gamesById[game.Id] = game;
        }
    }

    public IReadOnlyList<RommPlatform> Platforms { get; }

    public IReadOnlyList<RommGame> Games { get; }

    public DateTime LoadedUtc { get; }

    public RommPlatform? FindPlatform(int id) => _platformsById.TryGetValue(id, out var platform) ? platform : null;

    public RommGame? FindGame(int id) => _gamesById.TryGetValue(id, out var game) ? game : null;

    /// <summary>
    /// A fingerprint of what clients can see, so a refresh that changed nothing doesn't
    /// trigger an artwork pass.
    /// </summary>
    public string ContentSignature()
    {
        var builder = new System.Text.StringBuilder();
        foreach (var platform in Platforms)
        {
            builder.Append('p').Append(platform.Id).Append(':').Append(platform.Firmware.Count).Append(';');
        }

        foreach (var game in Games)
        {
            builder.Append('g').Append(game.Id).Append(':').Append(game.Rom.Id).Append(':').Append(game.UpdatedAt).Append(';');
        }

        return builder.ToString();
    }
}

/// <summary>
/// The plugin's own file rules, so RomM games are judged the same way as ROMs in a folder
/// library: which files a core can play, which are archives, and which archives stay intact.
/// </summary>
public sealed class RommFileRules
{
    public RommFileRules(
        Func<string, string, bool> isPlayableForCore,
        Func<string, bool> isArchive,
        Func<string, bool> isKnownRomExtension,
        Func<string, string, bool> keepArchiveIntact,
        Func<string, string, bool> isDiscSheet,
        Func<string, string, bool> repackArchive)
    {
        IsPlayableForCore = isPlayableForCore;
        IsArchive = isArchive;
        IsKnownRomExtension = isKnownRomExtension;
        KeepArchiveIntact = keepArchiveIntact;
        IsDiscSheet = isDiscSheet;
        RepackArchive = repackArchive;
    }

    /// <summary>Whether a core can play a file, given the core and the file name.</summary>
    public Func<string, string, bool> IsPlayableForCore { get; }

    public Func<string, bool> IsArchive { get; }

    public Func<string, bool> IsKnownRomExtension { get; }

    /// <summary>Whether an archive is sent as is, given the core and the file name. Arcade sets are.</summary>
    public Func<string, string, bool> KeepArchiveIntact { get; }

    /// <summary>Whether a file is a .cue or .m3u that needs the files beside it, given the core and the file name.</summary>
    public Func<string, string, bool> IsDiscSheet { get; }

    /// <summary>Whether an archive goes out repacked as a zip, given the core and the file name. A PlayStation .7z does.</summary>
    public Func<string, string, bool> RepackArchive { get; }
}

/// <summary>A save or state in RomM.</summary>
public sealed class RommAsset
{
    public int Id { get; set; }

    public string FileName { get; set; } = string.Empty;

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>A pairing RomM started, waiting for the user to approve it in RomM.</summary>
public sealed class RommDeviceLink
{
    public RommDeviceLink(string deviceCode, string userCode, string verificationUrl, string verificationUrlComplete, int expiresInSeconds, int intervalSeconds)
    {
        DeviceCode = deviceCode;
        UserCode = userCode;
        VerificationUrl = verificationUrl;
        VerificationUrlComplete = verificationUrlComplete;
        ExpiresInSeconds = expiresInSeconds;
        IntervalSeconds = intervalSeconds;
    }

    public string DeviceCode { get; }

    public string UserCode { get; }

    public string VerificationUrl { get; }

    public string VerificationUrlComplete { get; }

    public int ExpiresInSeconds { get; }

    public int IntervalSeconds { get; }
}
