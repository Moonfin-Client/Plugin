using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Api;

/// <summary>
/// Controller to serve Moonfin web files.
/// </summary>
[ApiController]
[Route("Moonfin/Web")]
public class MoonfinWebController : ControllerBase
{
    private readonly Assembly _assembly;
    private readonly ILogger<MoonfinWebController> _logger;

    // Every asset request hits the same failure, so warn once a run rather than per request.
    private static int _missingWebRootLogged;
    private static readonly Regex BaseHrefRegex = new Regex(
        "<base\\s+href=\"[^\"]*\"\\s*/?>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly IReadOnlyDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".html"] = "text/html; charset=utf-8",
            [".htm"] = "text/html; charset=utf-8",
            [".js"] = "application/javascript",
            [".mjs"] = "text/javascript",
            [".css"] = "text/css",
            [".json"] = "application/json",
            [".map"] = "application/json",
            [".wasm"] = "application/wasm",
            [".svg"] = "image/svg+xml",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".ico"] = "image/x-icon",
            [".txt"] = "text/plain; charset=utf-8",
            [".xml"] = "application/xml",
            [".woff"] = "font/woff",
            [".woff2"] = "font/woff2",
            [".ttf"] = "font/ttf",
            [".otf"] = "font/otf",
        };

    public MoonfinWebController(ILogger<MoonfinWebController> logger)
    {
        _logger = logger;
        _assembly = typeof(MoonfinWebController).Assembly;
    }

    /// <summary>
    /// Legacy entrypoint redirect to Moonfin web app.
    /// </summary>
    [HttpGet("plugin.js")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult RedirectLegacyPluginJs()
    {
        var target = ResolveWebBaseHref();
        var script =
            "(function(){if(window.location.pathname.toLowerCase().indexOf('/moonfin/web/')===-1){window.location.href='"
            + target + "';}})();";
        return Content(script, "application/javascript");
    }

    /// <summary>
    /// Legacy entrypoint redirect to Moonfin web app.
    /// </summary>
    [HttpGet("plugin.css")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult RedirectLegacyPluginCss()
    {
        return Content("/* legacy moonfin css stub */", "text/css");
    }

    /// <summary>
    /// Serves the Moonfin loader script bridge for stock Jellyfin web.
    /// </summary>
    /// <returns>The loader.js file.</returns>
    [HttpGet("loader.js")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetLoaderJs()
    {
        var resourceName = "Moonfin.Server.Web.loader.js";
        var stream = _assembly.GetManifestResourceStream(resourceName);

        if (stream == null)
        {
            return NotFound(new { Error = "loader.js not found" });
        }

        return File(stream, "application/javascript");
    }

    /// <summary>
    /// Serves runtime config consumed by Moonfin web plugin mode.
    /// </summary>
    [HttpGet("config.json")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetRuntimeConfig()
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        var runtimeBaseUrl = ResolveRuntimeBaseUrl();
        var payload = new
        {
            schemaVersion = 1,
            defaultServerUrl = ResolveDefaultServerUrl(config?.WebDefaultServerUrl, runtimeBaseUrl),
            discoveryProxyUrl = ResolveDiscoveryProxyUrl(runtimeBaseUrl),
            enableWebRtcScan = config?.WebEnableWebRtcScan ?? true,
            brandingName = "Moonfin",
            pluginMode = true,
            forcedServerUrl = NormalizeConfiguredServerUrl(config?.WebForcedServerUrl)
        };

        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

        return new JsonResult(payload);
    }

    /// <summary>
    /// Serves Moonfin web static files from disk with index fallback for SPA routes.
    /// </summary>
    [HttpGet("{**path}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetWebAsset([FromRoute] string? path)
    {
        var webRoot = ResolveWebRoot();
        if (string.IsNullOrWhiteSpace(webRoot) || !Directory.Exists(webRoot))
        {
            WarnAboutMissingWebRoot(webRoot);
            return NotFound(new { Error = "Moonfin web root not found", Path = webRoot });
        }

        var requestedPath = string.IsNullOrWhiteSpace(path) ? "index.html" : path;
        if (!TryResolvePath(webRoot, requestedPath, out var fullPath))
        {
            return NotFound();
        }

        if (System.IO.File.Exists(fullPath))
        {
            if (IsIndexHtml(fullPath))
            {
                return ServeIndexHtml(fullPath);
            }

            return PhysicalFile(fullPath, GetContentType(fullPath));
        }

        if (Directory.Exists(fullPath))
        {
            var nestedIndexPath = Path.Combine(fullPath, "index.html");
            if (System.IO.File.Exists(nestedIndexPath))
            {
                // The request resolved to a directory's index (e.g. the theme
                // editor at /theme). If the URL lacks a trailing slash, redirect
                // to add one so the browser resolves the page's relative asset
                // paths against the directory (.../theme/) instead of its parent
                // (.../), which otherwise 404s its css/js/icons.
                var urlPath = Request.Path.Value ?? string.Empty;
                if (urlPath.Length > 0 && !urlPath.EndsWith('/'))
                {
                    return Redirect($"{Request.PathBase}{urlPath}/{Request.QueryString}");
                }

                return ServeIndexHtml(nestedIndexPath);
            }
        }

        if (Path.HasExtension(requestedPath))
        {
            return NotFound();
        }

        var indexPath = Path.Combine(webRoot, "index.html");
        if (!System.IO.File.Exists(indexPath))
        {
            return NotFound(new { Error = "Moonfin web entrypoint missing", Path = indexPath });
        }

        return ServeIndexHtml(indexPath);
    }

    private static string? NormalizeOptionalText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private string ResolveRuntimeBaseUrl()
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}".Trim();
        return baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Resolves the absolute path the web app is served from, including any Jellyfin
    /// reverse-proxy sub-path prefix (Request.PathBase). Used to rewrite the index.html
    /// base href so the app works under both subdomain and sub-path hosting.
    /// </summary>
    private string ResolveWebBaseHref()
    {
        var pathBase = Request.PathBase.Value?.TrimEnd('/') ?? string.Empty;
        return $"{pathBase}/Moonfin/Web/";
    }

    private static bool IsIndexHtml(string filePath)
    {
        return string.Equals(
            Path.GetFileName(filePath),
            "index.html",
            StringComparison.OrdinalIgnoreCase);
    }

    private IActionResult ServeIndexHtml(string indexPath)
    {
        var pathBase = Request.PathBase.Value?.TrimEnd('/') ?? string.Empty;
        if (pathBase.Length == 0)
        {
            // No reverse-proxy sub-path: the build-time base href is already correct,
            // so serve the file unchanged (identical to standard subdomain/root hosting).
            return PhysicalFile(indexPath, "text/html; charset=utf-8");
        }

        var html = System.IO.File.ReadAllText(indexPath);
        html = BaseHrefRegex.Replace(html, _ => $"<base href=\"{pathBase}/Moonfin/Web/\">");

        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

        return Content(html, "text/html; charset=utf-8");
    }

    private static string ResolveDiscoveryProxyUrl(string runtimeBaseUrl)
    {
        return $"{runtimeBaseUrl}/Moonfin/Discovery/";
    }

    private static string ResolveDefaultServerUrl(string? configuredServerUrl, string runtimeBaseUrl)
    {
        return NormalizeConfiguredServerUrl(configuredServerUrl) ?? runtimeBaseUrl;
    }

    private static string? NormalizeConfiguredServerUrl(string? value)
    {
        var normalized = NormalizeOptionalText(value);
        if (normalized == null)
        {
            return null;
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return normalized.TrimEnd('/');
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        var count = segments.Count;

        if (count >= 2 &&
            segments[count - 2].Equals("web", StringComparison.OrdinalIgnoreCase) &&
            segments[count - 1].Equals("index.html", StringComparison.OrdinalIgnoreCase))
        {
            segments.RemoveRange(count - 2, 2);
        }
        else if (count >= 1 && segments[count - 1].Equals("web", StringComparison.OrdinalIgnoreCase))
        {
            segments.RemoveAt(count - 1);
        }

        var path = segments.Count == 0 ? string.Empty : "/" + string.Join('/', segments);
        var builder = new UriBuilder(uri)
        {
            Path = path,
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private string ResolveWebRoot()
    {
        var overridePath = NormalizeOptionalText(Environment.GetEnvironmentVariable("MOONFIN_WEB_ROOT"));
        if (!string.IsNullOrWhiteSpace(overridePath) && Directory.Exists(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        var assemblyDir = Path.GetDirectoryName(_assembly.Location);
        if (!string.IsNullOrWhiteSpace(assemblyDir))
        {
            var localFrontend = Path.Combine(assemblyDir, "frontend");
            if (Directory.Exists(localFrontend))
            {
                return Path.GetFullPath(localFrontend);
            }

            var localWeb = Path.Combine(assemblyDir, "web");
            if (Directory.Exists(localWeb))
            {
                return Path.GetFullPath(localWeb);
            }

            // Local development fallback when running from the source tree.
            var repoFrontend = Path.GetFullPath(
                Path.Combine(assemblyDir, "..", "..", "..", "..", "frontend"));
            if (Directory.Exists(repoFrontend))
            {
                return repoFrontend;
            }
        }

        // Returned whether or not it exists, so a caller can name it in a diagnostic.
        var dataFolder = MoonfinPlugin.Instance?.DataFolderPath;
        if (!string.IsNullOrWhiteSpace(dataFolder))
        {
            return Path.GetFullPath(Path.Combine(dataFolder, "web"));
        }

        return string.Empty;
    }

    /// <summary>
    /// The assets normally live beside the assembly, so losing them usually means the plugin
    /// folder has gone, which a bare 404 gives an admin no way to tell from a routing problem.
    /// </summary>
    private void WarnAboutMissingWebRoot(string resolved)
    {
        if (Interlocked.Exchange(ref _missingWebRootLogged, 1) != 0)
        {
            return;
        }

        var assemblyDir = Path.GetDirectoryName(_assembly.Location);
        _logger.LogWarning(
            "The Moonfin web app is not on disk, so /Moonfin/Web/ has nothing to serve. Looked "
            + "beside the assembly at {AssemblyDir} for a frontend or web folder, then at "
            + "{Resolved}. Reinstall the plugin, or copy the release zip's frontend contents "
            + "into that second path.",
            assemblyDir,
            resolved);
    }

    private static bool TryResolvePath(string rootPath, string requestPath, out string fullPath)
    {
        var normalizedRequest = requestPath.Replace('\\', '/').TrimStart('/');
        var candidate = Path.Combine(
            rootPath,
            normalizedRequest.Replace('/', Path.DirectorySeparatorChar));
        fullPath = Path.GetFullPath(candidate);

        var normalizedRoot = Path.GetFullPath(rootPath);
        var rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return fullPath.StartsWith(rootWithSeparator, comparison) ||
               string.Equals(fullPath, normalizedRoot, comparison);
    }

    private static string GetContentType(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (ContentTypes.TryGetValue(extension, out var contentType))
        {
            return contentType;
        }

        return "application/octet-stream";
    }
}
