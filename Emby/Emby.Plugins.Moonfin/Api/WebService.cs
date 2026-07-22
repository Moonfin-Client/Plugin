using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Common;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    public class WebService : IService, IRequiresRequest, IHasResultFactory
    {
        private readonly Assembly _assembly = typeof(WebService).Assembly;

        private static readonly Regex BaseHrefRegex = new Regex(
            "<base\\s+href=\"[^\"]*\"\\s*/?>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Emby's static file server doesn't know the newer .mjs and .wasm extensions
        // and serves them as application/octet-stream, which Chrome rejects for module
        // scripts. Serve web assets with an explicit content type instead.
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

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        public WebService(IApplicationHost appHost)
        {
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
        }

        public object Get(GetPluginJsRequest request)
        {
            const string script = "(function(){if(window.location.pathname.toLowerCase().indexOf('/moonfin/web/')===-1){window.location.href='/Moonfin/Web/';}})();";
            return ResultFactory.GetResult(Request, ToStream(script), "application/javascript", null);
        }

        public object Get(GetPluginCssRequest request)
        {
            return ResultFactory.GetResult(Request, ToStream("/* moonfin css stub */"), "text/css", null);
        }

        public object Get(GetLoaderJsRequest request)
        {
            var stream = _assembly.GetManifestResourceStream("Emby.Plugins.Moonfin.Web.loader.js");
            if (stream == null) { Request.Response.StatusCode = 404; return new { error = "loader.js not found" }; }
            return ResultFactory.GetResult(Request, stream, "application/javascript", null);
        }

        private object ServeWebConfig()
        {
            var config = Plugin.Instance?.Configuration;
            var runtimeBaseUrl = ResolveRuntimeBaseUrl();
            var payload = new
            {
                schemaVersion = 1,
                defaultServerUrl = NormalizeConfiguredServerUrl(config?.WebDefaultServerUrl) ?? runtimeBaseUrl,
                discoveryProxyUrl = $"{runtimeBaseUrl}/Moonfin/Discovery/",
                enableWebRtcScan = config?.WebEnableWebRtcScan ?? true,
                brandingName = "Moonfin",
                pluginMode = true,
                forcedServerUrl = NormalizeConfiguredServerUrl(config?.WebForcedServerUrl)
            };

            Request.Response.AddHeader("Cache-Control", "no-store, no-cache, must-revalidate");
            Request.Response.AddHeader("Pragma", "no-cache");

            var json = JsonSerializer.Serialize(payload);
            return ResultFactory.GetResult(Request, ToStream(json), "application/json", null);
        }

        public object Get(GetWebAssetRequest request)
        {
            var webRoot = ResolveWebRoot();
            if (string.IsNullOrWhiteSpace(webRoot) || !Directory.Exists(webRoot))
            {
                Request.Response.StatusCode = 404;
                return new { error = "Moonfin web root not found" };
            }

            var requestedPath = string.IsNullOrWhiteSpace(request.Path) ? "index.html" : request.Path;

            // config.json is built per request and has no file on disk. It cannot have its own
            // route because Emby strips the .json suffix before matching, so serve it here.
            if (string.Equals(requestedPath, "config.json", StringComparison.OrdinalIgnoreCase))
            {
                return ServeWebConfig();
            }

            if (!TryResolvePath(webRoot, requestedPath, out var fullPath))
            { Request.Response.StatusCode = 404; return null!; }

            if (File.Exists(fullPath))
            {
                return ServeFile(fullPath);
            }

            if (Directory.Exists(fullPath))
            {
                var nested = Path.Combine(fullPath, "index.html");
                if (File.Exists(nested))
                {
                    // The request resolved to a directory's index (e.g. the theme editor at /theme).
                    // If the URL lacks a trailing slash, redirect to add one so the browser resolves
                    // the page's relative asset paths against the directory instead of its parent.
                    try
                    {
                        var uri = new Uri(Request.AbsoluteUri);
                        if (!uri.AbsolutePath.EndsWith("/"))
                        {
                            Request.Response.StatusCode = 302;
                            Request.Response.AddHeader("Location", uri.AbsolutePath + "/" + uri.Query);
                            return null!;
                        }
                    }
                    catch { /* fall through to serving the index directly */ }

                    return ServeFile(nested);
                }
            }

            if (Path.HasExtension(requestedPath)) { Request.Response.StatusCode = 404; return null!; }

            var indexPath = Path.Combine(webRoot, "index.html");
            if (!File.Exists(indexPath)) { Request.Response.StatusCode = 404; return null!; }
            return ServeFile(indexPath);
        }

        private object ServeFile(string fullPath)
        {
            var html = RewriteBaseHref(fullPath);
            if (html == null)
            {
                var ext = Path.GetExtension(fullPath).ToLowerInvariant();
                if (ContentTypes.TryGetValue(ext, out var contentType))
                {
                    return ResultFactory.GetStaticFileResult(Request, new StaticFileResultOptions
                    {
                        Path = fullPath,
                        ContentType = contentType,
                    });
                }
                return ResultFactory.GetStaticFileResult(Request, fullPath);
            }

            Request.Response.AddHeader("Cache-Control", "no-store, no-cache, must-revalidate");
            Request.Response.AddHeader("Pragma", "no-cache");
            Request.Response.AddHeader("Expires", "0");
            return ResultFactory.GetResult(Request, ToStream(html), "text/html; charset=utf-8", null);
        }

        /// <summary>
        /// The index rewritten to point at the sub-path a reverse proxy mounts Moonfin under, or
        /// null when the file should be served as it is on disk.
        /// </summary>
        private string? RewriteBaseHref(string fullPath)
        {
            if (!string.Equals(Path.GetFileName(fullPath), "index.html", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // The bundle is built with a base href of /Moonfin/Web/, which is already right when the
            // app is served from the domain root.
            var prefix = ForwardedPrefix();
            if (prefix.Length == 0)
            {
                return null;
            }

            // The theme editor ships its own index with no base href, so there is nothing to fix up.
            var original = File.ReadAllText(fullPath);
            return BaseHrefRegex.IsMatch(original)
                ? BaseHrefRegex.Replace(original, _ => $"<base href=\"{prefix}/Moonfin/Web/\">")
                : null;
        }

        private string ResolveRuntimeBaseUrl()
        {
            var host = Request.Headers["Host"] ?? "localhost";
            var baseUrl = $"{ResolveScheme()}://{host}".TrimEnd('/');

            var prefix = ForwardedPrefix();
            return prefix.Length == 0 ? baseUrl : baseUrl + prefix;
        }

        /// <summary>
        /// The scheme the client actually reached the server on. A reverse proxy reports the public
        /// scheme through a header, since the hop to Emby is plain http. Without a proxy, the scheme
        /// of the request itself is what the browser used, so a plain-http LAN server is not handed
        /// an https URL it does not serve.
        /// </summary>
        private string ResolveScheme()
        {
            var forwarded = Request.Headers["X-Forwarded-Proto"];
            if (!string.IsNullOrEmpty(forwarded))
            {
                return forwarded.Split(',')[0].Trim().ToLowerInvariant();
            }

            if (Uri.TryCreate(Request.AbsoluteUri, UriKind.Absolute, out var uri))
            {
                return uri.Scheme;
            }

            return Request.IsLocal ? "http" : "https";
        }

        /// <summary>
        /// The sub-path a reverse proxy mounts Moonfin under, or empty when it is served from the
        /// domain root. Emby's request has no PathBase, so the prefix only reaches us in the header
        /// the proxy sets.
        /// </summary>
        private string ForwardedPrefix()
        {
            var prefix = Request.Headers["X-Forwarded-Prefix"];
            if (string.IsNullOrEmpty(prefix)) prefix = Request.Headers["X-Forwarded-Path"];
            return NormalizeForwardedPrefix(prefix);
        }

        private static string NormalizeForwardedPrefix(string? prefix)
        {
            prefix = prefix?.Trim();
            if (string.IsNullOrEmpty(prefix) || prefix == "/") return string.Empty;
            if (!prefix.StartsWith("/")) prefix = "/" + prefix;
            return prefix.TrimEnd('/');
        }

        private static string? NormalizeConfiguredServerUrl(string? value)
        {
            value = value?.Trim();
            if (string.IsNullOrEmpty(value)) return null;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value.TrimEnd('/');

            var segments = new List<string>(
                uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries));
            var count = segments.Count;
            if (count >= 2 && string.Equals(segments[count - 2], "web", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(segments[count - 1], "index.html", StringComparison.OrdinalIgnoreCase))
                segments.RemoveRange(count - 2, 2);
            else if (count >= 1 && string.Equals(segments[count - 1], "web", StringComparison.OrdinalIgnoreCase))
                segments.RemoveAt(count - 1);

            var path = segments.Count == 0 ? string.Empty : "/" + string.Join("/", segments);
            return new UriBuilder(uri) { Path = path, Query = string.Empty, Fragment = string.Empty }.Uri
                .GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        private string ResolveWebRoot()
        {
            var envOverride = Environment.GetEnvironmentVariable("MOONFIN_WEB_ROOT")?.Trim();
            if (!string.IsNullOrEmpty(envOverride) && Directory.Exists(envOverride)) return Path.GetFullPath(envOverride);

            // The bundle ships as a web/ folder beside the dll in the plugins directory. Emby loads
            // the dll from memory so _assembly.Location is usually empty, which is why the plugins
            // path comes first and the assembly directory is only a fallback for hosts that do load
            // it from disk.
            foreach (var baseDir in new[] { Plugin.Instance?.PluginsPath, Path.GetDirectoryName(_assembly.Location) })
            {
                if (string.IsNullOrEmpty(baseDir)) continue;
                foreach (var folder in new[] { "web", "frontend" })
                {
                    var candidate = Path.Combine(baseDir, folder);
                    if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
                }
            }

            var dataPath = Plugin.Instance?.DataFolderPath;
            if (!string.IsNullOrEmpty(dataPath))
                return Path.GetFullPath(Path.Combine(dataPath, "web"));

            return string.Empty;
        }

        private static bool TryResolvePath(string rootPath, string requestPath, out string fullPath)
        {
            var normalizedRequest = requestPath.Replace('\\', '/').TrimStart('/');
            var candidate = Path.Combine(rootPath, normalizedRequest.Replace('/', Path.DirectorySeparatorChar));
            fullPath = Path.GetFullPath(candidate);

            var normalizedRoot = Path.GetFullPath(rootPath);
            var rootWithSep = normalizedRoot.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? normalizedRoot : normalizedRoot + Path.DirectorySeparatorChar;
            var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return fullPath.StartsWith(rootWithSep, comparison) || string.Equals(fullPath, normalizedRoot, comparison);
        }

        private static Stream ToStream(string text) => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
    }
}
