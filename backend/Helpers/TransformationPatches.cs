using System.Reflection;
using System.Text.RegularExpressions;
using Moonfin.Server.Models;

namespace Moonfin.Server.Helpers;

/// <summary>
/// Static callbacks invoked by the File Transformation plugin to inject
/// Moonfin's frontend assets into Jellyfin's served web files.
/// </summary>
public static class TransformationPatches
{
    /// <summary>
    /// Injects the Moonfin loader script into index.html before the closing &lt;/head&gt; tag.
    /// </summary>
    public static string IndexHtml(PatchRequestPayload payload)
    {
        if (string.IsNullOrEmpty(payload.Contents))
        {
            return payload.Contents ?? string.Empty;
        }

        var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Moonfin.Server.Web.inject.html");

        if (stream == null)
        {
            var fallbackScript = "<script src=\"/Moonfin/Web/loader.js\"></script>";
            return Regex.Replace(payload.Contents, "(</head>)", $"{fallbackScript}$1");
        }

        using var reader = new StreamReader(stream);
        var injectHtml = reader.ReadToEnd();

        return Regex.Replace(payload.Contents, "(</head>)", $"{injectHtml}$1");
    }
}
