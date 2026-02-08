using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Moonfin.Server.Api;

/// <summary>
/// Controller to serve Moonfin web plugin files.
/// </summary>
[ApiController]
[Route("Moonfin/Web")]
public class MoonfinWebController : ControllerBase
{
    private readonly Assembly _assembly;

    public MoonfinWebController()
    {
        _assembly = typeof(MoonfinWebController).Assembly;
    }

    /// <summary>
    /// Serves the Moonfin web plugin JavaScript file.
    /// </summary>
    /// <returns>The plugin.js file.</returns>
    [HttpGet("plugin.js")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetPluginJs()
    {
        var resourceName = "Moonfin.Server.Web.plugin.js";
        var stream = _assembly.GetManifestResourceStream(resourceName);

        if (stream == null)
        {
            return NotFound(new { Error = "plugin.js not found" });
        }

        return File(stream, "application/javascript");
    }

    /// <summary>
    /// Serves the Moonfin web plugin CSS file.
    /// </summary>
    /// <returns>The plugin.css file.</returns>
    [HttpGet("plugin.css")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetPluginCss()
    {
        var resourceName = "Moonfin.Server.Web.plugin.css";
        var stream = _assembly.GetManifestResourceStream(resourceName);

        if (stream == null)
        {
            return NotFound(new { Error = "plugin.css not found" });
        }

        return File(stream, "text/css");
    }

    /// <summary>
    /// Returns a small loader script that can be injected into index.html.
    /// This script loads the main plugin files.
    /// </summary>
    /// <returns>A JavaScript loader snippet.</returns>
    [HttpGet("loader.js")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ContentResult GetLoaderJs()
    {
        var loaderScript = @"
(function() {
    var link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = '/Moonfin/Web/plugin.css';
    document.head.appendChild(link);

    var script = document.createElement('script');
    script.src = '/Moonfin/Web/plugin.js';
    document.head.appendChild(script);
})();
";
        return Content(loaderScript, "application/javascript");
    }
}
