using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Common;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Services;
using Moonfin.Games;
using Moonfin.Romm;
using SharedGamesService = Moonfin.Games.GamesService;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>
    /// Emby-side retro-games API exposing the /Moonfin/Games and /Moonfin/EmulatorJS routes
    /// the Moonfin client uses.
    /// </summary>
    public class GamesService : IService, IRequiresRequest, IHasResultFactory
    {
        private const long MaxSaveBytes = 32 * 1024 * 1024;
        private const long MaxRommArchiveBytes = 512L * 1024 * 1024;
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

        private static SharedGamesService? Games() => Plugin.Instance?.Games;

        private static GameArtworkReconciliationService? Artwork() => Plugin.Instance?.GameArtwork;

        private Guid? UserId() => AuthHelpers.GetCurrentUserId(Request, _authContext);

        // A user linked to RomM browses and downloads with their own RomM account.
        private RommSource? Romm() => Plugin.Instance?.Romm?.ForUser(UserId());

        public object Get(GetGamesDebugRequest request)
        {
            // Intentionally not gated on GamesEnabled: this diagnoses why libraries aren't detected.
            var games = Games();
            if (games == null) return Json(new { error = "Library manager unavailable" });
            return Json(games.GetDiagnostics());
        }

        public object Get(GetGameLibrariesRequest request)
        {
            var games = Games();
            if (!GamesEnabled() || games == null) return Json(Array.Empty<object>());
            return Json(games.GetGameLibraries());
        }

        public async Task<object> Get(GetGameSystemsRequest request)
        {
            var artwork = Artwork();
            if (!GamesEnabled() || artwork == null) return Json(Array.Empty<object>());

            var systems = (await artwork.GetSystemsAsync(request.LibraryId, UserId(), Request.CancellationToken).ConfigureAwait(false)).ToList();

            var artworkBySystem = await artwork
                .GetSystemArtworkBatchAsync(request.LibraryId, systems.Select(system => system.Id), Request.CancellationToken)
                .ConfigureAwait(false);
            GameArtworkApi.AttachPreviews(request.LibraryId, systems, artworkBySystem);
            return Json(systems);
        }

        public object Get(GetGamesRequest request)
        {
            var games = Games();
            if (!GamesEnabled() || games == null) return Json(Array.Empty<object>());
            return Json(games.GetGames(request.LibraryId, request.System, UserId()));
        }

        public async Task<object> Get(GetGameDetailRequest request)
        {
            var games = Games();
            if (!GamesEnabled() || games == null) return NotFound();
            var game = await games.GetGameAsync(request.LibraryId, request.GameId, UserId(), Request.CancellationToken).ConfigureAwait(false);
            return game == null ? NotFound() : Json(game);
        }

        /// <summary>
        /// Sets or clears the current user's explicit arcade emulator choice. A null core returns the
        /// game to its server-selected recommendation.
        /// </summary>
        public async Task<object> Put(PutGameCoreRequest request)
        {
            var games = Games();
            if (!GamesEnabled() || games == null) return NotFound();
            var userId = UserId();
            if (userId == null) return Json(401, new { error = "User not authenticated" });
            var body = await MoonfinJson.ReadBodyAsync<CoreOverrideBody>(request.RequestStream).ConfigureAwait(false);
            if (body == null) return Json(400, new { error = "A JSON body is required." });

            try
            {
                var game = await games.SetCoreOverrideAsync(request.LibraryId, request.GameId, userId.Value, body.Core, Request.CancellationToken).ConfigureAwait(false);
                return game == null ? NotFound() : Json(game);
            }
            catch (ArgumentException)
            {
                return Json(400, new { error = "The requested core is not available for this game." });
            }
        }

        /// <summary>
        /// Sets or clears the current user's explicit player backend. EmulatorJS is a player backend
        /// rather than a libretro core, so this is available for every game system.
        /// </summary>
        public async Task<object> Put(PutGameBackendRequest request)
        {
            var games = Games();
            if (!GamesEnabled() || games == null) return NotFound();
            var userId = UserId();
            if (userId == null) return Json(401, new { error = "User not authenticated" });
            var body = await MoonfinJson.ReadBodyAsync<BackendOverrideBody>(request.RequestStream).ConfigureAwait(false);
            if (body == null) return Json(400, new { error = "A JSON body is required." });

            try
            {
                var game = await games.SetBackendOverrideAsync(request.LibraryId, request.GameId, userId.Value, body.Backend, Request.CancellationToken).ConfigureAwait(false);
                return game == null ? NotFound() : Json(game);
            }
            catch (ArgumentException)
            {
                return Json(400, new { error = "The requested game backend is not available." });
            }
        }

        public object Get(GetArtworkCapabilitiesRequest request) => Json(GameArtworkApi.Capabilities());

        /// <summary>Returns one system's locally cataloged artwork state without scheduling provider work.</summary>
        public async Task<object> Get(GetArtworkManifestRequest request)
        {
            var artwork = Artwork();
            if (!GamesEnabled() || artwork == null || string.IsNullOrWhiteSpace(request.System)) return ArtworkNotFound(confirmedMissing: false);

            var system = await artwork.GetSystemArtworkAsync(request.LibraryId, request.System!, Request.CancellationToken).ConfigureAwait(false);
            if (system == null) return ArtworkNotFound(confirmedMissing: false);

            // The generation query is what clients send back, the ETag is there for plain HTTP caches.
            var currentGeneration = GameArtworkApi.Generation(system.System.Generation);
            Request.Response.AddHeader("ETag", "\"" + currentGeneration + "\"");
            if (string.Equals(request.Generation, currentGeneration, StringComparison.Ordinal))
            {
                Request.Response.StatusCode = 304;
                return null!;
            }

            return Json(GameArtworkApi.Manifest(request.LibraryId, system));
        }

        /// <summary>Moves already cataloged artwork up the queue in the order the client asks for.</summary>
        public async Task<object> Post(PostArtworkPriorityRequest request)
        {
            var artwork = Artwork();
            if (!GamesEnabled() || artwork == null) return NotFound();

            var body = await MoonfinJson.ReadBodyAsync<GameArtworkPriorityRequest>(request.RequestStream).ConfigureAwait(false);
            if (body == null || string.IsNullOrWhiteSpace(body.SystemId) || body.Items == null)
            {
                return Json(400, new { error = "A systemId is required." });
            }

            if (body.Items.Count > GameArtworkApi.MaxArtworkPriorityItems)
            {
                return Json(400, new { error = $"At most {GameArtworkApi.MaxArtworkPriorityItems} artwork priority items may be submitted at once." });
            }

            var system = await artwork.GetSystemArtworkAsync(request.LibraryId, body.SystemId, Request.CancellationToken).ConfigureAwait(false);
            if (system == null) return NotFound();

            if (!string.Equals(body.KnownGeneration, GameArtworkApi.Generation(system.System.Generation), StringComparison.Ordinal))
            {
                return Json(400, new { error = "The artwork generation is no longer current." });
            }

            // One snapshot of the library for the whole request, so a full body doesn't walk the ROM folders per item.
            var lookup = artwork.CreateGameLookup(request.LibraryId);

            var seenGames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in body.Items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.GameId) || !seenGames.Add(item.GameId) ||
                    !system.GameIds.Contains(item.GameId, StringComparer.Ordinal) ||
                    !artwork.IsCurrentGameMember(request.LibraryId, item.GameId, lookup) ||
                    item.Roles == null || item.Roles.Count == 0)
                {
                    return Json(400, new { error = "Priority items must name distinct games in the requested system and at least one role." });
                }

                var seenRoles = new HashSet<string>(StringComparer.Ordinal);
                if (item.Roles.Any(role => !GameArtworkApi.IsRole(role) || !seenRoles.Add(role)))
                {
                    return Json(400, new { error = "Artwork roles must be distinct boxart, snap, or title values." });
                }
            }

            foreach (var item in body.Items)
            {
                foreach (var role in item.Roles)
                {
                    await artwork.PromoteAsync(request.LibraryId, item.GameId, role, lookup, Request.CancellationToken).ConfigureAwait(false);
                }
            }

            Request.Response.StatusCode = 204;
            return null!;
        }

        /// <summary>Serves one versioned artwork file without changing the catalog or the queue.</summary>
        public async Task<object> Get(GetGameArtworkRequest request)
        {
            var artwork = Artwork();
            if (!GamesEnabled() || artwork == null || !GameArtworkApi.IsRole(request.Role)) return ArtworkNotFound(confirmedMissing: false);

            var entry = await artwork.GetArtworkAsync(request.LibraryId, request.GameId, request.Role, Request.CancellationToken).ConfigureAwait(false);
            if (entry == null) return ArtworkNotFound(confirmedMissing: false);
            if (entry.Entry.State == ArtworkCatalogState.Missing) return ArtworkNotFound(confirmedMissing: true);

            var asset = artwork.ResolveLocalArtwork(entry.Entry, request.Revision);
            return asset == null
                ? ArtworkNotFound(confirmedMissing: false)
                : await ServeArtworkAsync(asset, versionedUrl: true).ConfigureAwait(false);
        }

        /// <summary>Reopens one game's artwork and queues it again.</summary>
        public async Task<object> Post(RefreshGameArtworkRequest request)
        {
            var artwork = Artwork();
            if (!GamesEnabled() || artwork == null) return NotFound();

            var role = GameArtworkApi.NormalizeLegacyRole(request.Type);
            if (request.Type != null && !GameArtworkApi.IsRole(request.Type))
            {
                return Json(400, new { error = "Artwork type must be boxart, snap, or title." });
            }

            if (!await artwork.RequestRefreshAsync(request.LibraryId, request.GameId, role, Request.CancellationToken).ConfigureAwait(false))
            {
                return NotFound();
            }

            Request.Response.StatusCode = 202;
            return null!;
        }

        /// <summary>
        /// Runs in the background. Checks the library's game metadata for updates, then reopens
        /// artwork that never found an original.
        /// </summary>
        public object Post(RefreshLibraryArtworkRequest request)
        {
            var artwork = Artwork();
            if (!GamesEnabled() || artwork == null) return NotFound();

            switch (artwork.StartLibraryArtworkRefresh(request.LibraryId, request.System))
            {
                case LibraryArtworkRefreshStatus.NotFound:
                    return NotFound();
                case LibraryArtworkRefreshStatus.BadSystem:
                    return Json(400, new { error = "Unknown system." });
                case LibraryArtworkRefreshStatus.Conflict:
                    return Json(409, new { error = "An artwork refresh for this library is already running." });
                default:
                    Request.Response.StatusCode = 202;
                    return null!;
            }
        }

        /// <summary>
        /// The thumb route older apps use. It only reads the catalog and can move queued work
        /// forward, while downloads and resizing stay with the background workers.
        /// </summary>
        public async Task<object> Get(GetGameThumbRequest request)
        {
            var artwork = Artwork();
            if (!GamesEnabled() || artwork == null) return ArtworkNotFound(confirmedMissing: false);

            var role = GameArtworkApi.NormalizeLegacyRole(request.Type);
            var cancellationToken = Request.CancellationToken;
            var entry = await artwork.GetArtworkAsync(request.LibraryId, request.GameId, role, cancellationToken).ConfigureAwait(false);
            if (entry == null)
            {
                // Reconciliation may not have reached this game yet. Queue local catalog work, but
                // never look anything up or encode on the request path.
                if (await artwork.RequestRefreshAsync(request.LibraryId, request.GameId, role, cancellationToken).ConfigureAwait(false))
                {
                    return RetryableArtworkResponse(503, TimeSpan.FromSeconds(5));
                }

                return ArtworkNotFound(confirmedMissing: false);
            }

            if (entry.Entry.State == ArtworkCatalogState.Missing) return ArtworkNotFound(confirmedMissing: true);

            if (entry.Entry.State == ArtworkCatalogState.Pending)
            {
                await artwork.PromoteAsync(request.LibraryId, request.GameId, role, cancellationToken).ConfigureAwait(false);
                return RetryableArtworkResponse(503, TimeSpan.FromSeconds(5));
            }

            if (entry.Entry.State == ArtworkCatalogState.Retryable)
            {
                await artwork.PromoteAsync(request.LibraryId, request.GameId, role, cancellationToken).ConfigureAwait(false);
                var retry = entry.Entry.RetryAfterUtc - DateTimeOffset.UtcNow;
                return RetryableArtworkResponse(504, retry.GetValueOrDefault(TimeSpan.FromSeconds(5)));
            }

            var asset = artwork.ResolveLocalArtwork(
                entry.Entry,
                entry.Entry.Revision.ToString(CultureInfo.InvariantCulture),
                GameArtworkApi.WantsFullResolution(request.Full));
            return asset == null
                ? ArtworkNotFound(confirmedMissing: false)
                : await ServeArtworkAsync(asset, versionedUrl: false).ConfigureAwait(false);
        }

        /// <summary>
        /// Serves one local artwork file under the caller's delivery lease. The file is read while
        /// the lease is held, since Emby writes a returned stream after this method has finished.
        /// </summary>
        private async Task<object> ServeArtworkAsync(GameArtworkLocalAsset asset, bool versionedUrl)
        {
            DateTime lastModified;
            try { lastModified = File.GetLastWriteTimeUtc(asset.Path); }
            catch (IOException) { return ArtworkNotFound(confirmedMissing: false); }

            var cacheControl = GameArtworkApi.CacheControl(versionedUrl, asset.IsThumbnail);
            var lastModifiedText = lastModified.ToString("R", CultureInfo.InvariantCulture);
            var since = Request.Headers?.Get("If-Modified-Since");
            if (since != null &&
                DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var sinceTime) &&
                lastModified.AddTicks(-(lastModified.Ticks % TimeSpan.TicksPerSecond)) <= sinceTime.UtcDateTime)
            {
                Request.Response.AddHeader("Cache-Control", cacheControl);
                Request.Response.AddHeader("Last-Modified", lastModifiedText);
                Request.Response.StatusCode = 304;
                return null!;
            }

            var limiter = Plugin.Instance?.GameArtworkDelivery;
            IDisposable? lease = null;
            byte[] bytes;
            try
            {
                if (limiter != null) lease = await limiter.AcquireAsync(UserId(), Request.CancellationToken).ConfigureAwait(false);
                bytes = await File.ReadAllBytesAsync(asset.Path, Request.CancellationToken).ConfigureAwait(false);
            }
            catch (ArtworkDeliveryUnavailableException ex)
            {
                return RetryableArtworkResponse(503, ex.RetryAfter);
            }
            catch (IOException)
            {
                return ArtworkNotFound(confirmedMissing: false);
            }
            finally
            {
                lease?.Dispose();
            }

            // Emby replaces a Cache-Control header with its own no-store, so caching goes through
            // the static result's duration and date instead.
            return await ResultFactory.GetStaticResult(Request, new StaticResultOptions
            {
                ContentType = asset.ContentType,
                ContentLength = bytes.Length,
                CacheKey = ArtworkCacheKey(asset.Path, lastModified),
                CacheDuration = GameArtworkApi.CacheDuration(versionedUrl, asset.IsThumbnail),
                AllowPublicCache = false,
                DateLastModified = new DateTimeOffset(lastModified, TimeSpan.Zero),
                ResponseHeaders = new Dictionary<string, string> { ["X-Content-Type-Options"] = "nosniff" },
                ContentFactory = (start, end, cancel) => Task.FromResult(new StreamHandler
                {
                    Stream = new MemoryStream(bytes),
                    Length = bytes.Length,
                    TotalLength = bytes.Length,
                }),
            }).ConfigureAwait(false);
        }

        private static Guid ArtworkCacheKey(string path, DateTime lastModified)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            return new Guid(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(path + "|" + lastModified.Ticks.ToString(CultureInfo.InvariantCulture))));
        }

        private object RetryableArtworkResponse(int statusCode, TimeSpan retryAfter)
        {
            Request.Response.AddHeader("Retry-After", GameArtworkApi.RetryAfterSeconds(retryAfter).ToString(CultureInfo.InvariantCulture));
            Request.Response.AddHeader("Cache-Control", "no-store");
            Request.Response.StatusCode = statusCode;
            return null!;
        }

        private object ArtworkNotFound(bool confirmedMissing)
        {
            Request.Response.AddHeader("Cache-Control", confirmedMissing ? "private,max-age=300" : "no-store");
            return NotFound();
        }

        public object Get(GetArcadeCompatibilityStatusRequest request)
        {
            var arcade = Plugin.Instance?.ArcadeCompatibility;
            return arcade == null ? NotFound() : Json(arcade.GetStatus());
        }

        // Real MAME DATs run 200 MB and up.
        private const long MaxDatUploadBytes = 512L * 1024 * 1024;

        /// <summary>
        /// Replaces one local compatibility DAT with an administrator-supplied, version-pinned XML
        /// snapshot. This never downloads upstream data itself.
        /// </summary>
        public async Task<object> Put(PutArcadeCompatibilityDatRequest request)
        {
            var arcade = Plugin.Instance?.ArcadeCompatibility;
            if (arcade == null) return NotFound();

            // Emby has no per-route body limit, so the upload is spooled to a temp file under the cap first.
            var temp = Path.Combine(Path.GetTempPath(), $"moonfin-dat-{Guid.NewGuid():N}.xml");
            try
            {
                using (var dst = File.Create(temp))
                {
                    if (request.RequestStream != null && !await CopyCappedAsync(request.RequestStream, dst, MaxDatUploadBytes).ConfigureAwait(false))
                    {
                        return Json(413, new { error = $"The uploaded DAT exceeds the {MaxDatUploadBytes / (1024 * 1024)} MB limit for this endpoint." });
                    }
                }

                using var source = File.OpenRead(temp);
                await arcade.InstallDatAsync(request.Core, source, Request.CancellationToken).ConfigureAwait(false);
                Request.Response.StatusCode = 204;
                return null!;
            }
            catch (EmptyArcadeDatException)
            {
                return Json(400, new { error = "The uploaded DAT contains no usable game or machine sets." });
            }
            catch (ArgumentException)
            {
                return Json(400, new { error = "Use 'arcade' for FBNeo or 'mame' for MAME." });
            }
            catch (System.Xml.XmlException)
            {
                return Json(400, new { error = "The uploaded DAT is not valid XML." });
            }
            finally
            {
                try { File.Delete(temp); }
                catch (IOException)
                {
                    // A temp file left behind is cleared with the rest of the temp folder.
                }
            }
        }

        /// <summary>Copies at most maxBytes. False when the source held more.</summary>
        private static async Task<bool> CopyCappedAsync(Stream source, Stream destination, long maxBytes)
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes) return false;
                await destination.WriteAsync(buffer, 0, read).ConfigureAwait(false);
            }

            return true;
        }

        public async Task<object> Get(GetGameRomRequest request)
        {
            var games = Games();
            if (!GamesEnabled() || games == null) return NotFound();
            var romm = Romm();
            if (romm?.Owns(request.LibraryId) == true) return await ServeRommRomAsync(romm, request.Token).ConfigureAwait(false);
            var path = games.ResolveFilePath(request.LibraryId, request.Token, allowBios: false);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return NotFound();
            if (GameSystemCoreResolver.IsCueSheet(path))
            {
                var discSet = DiscSet.ForCueSheet(path);
                return discSet == null ? NotFound() : new DiscSetZipResult(discSet);
            }
            if (SharedGamesService.IsArchive(path))
            {
                var core = games.SystemCoreFor(request.LibraryId, path) ?? string.Empty;
                if (GameSystemCoreResolver.ShouldPreserveArchiveForCore(core, path))
                    return await ResultFactory.GetStaticFileResult(Request, path).ConfigureAwait(false);
                if (GameSystemCoreResolver.ShouldRepackArchiveForCore(core, path))
                {
                    var discSet = DiscSet.ForSevenZip(path);
                    return discSet == null ? NotFound() : new DiscSetZipResult(discSet);
                }

                byte[]? rom;
                try { rom = SharedGamesService.ExtractRomFromArchive(path); }
                catch { return NotFound(); }
                if (rom == null || rom.Length == 0) return NotFound();
                // Unpack the archive in memory so the client gets raw ROM bytes. The file on disk is untouched.
                return ResultFactory.GetResult(Request, new MemoryStream(rom), "application/octet-stream", null);
            }
            return await ResultFactory.GetStaticFileResult(Request, path).ConfigureAwait(false);
        }

        public async Task<object> Get(GetGameBiosRequest request)
        {
            var games = Games();
            if (!GamesEnabled() || games == null) return NotFound();
            var romm = Romm();
            if (romm?.Owns(request.LibraryId) == true)
            {
                var firmware = romm.FindFirmware(request.Token);
                var client = romm.CreateClient();
                if (firmware == null || client == null) return NotFound();
                return await ProxyRommAsync(range => client.SendFirmwareAsync(HttpMethod.Get, firmware, range, Request.CancellationToken)).ConfigureAwait(false);
            }

            var path = games.ResolveFilePath(request.LibraryId, request.Token, allowBios: true);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return NotFound();
            return await ResultFactory.GetStaticFileResult(Request, path).ConfigureAwait(false);
        }

        public object Head(HeadGameRomRequest request)
        {
            var romm = Romm();
            if (GamesEnabled() && romm?.Owns(request.LibraryId) == true)
            {
                // RomM listed every size, unpacked archives included, so no request goes upstream.
                var game = romm.FindGame(request.Token);
                return game == null ? NotFound() : HeadResult(game.ServedSizeBytes);
            }

            return SizeOnly(request.LibraryId, request.Token, allowBios: false);
        }

        public object Head(HeadGameBiosRequest request)
        {
            var romm = Romm();
            if (GamesEnabled() && romm?.Owns(request.LibraryId) == true)
            {
                var firmware = romm.FindFirmware(request.Token);
                return firmware == null ? NotFound() : HeadResult(firmware.SizeBytes);
            }

            return SizeOnly(request.LibraryId, request.Token, allowBios: true);
        }

        // Answers with the length the matching GET would send and no body. An archived ROM is
        // measured from the archive index, since the GET serves the unpacked entry.
        private object SizeOnly(string libraryId, string token, bool allowBios)
        {
            var games = Games();
            if (!GamesEnabled() || games == null) return NotFound();
            var path = games.ResolveFilePath(libraryId, token, allowBios);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return NotFound();

            long? length = new FileInfo(path).Length;
            if (!allowBios && GameSystemCoreResolver.IsCueSheet(path))
                length = DiscSet.ForCueSheet(path)?.Length;
            else if (!allowBios && SharedGamesService.IsArchive(path))
            {
                var core = games.SystemCoreFor(libraryId, path) ?? string.Empty;
                if (GameSystemCoreResolver.ShouldRepackArchiveForCore(core, path))
                    length = DiscSet.ForSevenZip(path)?.Length;
                else if (!GameSystemCoreResolver.ShouldPreserveArchiveForCore(core, path))
                    length = SharedGamesService.GetExtractedRomInfo(path)?.Length;
            }
            if (length == null) return NotFound();

            return HeadResult(length.Value);
        }

        private object HeadResult(long length)
        {
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
                    TotalLength = length,
                }),
            });
        }

        public async Task<object> Get(GetRommAccountRequest request)
        {
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return Json(401, new { error = "User not authenticated" });
            var accounts = Plugin.Instance?.Romm?.Accounts;
            return Json(accounts == null || !GamesEnabled()
                ? new RommAccountStatus()
                : await accounts.StatusAsync(userId.Value, Request.CancellationToken).ConfigureAwait(false));
        }

        public async Task<object> Post(LinkRommAccountRequest request)
        {
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return Json(401, new { error = "User not authenticated" });
            var accounts = Plugin.Instance?.Romm?.Accounts;
            return Json(accounts == null || !GamesEnabled()
                ? new RommAccountStatus()
                : await accounts.StartLinkAsync(userId.Value, Request.CancellationToken).ConfigureAwait(false));
        }

        public object Delete(UnlinkRommAccountRequest request)
        {
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return Json(401, new { error = "User not authenticated" });
            return Json(Plugin.Instance?.Romm?.Accounts?.Unlink(userId.Value) ?? new RommAccountStatus());
        }

        public async Task<object> Post(TestRommRequest request)
        {
            var romm = Romm();
            var baseUrl = RommClient.NormalizeBaseUrl(request.Url);
            var token = RommClient.NormalizeToken(request.Token);
            if (romm == null) return Json(new RommTestResult { Error = "The plugin is still starting." });
            if (baseUrl == null) return Json(new RommTestResult { Error = "Enter the RomM address, like http://192.168.1.20:8080." });
            if (token == null) return Json(new RommTestResult { Error = "Enter a RomM client token." });

            try
            {
                var info = await romm.ClientFor(baseUrl, token).CheckAsync(Request.CancellationToken).ConfigureAwait(false);
                return Json(new RommTestResult
                {
                    Ok = true,
                    Version = info.Version,
                    PlatformCount = info.PlatformCount,
                    SupportedPlatformCount = info.SupportedPlatformCount,
                    SupportedRomCount = info.SupportedRomCount,
                });
            }
            catch (Exception ex) when (ex is JsonException || ex is RommRequestException { StatusCode: 404 })
            {
                return Json(new RommTestResult { Error = "That address answered, but not like a RomM server." });
            }
            catch (RommRequestException ex)
            {
                return Json(new RommTestResult { Error = ex.Message + "." });
            }
            catch (HttpRequestException)
            {
                return Json(new RommTestResult { Error = "Couldn't reach RomM at that address." });
            }
            catch (TaskCanceledException)
            {
                return Json(new RommTestResult { Error = "RomM didn't answer in time." });
            }
        }

        private async Task<object> ServeRommRomAsync(RommSource romm, string gameId)
        {
            var game = romm.FindGame(gameId);
            var client = romm.CreateClient();
            if (game == null || client == null) return NotFound();

            if (!game.UnpackArchive && !game.RepackArchive)
            {
                return await ProxyRommAsync(range => client.SendRomAsync(HttpMethod.Get, game, range, Request.CancellationToken)).ConfigureAwait(false);
            }

            // A zipped ROM is unpacked here and a PlayStation .7z is repacked as a zip, the same
            // way a folder library does it.
            var temp = Path.Combine(Path.GetTempPath(), $"moonfin-romm-{Guid.NewGuid():N}{Path.GetExtension(game.Rom.FileName)}");
            var tempInUse = false;
            try
            {
                var status = await client.DownloadRomAsync(game, temp, MaxRommArchiveBytes, Request.CancellationToken).ConfigureAwait(false);
                if (status == HttpStatusCode.RequestEntityTooLarge)
                {
                    return Json(413, new { error = "The archive exceeds the " + (MaxRommArchiveBytes / (1024 * 1024)) + " MB limit for extracted ROMs." });
                }

                if (status != HttpStatusCode.OK) return RommFailure(status);

                if (game.RepackArchive)
                {
                    var discSet = DiscSet.ForSevenZip(temp);
                    if (discSet == null) return NotFound();
                    // The zip is written after this returns, so the result deletes the download.
                    tempInUse = true;
                    return new DiscSetZipResult(discSet, temp);
                }

                byte[]? rom;
                try { rom = SharedGamesService.ExtractRomFromArchive(temp); }
                catch { return NotFound(); }
                if (rom == null || rom.Length == 0) return NotFound();
                return ResultFactory.GetResult(Request, new MemoryStream(rom), "application/octet-stream", null);
            }
            catch (HttpRequestException)
            {
                Request.Response.StatusCode = 502;
                return null!;
            }
            finally
            {
                try { if (!tempInUse) File.Delete(temp); }
                catch (IOException)
                {
                    // A temp file left behind is cleared with the rest of the temp folder.
                }
            }
        }

        /// <summary>
        /// Streams a RomM file to the app. A byte range is passed through so a client can resume or
        /// probe the size the way it does against a file on disk.
        /// </summary>
        private async Task<object> ProxyRommAsync(Func<string?, Task<HttpResponseMessage>> send)
        {
            HttpResponseMessage upstream;
            try
            {
                upstream = await send(Request.Headers?.Get("Range")).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                Request.Response.StatusCode = 502;
                return null!;
            }
            catch (TaskCanceledException)
            {
                Request.Response.StatusCode = 504;
                return null!;
            }

            if (upstream.StatusCode != HttpStatusCode.OK && upstream.StatusCode != HttpStatusCode.PartialContent)
            {
                using (upstream) return RommFailure(upstream.StatusCode);
            }

            var headers = new Dictionary<string, string> { ["Accept-Ranges"] = "bytes" };
            if (upstream.Content.Headers.ContentLength is long length)
            {
                headers["Content-Length"] = length.ToString(CultureInfo.InvariantCulture);
            }

            if (upstream.Content.Headers.ContentRange != null)
            {
                headers["Content-Range"] = upstream.Content.Headers.ContentRange.ToString();
            }

            Request.Response.StatusCode = (int)upstream.StatusCode;
            // Emby disposes the stream once it is written, which releases the RomM connection.
            var stream = await upstream.Content.ReadAsStreamAsync().ConfigureAwait(false);
            return ResultFactory.GetResult(Request, stream, "application/octet-stream", headers);
        }

        private object RommFailure(HttpStatusCode status)
        {
            Request.Response.StatusCode = status == HttpStatusCode.NotFound ? 404
                : status == HttpStatusCode.RequestedRangeNotSatisfiable ? 416
                : 502;
            return null!;
        }

        public async Task<object> Get(GetGameSaveRequest request)
        {
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return Json(401, new { error = "User not authenticated" });
            var rommSaves = Plugin.Instance?.RommSaves;
            var target = RommTarget(rommSaves, userId.Value, request.GameId, request.Kind);
            var data = target != null
                ? await rommSaves!.ReadAsync(target, Request.CancellationToken).ConfigureAwait(false)
                : GameSavesHelper.Get(userId.Value, request.GameId, request.Kind ?? "state");
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

            var rommSaves = Plugin.Instance?.RommSaves;
            var target = RommTarget(rommSaves, userId.Value, request.GameId, request.Kind);
            if (target != null)
            {
                await rommSaves!.WriteAsync(target, ms.ToArray(), Request.CancellationToken).ConfigureAwait(false);
            }
            else
            {
                GameSavesHelper.Save(userId.Value, request.GameId, request.Kind ?? "state", ms.ToArray());
            }

            Request.Response.StatusCode = 204;
            return null!;
        }

        private static RommSaveTarget? RommTarget(RommSaveSync? rommSaves, Guid userId, string gameId, string? kind) =>
            rommSaves?.Target(userId, gameId, kind, GameSavesHelper.ResolvePath(userId, gameId, kind ?? "state"));

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

        public object Get(GetEmulatorBridgeRequest request)
        {
            var stream = _assembly.GetManifestResourceStream("Emby.Plugins.Moonfin.EmulatorJS.moonfin-bridge.js");
            return stream == null ? NotFound() : ResultFactory.GetResult(Request, stream, "application/javascript", null);
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

    internal sealed class CoreOverrideBody
    {
        public string? Core { get; set; }
    }

    internal sealed class BackendOverrideBody
    {
        public string? Backend { get; set; }
    }
}
