using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// Hosts the EmulatorJS runtime for Moonfin clients: serves the player shell and the
/// EmulatorJS <c>data/</c> folder (loader + WASM cores) from the plugin data directory.
/// Clients point <c>EJS_pathtodata</c> at <c>/Moonfin/EmulatorJS/data/</c>.
/// </summary>
[ApiController]
[Route("Moonfin/EmulatorJS")]
public class EmulatorController : ControllerBase
{
    private static readonly Assembly Assembly = typeof(EmulatorController).Assembly;

    private static readonly IReadOnlyDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".html"] = "text/html; charset=utf-8",
            [".js"] = "application/javascript",
            [".mjs"] = "text/javascript",
            [".css"] = "text/css",
            [".json"] = "application/json",
            [".wasm"] = "application/wasm",
            [".data"] = "application/octet-stream",
            [".mem"] = "application/octet-stream",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".svg"] = "image/svg+xml",
            [".ttf"] = "font/ttf",
            [".woff"] = "font/woff",
            [".woff2"] = "font/woff2",
        };

    /// <summary>Cores whose WASM build needs SharedArrayBuffer, i.e. a cross-origin isolated document.</summary>
    private static readonly HashSet<string> ThreadRequiredCores =
        new(StringComparer.Ordinal) { "psp" };

    /// <summary>
    /// Serves the Moonfin EmulatorJS player shell (embedded resource). Anonymous: the shell
    /// is static and non-sensitive, and the WebView loads it as a plain document with no auth
    /// header. The ROM/BIOS/save URLs it fetches carry an ApiKey query parameter and stay authorized.
    /// </summary>
    [HttpGet("player.html")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetPlayer()
    {
        using var stream = Assembly.GetManifestResourceStream("Moonfin.Server.EmulatorJS.player.html");
        if (stream == null)
        {
            return NotFound(new { Error = "player.html not found" });
        }

        using var reader = new StreamReader(stream);
        var html = reader.ReadToEnd();
        html = html.Replace("__EJS_PATHTODATA__", ResolveDataPath());

        // Cores that need SharedArrayBuffer (e.g. PSP) only work in a cross-origin isolated
        // document. Send the isolation headers just for those, so cartridge and single-threaded
        // disc cores keep loading normally (including from the CDN).
        var core = Request.Query["core"].ToString();
        if (ThreadRequiredCores.Contains(core))
        {
            Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
            Response.Headers["Cross-Origin-Embedder-Policy"] = "credentialless";
        }

        return Content(html, "text/html; charset=utf-8");
    }

    /// <summary>
    /// Serves the Moonfin host/controller bridge used by the anonymous player shell.
    /// </summary>
    [HttpGet("moonfin-bridge.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetMoonfinBridge()
    {
        using var stream = Assembly.GetManifestResourceStream("Moonfin.Server.EmulatorJS.moonfin-bridge.js");
        if (stream == null)
        {
            return NotFound(new { Error = "moonfin-bridge.js not found" });
        }

        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), "application/javascript");
    }

    /// <summary>
    /// Resolves where EmulatorJS should load its runtime + cores from. Order: an admin
    /// override URL, then self-hosted cores if installed, then the EmulatorJS CDN so games
    /// work with zero setup.
    /// </summary>
    private string ResolveDataPath()
    {
        var overrideUrl = MoonfinPlugin.Instance?.Configuration?.GamesCoreDataUrl;
        if (!string.IsNullOrWhiteSpace(overrideUrl))
        {
            return overrideUrl.EndsWith('/') ? overrideUrl : overrideUrl + "/";
        }

        var dataRoot = GetDataRoot();
        if (CoresService.IsDataInstalled(dataRoot))
        {
            // Relative to /Moonfin/EmulatorJS/player.html -> /Moonfin/EmulatorJS/data/.
            return "./data/";
        }

        // Tracks EmulatorJS's "stable" channel rather than a fixed release tag, so this URL can
        // silently change out from under player.html's internals-reaching navigation adapter.
        // See CoresService.ExpectedEmulatorJsVersion for what version that code was last
        // verified against; moonfinAssertEmulatorContract in player.html is the runtime guard
        // against drift here.
        return "https://cdn.emulatorjs.org/stable/data/";
    }

    /// <summary>
    /// Serves self-hosted EmulatorJS <c>data/</c> files from the plugin data directory
    /// (<c>&lt;dataFolder&gt;/emulatorjs/data/</c>) when an admin has installed cores there.
    /// When not installed, the player falls back to the EmulatorJS CDN instead.
    /// </summary>
    [HttpGet("data/{**path}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetDataAsset([FromRoute] string? path)
    {
        var dataRoot = GetDataRoot();
        if (string.IsNullOrEmpty(dataRoot) || !Directory.Exists(dataRoot))
        {
            // This endpoint is [AllowAnonymous] (the player shell fetches it with no auth
            // header), so the 404 body must not disclose server filesystem layout - the absolute
            // ExpectedPath previously returned here reveals the plugin data folder and, on
            // Windows, the OS account name embedded in it (e.g. C:\Users\<name>\AppData\...).
            // The hint stays; only the concrete path is removed.
            return NotFound(new
            {
                Error = "Self-hosted EmulatorJS data folder not installed.",
                Hint = "Optional: install the EmulatorJS data/ folder in the plugin's data directory for offline use. Otherwise the CDN is used automatically."
            });
        }

        var requested = string.IsNullOrWhiteSpace(path) ? "loader.js" : path;
        if (!TryResolveContainedPath(dataRoot, requested, out var fullPath) || !System.IO.File.Exists(fullPath))
        {
            return NotFound();
        }

        return PhysicalFile(fullPath, GetContentType(fullPath), enableRangeProcessing: true);
    }

    private string GetDataRoot()
    {
        var dataFolder = MoonfinPlugin.Instance?.DataFolderPath;
        if (string.IsNullOrWhiteSpace(dataFolder))
        {
            return string.Empty;
        }

        return Path.Combine(dataFolder, "emulatorjs", "data");
    }

    private static bool TryResolveContainedPath(string rootPath, string requestPath, out string fullPath)
    {
        var normalizedRequest = requestPath.Replace('\\', '/').TrimStart('/');
        var candidate = Path.Combine(rootPath, normalizedRequest.Replace('/', Path.DirectorySeparatorChar));
        fullPath = Path.GetFullPath(candidate);

        var normalizedRoot = Path.GetFullPath(rootPath);
        var rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        return fullPath.StartsWith(rootWithSeparator, comparison) ||
               string.Equals(fullPath, normalizedRoot, comparison);
    }

    private static string GetContentType(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return ContentTypes.TryGetValue(extension, out var contentType)
            ? contentType
            : "application/octet-stream";
    }
}
