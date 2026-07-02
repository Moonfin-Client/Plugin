using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Downloads an EmulatorJS cores <c>data/</c> zip and installs it under the plugin data
/// directory so every Moonfin client can use self-hosted cores. Uses the built-in
/// <see cref="ZipArchive"/> (no third-party archive library, which Jellyfin's plugin load
/// context cannot reliably resolve). The zip URL is the admin override <c>GamesCoreZipUrl</c>,
/// or the <c>emulatorjs-data.zip</c> asset on the Moonbase plugin's latest GitHub release.
/// The download runs in the background and is polled via <see cref="GetStatus"/>; the player
/// switches from the CDN to these files automatically once <c>loader.js</c> exists.
/// </summary>
public class CoresService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CoresService> _logger;

    private readonly object _gate = new();
    private CoresState _state = CoresState.Idle;
    private string? _error;
    private int _filesInstalled;

    public CoresService(IHttpClientFactory httpClientFactory, ILogger<CoresService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public enum CoresState
    {
        Idle,
        Downloading,
        Installed,
        Failed
    }

    public CoresStatus GetStatus()
    {
        lock (_gate)
        {
            return new CoresStatus
            {
                Installed = LocalCoresInstalled(),
                Downloading = _state == CoresState.Downloading,
                State = _state.ToString().ToLowerInvariant(),
                Error = _error,
                FilesInstalled = _filesInstalled
            };
        }
    }

    /// <summary>Starts a background install that downloads the cores zip from a URL.</summary>
    public bool StartInstall()
    {
        if (!BeginInstall())
        {
            return false;
        }

        _ = Task.Run(RunDownloadInstallAsync);
        return true;
    }

    /// <summary>
    /// Starts a background install from an already-uploaded zip at <paramref name="tempZipPath"/>.
    /// The file is deleted when extraction finishes.
    /// </summary>
    public bool StartInstallFromFile(string tempZipPath)
    {
        if (!BeginInstall())
        {
            return false;
        }

        _ = Task.Run(() => InstallFromTempFile(tempZipPath));
        return true;
    }

    private bool BeginInstall()
    {
        lock (_gate)
        {
            if (_state == CoresState.Downloading)
            {
                return false;
            }

            _state = CoresState.Downloading;
            _error = null;
            _filesInstalled = 0;
        }

        return true;
    }

    private async Task RunDownloadInstallAsync()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"emulatorjs-{Guid.NewGuid():N}.zip");
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");

            var zipUrl = await ResolveZipUrlAsync(client).ConfigureAwait(false);
            if (zipUrl == null)
            {
                Fail("No cores zip found. Upload a zip, set a Cores zip URL, or attach an 'emulatorjs-data.zip' asset to the plugin's GitHub release.");
                return;
            }

            // Stream the zip to a temp file rather than buffering ~290 MB in memory.
            await using (var src = await client.GetStreamAsync(zipUrl).ConfigureAwait(false))
            await using (var dst = File.Create(tempFile))
            {
                await src.CopyToAsync(dst).ConfigureAwait(false);
            }

            InstallFromTempFile(tempFile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EmulatorJS core download failed");
            Fail(ex.Message);
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { /* ignore */ }
        }
    }

    /// <summary>Extracts a local zip into the data folder and updates status, then deletes it.</summary>
    private void InstallFromTempFile(string tempZipPath)
    {
        var dataFolder = MoonfinPlugin.Instance?.DataFolderPath;
        if (string.IsNullOrWhiteSpace(dataFolder))
        {
            Fail("Plugin data folder unavailable.");
            return;
        }

        var dataRoot = Path.Combine(dataFolder, "emulatorjs", "data");
        try
        {
            var installed = ExtractDataFolder(tempZipPath, dataRoot);
            if (installed == 0)
            {
                Fail("The cores zip did not contain an EmulatorJS data folder (no loader.js).");
                return;
            }

            lock (_gate)
            {
                _filesInstalled = installed;
                _state = CoresState.Installed;
            }

            _logger.LogInformation("Installed {Count} EmulatorJS core files to {Path}", installed, dataRoot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EmulatorJS core install failed");
            Fail(ex.Message);
        }
        finally
        {
            try { if (File.Exists(tempZipPath)) File.Delete(tempZipPath); } catch { /* ignore */ }
        }
    }

    private void Fail(string message)
    {
        lock (_gate)
        {
            _error = message;
            _state = CoresState.Failed;
        }
    }

    private async Task<string?> ResolveZipUrlAsync(HttpClient client)
    {
        var overrideUrl = MoonfinPlugin.Instance?.Configuration?.GamesCoreZipUrl;
        if (!string.IsNullOrWhiteSpace(overrideUrl))
        {
            return overrideUrl;
        }

        // Otherwise look for an "emulatorjs-data.zip" asset on the plugin's latest release.
        var releaseJson = await client.GetStringAsync(
            "https://api.github.com/repos/Moonfin-Client/Plugin/releases/latest").ConfigureAwait(false);
        using var doc = JsonDocument.Parse(releaseJson);
        if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (!string.IsNullOrEmpty(url) && name != null &&
                name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                name.Contains("emulatorjs", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the zip's EmulatorJS data folder into <paramref name="dataRoot"/>. The
    /// in-archive data root is located by finding loader.js, so it works whether the zip nests
    /// the files under "data/" or at its root. Each entry is written with an explicit
    /// containment check (zip-slip guard).
    /// </summary>
    private static int ExtractDataFolder(string zipPath, string dataRoot)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();

        var loader = entries.FirstOrDefault(e =>
            Path.GetFileName(e.FullName.Replace('\\', '/')).Equals("loader.js", StringComparison.OrdinalIgnoreCase));
        if (loader == null)
        {
            return 0;
        }

        var loaderKey = loader.FullName.Replace('\\', '/');
        var prefix = loaderKey[..(loaderKey.Length - "loader.js".Length)]; // includes trailing '/'

        Directory.CreateDirectory(dataRoot);
        var rootFull = Path.GetFullPath(dataRoot);
        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;

        var count = 0;
        foreach (var entry in entries)
        {
            var key = entry.FullName.Replace('\\', '/');
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = key[prefix.Length..];
            if (relative.Length == 0)
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(dataRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(rootWithSep, StringComparison.Ordinal))
            {
                continue; // zip-slip guard
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            count++;
        }

        return count;
    }

    private static bool LocalCoresInstalled()
    {
        var dataFolder = MoonfinPlugin.Instance?.DataFolderPath;
        if (string.IsNullOrWhiteSpace(dataFolder))
        {
            return false;
        }

        return File.Exists(Path.Combine(dataFolder, "emulatorjs", "data", "loader.js"));
    }
}

public class CoresStatus
{
    // Explicit camelCase so the admin page JS reads the same field names regardless of
    // Jellyfin's global JSON naming policy (which is PascalCase).
    [System.Text.Json.Serialization.JsonPropertyName("installed")]
    public bool Installed { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("downloading")]
    public bool Downloading { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("state")]
    public string State { get; set; } = "idle";

    [System.Text.Json.Serialization.JsonPropertyName("error")]
    public string? Error { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("filesInstalled")]
    public int FilesInstalled { get; set; }
}
