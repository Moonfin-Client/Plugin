using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace Moonfin.Server.Api;

/// <summary>
/// Serves plugin-bundled image assets (rating provider icons).
/// </summary>
[ApiController]
[Route("Moonfin/Assets")]
public sealed class MoonfinAssetsController : ControllerBase
{
    private static readonly Lazy<Dictionary<string, string>> ResourceMap = new(() =>
    {
        var asm = Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames();
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var n in names)
        {
            if (!n.Contains(".assets.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var idx = n.LastIndexOf(".assets.", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                continue;
            }

            // Resource name after ".assets." e.g. "Moonfin.Server.assets.IMDb.png" -> "IMDb.png"
            var file = n.Substring(idx + ".assets.".Length);
            if (!dict.ContainsKey(file))
            {
                dict[file] = n;
            }
        }

        return dict;
    });

    /// <summary>
    /// Get a bundled asset file by name.
    /// </summary>
    [HttpGet("{fileName}")]
    public IActionResult Get([FromRoute] string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 128)
        {
            return NotFound();
        }

        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..", StringComparison.Ordinal))
        {
            return NotFound();
        }

        var map = ResourceMap.Value;
        if (!map.TryGetValue(fileName, out var resName))
        {
            return NotFound();
        }

        var asm = Assembly.GetExecutingAssembly();
        var stream = asm.GetManifestResourceStream(resName);
        if (stream is null)
        {
            return NotFound();
        }

        var contentType = GetContentType(fileName) ?? "application/octet-stream";

        Response.Headers["Cache-Control"] = "public,max-age=31536000,immutable";
        Response.Headers["X-Content-Type-Options"] = "nosniff";

        return File(stream, contentType);
    }

    private static string? GetContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            _ => null
        };
    }
}
