using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Models;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Common;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>
    /// Emby-side retro-games API exposing the /Moonfin/Games and /Moonfin/EmulatorJS routes
    /// the Moonfin client uses.
    /// </summary>
    public class GamesService : IService, IRequiresRequest, IHasResultFactory
    {
        private const long MaxSaveBytes = 32 * 1024 * 1024;
        private static readonly Assembly _assembly = typeof(GamesService).Assembly;

        private readonly IAuthorizationContext _authContext;

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        public GamesService(IApplicationHost appHost)
        {
            _authContext = appHost.Resolve<IAuthorizationContext>();
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
        }

        private object Json(object? body) => MoonfinJson.Result(Request, ResultFactory, body);
        private object Json(int statusCode, object? body) { Request.Response.StatusCode = statusCode; return Json(body); }
        private object NotFound() { Request.Response.StatusCode = 404; return null!; }

        private static bool GamesEnabled() => Plugin.Instance?.Configuration?.GamesEnabled == true;

        private GamesScanner? Scanner()
        {
            var lm = PluginServices.LibraryManager;
            return lm == null ? null : new GamesScanner(lm);
        }

        public object Get(GetGamesDebugRequest request)
        {
            // Intentionally not gated on GamesEnabled: this diagnoses why libraries aren't detected.
            var scanner = Scanner();
            if (scanner == null) return Json(new { error = "Library manager unavailable" });
            return Json(scanner.GetDiagnostics());
        }

        public object Get(GetGameLibrariesRequest request)
        {
            var scanner = Scanner();
            if (!GamesEnabled() || scanner == null) return Json(Array.Empty<object>());
            return Json(scanner.GetGameLibraries());
        }

        public object Get(GetGameSystemsRequest request)
        {
            var scanner = Scanner();
            if (!GamesEnabled() || scanner == null) return Json(Array.Empty<object>());
            return Json(scanner.GetSystems(request.LibraryId));
        }

        public object Get(GetGamesRequest request)
        {
            var scanner = Scanner();
            if (!GamesEnabled() || scanner == null) return Json(Array.Empty<object>());
            return Json(scanner.GetGames(request.LibraryId, request.System));
        }

        public object Get(GetGameDetailRequest request)
        {
            var scanner = Scanner();
            if (!GamesEnabled() || scanner == null) return NotFound();
            var game = scanner.GetGame(request.LibraryId, request.GameId);
            return game == null ? NotFound() : Json(game);
        }

        /// <summary>
        /// Streams a game's box art or screenshot, downloading and caching it on first ask. Takes a
        /// game token and a kind, never a URL, so this cannot be pointed at another host.
        /// </summary>
        public async Task<object> Get(GetGameThumbRequest request)
        {
            var scanner = Scanner();
            var thumbs = Plugin.Instance?.GameThumbs;
            if (!GamesEnabled() || scanner == null || thumbs == null) return NotFound();

            var source = scanner.ResolveThumbSource(request.LibraryId, request.GameId);
            if (source == null) return NotFound();

            var path = await thumbs
                .GetThumbPathAsync(source.Value.Core, source.Value.FileName, GameThumbService.ParseKind(request.Type))
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return NotFound();

            // Art for a given ROM name never changes, so let clients keep it.
            Request.Response.AddHeader("Cache-Control", "public,max-age=31536000,immutable");
            Request.Response.AddHeader("X-Content-Type-Options", "nosniff");
            return ResultFactory.GetStaticFileResult(Request, path);
        }

        public object Get(GetGameRomRequest request)
        {
            var scanner = Scanner();
            if (!GamesEnabled() || scanner == null) return NotFound();
            var path = scanner.ResolveFilePath(request.LibraryId, request.Token, allowBios: false);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return NotFound();
            if (GamesScanner.IsArchive(path))
            {
                byte[]? rom;
                try { rom = GamesScanner.ExtractRomFromArchive(path); }
                catch { return NotFound(); }
                if (rom == null || rom.Length == 0) return NotFound();
                // Unpack the archive in memory so the client gets raw ROM bytes. The file on disk is untouched.
                return ResultFactory.GetResult(Request, new MemoryStream(rom), "application/octet-stream", null);
            }
            return ResultFactory.GetStaticFileResult(Request, path);
        }

        public object Get(GetGameBiosRequest request)
        {
            var scanner = Scanner();
            if (!GamesEnabled() || scanner == null) return NotFound();
            var path = scanner.ResolveFilePath(request.LibraryId, request.Token, allowBios: true);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return NotFound();
            return ResultFactory.GetStaticFileResult(Request, path);
        }

        public object Head(HeadGameRomRequest request) =>
            SizeOnly(request.LibraryId, request.Token, allowBios: false);

        public object Head(HeadGameBiosRequest request) =>
            SizeOnly(request.LibraryId, request.Token, allowBios: true);

        // Answers with the length the matching GET would send and no body. An archived ROM is
        // measured from the archive index, since the GET serves the unpacked entry.
        private object SizeOnly(string libraryId, string token, bool allowBios)
        {
            var scanner = Scanner();
            if (!GamesEnabled() || scanner == null) return NotFound();
            var path = scanner.ResolveFilePath(libraryId, token, allowBios);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return NotFound();

            var length = !allowBios && GamesScanner.IsArchive(path)
                ? GamesScanner.GetExtractedRomLength(path)
                : new FileInfo(path).Length;
            if (length == null) return NotFound();

            return ResultFactory.GetStaticResult(Request, new StaticResultOptions
            {
                ContentType = "application/octet-stream",
                ContentLength = length,
                IsHeadRequest = true,
                SupportsRangeRequests = true,
                // A head request has no body to produce, but a factory is supplied so nothing
                // can trip over a missing one.
                ContentFactory = (start, end, cancel) => Task.FromResult(new StreamHandler
                {
                    Stream = Stream.Null,
                    Length = 0,
                    TotalLength = length.Value,
                }),
            });
        }

        public object Get(GetGameSaveRequest request)
        {
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return Json(401, new { error = "User not authenticated" });
            var data = GameSavesHelper.Get(userId.Value, request.GameId, request.Kind ?? "state");
            if (data == null) return NotFound();
            return ResultFactory.GetResult(Request, new MemoryStream(data), "application/octet-stream", null);
        }

        public async Task<object> Put(PutGameSaveRequest request)
        {
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return Json(401, new { error = "User not authenticated" });

            using var ms = new MemoryStream();
            if (request.RequestStream != null) await request.RequestStream.CopyToAsync(ms).ConfigureAwait(false);
            if (ms.Length == 0) return Json(400, new { error = "Empty save payload." });
            if (ms.Length > MaxSaveBytes) return Json(400, new { error = "Save payload too large." });

            GameSavesHelper.Save(userId.Value, request.GameId, request.Kind ?? "state", ms.ToArray());
            Request.Response.StatusCode = 204;
            return null!;
        }

        public object Get(GetCoresStatusRequest request)
        {
            return Json(GameCoresHelper.GetStatus());
        }

        public object Post(InstallCoresRequest request)
        {
            GameCoresHelper.StartInstall();
            return Json(202, GameCoresHelper.GetStatus());
        }

        public async Task<object> Post(UploadCoresRequest request)
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"emulatorjs-upload-{Guid.NewGuid():N}.zip");
            using (var dst = File.Create(tempFile))
            {
                if (request.RequestStream != null) await request.RequestStream.CopyToAsync(dst).ConfigureAwait(false);
            }

            if (!File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
            {
                try { File.Delete(tempFile); } catch { /* ignore */ }
                return Json(400, new { error = "Empty upload." });
            }

            GameCoresHelper.StartInstallFromFile(tempFile);
            return Json(202, GameCoresHelper.GetStatus());
        }

        public object Get(GetEmulatorPlayerRequest request)
        {
            var stream = _assembly.GetManifestResourceStream("Emby.Plugins.Moonfin.EmulatorJS.player.html");
            if (stream == null) return NotFound();
            string html;
            using (var reader = new StreamReader(stream)) html = reader.ReadToEnd();
            html = html.Replace("__EJS_PATHTODATA__", GameCoresHelper.ResolveDataPath());

            // Cores that need SharedArrayBuffer (e.g. PSP) only work in a cross-origin isolated
            // document. Send the isolation headers just for those so cartridge and single-threaded
            // disc cores keep loading normally.
            System.Collections.Generic.IDictionary<string, string>? headers = null;
            if (string.Equals(Request?.QueryString?.Get("core"), "psp", StringComparison.Ordinal))
            {
                headers = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["Cross-Origin-Opener-Policy"] = "same-origin",
                    ["Cross-Origin-Embedder-Policy"] = "credentialless"
                };
            }

            return ResultFactory.GetResult(Request, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html)), "text/html; charset=utf-8", headers);
        }

        public object Get(GetEmulatorDataRequest request)
        {
            var dataRoot = GameCoresHelper.LocalDataRoot();
            if (string.IsNullOrEmpty(dataRoot) || !Directory.Exists(dataRoot)) return NotFound();

            var requested = string.IsNullOrWhiteSpace(request.Path) ? "loader.js" : request.Path!;
            if (!TryResolveContainedPath(dataRoot, requested, out var fullPath) || !File.Exists(fullPath)) return NotFound();
            return ResultFactory.GetStaticFileResult(Request, fullPath);
        }

        private static bool TryResolveContainedPath(string rootPath, string requestPath, out string fullPath)
        {
            var normalizedRequest = requestPath.Replace('\\', '/').TrimStart('/');
            var candidate = Path.Combine(rootPath, normalizedRequest.Replace('/', Path.DirectorySeparatorChar));
            fullPath = Path.GetFullPath(candidate);
            var normalizedRoot = Path.GetFullPath(rootPath);
            var rootWithSep = normalizedRoot.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? normalizedRoot : normalizedRoot + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(rootWithSep, StringComparison.Ordinal) || string.Equals(fullPath, normalizedRoot, StringComparison.Ordinal);
        }
    }
}
