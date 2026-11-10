namespace Moonfin.Games;

/// <summary>The plugin settings the games code reads. Both ports' PluginConfiguration implement it.</summary>
public interface IGamesConfig
{
    List<string> GameLibraryIds { get; }

    bool GamesEnabled { get; }

    bool GamesMetadataEnabled { get; }

    string GamesMetadataDbUrlBase { get; }

    bool GamesLaunchBoxEnabled { get; }

    string GamesLaunchBoxUrl { get; }
}

/// <summary>Where the games code finds the running plugin's settings and data folder. Each port sets these at startup.</summary>
public static class GamesHost
{
    public static Func<IGamesConfig?> Config { get; set; } = () => null;

    public static Func<string?> DataFolderPath { get; set; } = () => null;
}

/// <summary>Finds the server's game libraries, which each server keeps in its own library manager.</summary>
public interface IGameLibraryHost
{
    /// <summary>The configured libraries, or the ones whose names look like game libraries when none are configured.</summary>
    List<GameLibrary> FindLibraries(IReadOnlyList<string> configuredIds);

    /// <summary>A library's name by its id, or null when it no longer exists.</summary>
    string? LibraryName(string libraryId);

    /// <summary>The server's library folders, for the admin Debug endpoint.</summary>
    object DescribeFolders();
}

/// <summary>Downscales artwork with the server's own image library.</summary>
public interface IGameThumbnailEncoder
{
    /// <summary>True when the server can write the format with this file extension, such as ".jpg".</summary>
    bool CanWrite(string extension);

    /// <summary>Writes a copy no larger than maxDimension on either side and returns the path written, or null.</summary>
    string? Encode(string originalPath, string outputPath, string extension, int maxDimension, int quality);
}
