using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Assembly _assembly;

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

    public EmulatorController(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        _assembly = typeof(EmulatorController).Assembly;
    }

    /// <summary>
    /// Serves the Moonfin EmulatorJS player shell (embedded resource). Anonymous: the shell
    /// is static and non-sensitive, and the WebView loads it as a plain document with no auth
    /// header. The ROM/BIOS/save URLs it fetches carry an api_key and stay authorized.
    /// </summary>
    [HttpGet("player.html")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetPlayer()
    {
        using var stream = _assembly.GetManifestResourceStream("Moonfin.Server.EmulatorJS.player.html");
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

    private static readonly HashSet<string> ThreadRequiredCores =
        new(StringComparer.Ordinal) { "psp" };

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
        if (!string.IsNullOrEmpty(dataRoot) && System.IO.File.Exists(Path.Combine(dataRoot, "loader.js")))
        {
            // Relative to /Moonfin/EmulatorJS/player.html -> /Moonfin/EmulatorJS/data/.
            return "./data/";
        }

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
            return NotFound(new
            {
                Error = "Self-hosted EmulatorJS data folder not installed.",
                ExpectedPath = dataRoot,
                Hint = "Optional: drop the EmulatorJS data/ folder there for offline use. Otherwise the CDN is used automatically."
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
