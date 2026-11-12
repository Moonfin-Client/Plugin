using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    [Route("/Moonfin/Games/Libraries", "GET")]
    [Authenticated]
    public class GetGameLibrariesRequest : IReturn<object> { }

    [Route("/Moonfin/Games/{LibraryId}/Systems", "GET")]
    [Authenticated]
    public class GetGameSystemsRequest : IReturn<object>
    {
        public string LibraryId { get; set; } = string.Empty;
    }

    [Route("/Moonfin/Games/{LibraryId}/Games", "GET")]
    [Authenticated]
    public class GetGamesRequest : IReturn<object>
    {
        public string LibraryId { get; set; } = string.Empty;
        public string? System { get; set; }
    }

    [Route("/Moonfin/Games/{LibraryId}/Recent", "GET")]
    [Authenticated]
    public class GetRecentGamesRequest : IReturn<object>
    {
        public string LibraryId { get; set; } = string.Empty;
        public string? Sort { get; set; }
        public int? Limit { get; set; }
    }

    [Route("/Moonfin/Games/{LibraryId}/Games/{GameId}", "GET")]
    [Authenticated]
    public class GetGameDetailRequest : IReturn<object>
    {
        public string LibraryId { get; set; } = string.Empty;
        public string GameId { get; set; } = string.Empty;
    }

    [Route("/Moonfin/Games/{LibraryId}/Thumb/{GameId}", "GET")]
    [Authenticated]
    public class GetGameThumbRequest : IReturn<object>
    {
        public string LibraryId { get; set; } = string.Empty;
        public string GameId { get; set; } = string.Empty;

        /// <summary>boxart (default), snap or title.</summary>
        public string? Type { get; set; }

        /// <summary>1 or true serves the original instead of the derived thumbnail.</summary>
        public string? Full { get; set; }
    }

    [Route("/Moonfin/Games/{LibraryId}/Rom/{Token}", "GET")]
    [Route("/Moonfin/Games/{LibraryId}/Rom/{Token}/{FileName}", "GET")]
    [Authenticated]
    public class GetGameRomRequest : IReturn<object>
    {
        public string LibraryId { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;

        /// <summary>The ROM's own file name, for emulators that need it in the URL. Never used to find the file.</summary>
        public string? FileName { get; set; }
    }

    [Route("/Moonfin/Games/{LibraryId}/Bios/{Token}", "GET")]
    [Authenticated]
    public class GetGameBiosRequest : IReturn<object>
    {
        public string LibraryId { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
    }

    // EmulatorJS sizes a ROM or BIOS before reusing its cached copy, and treats a refused HEAD
    // as a fatal error rather than a cache miss.
    [Route("/Moonfin/Games/{LibraryId}/Rom/{Token}", "HEAD")]
    [Route("/Moonfin/Games/{LibraryId}/Rom/{Token}/{FileName}", "HEAD")]
    [Authenticated]
    public class HeadGameRomRequest : IReturn<object>
    {
        public string LibraryId { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public string? FileName { get; set; }
    }

    [Route("/Moonfin/Games/{LibraryId}/Bios/{Token}", "HEAD")]
    [Authenticated]
    public class HeadGameBiosRequest : IReturn<object>
    {
        public string LibraryId { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
    }

    [Route("/Moonfin/Games/Saves/{GameId}", "GET")]
    [Authenticated]
    public class GetGameSaveRequest : IReturn<object>
    {
        public string GameId { get; set; } = string.Empty;
        public string? Kind { get; set; }
    }

    [Route("/Moonfin/Games/Saves/{GameId}", "PUT")]
    [Authenticated]
    public class PutGameSaveRequest : IReturn<object>, IRequiresRequestStream
    {
        public string GameId { get; set; } = string.Empty;
        public string? Kind { get; set; }
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Games/Romm/Account", "GET")]
    [Authenticated]
    public class GetRommAccountRequest : IReturn<object>
    {
    }

    [Route("/Moonfin/Games/Romm/Account", "POST")]
    [Authenticated]
    public class LinkRommAccountRequest : IReturn<object>
    {
    }

    [Route("/Moonfin/Games/Romm/Account", "DELETE")]
    [Authenticated]
    public class UnlinkRommAccountRequest : IReturn<object>
    {
    }

    [Route("/Moonfin/Games/Romm/Test", "POST")]
    [Authenticated(Roles = "Admin")]
    public class TestRommRequest : IReturn<object>
    {
        public string? Url { get; set; }
        public string? Token { get; set; }
    }

    [Route("/Moonfin/Games/Debug", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetGamesDebugRequest : IReturn<object> { }

    [Route("/Moonfin/Games/Cores/Status", "GET")]
    [Authenticated]
    public class GetCoresStatusRequest : IReturn<object> { }

    [Route("/Moonfin/Games/Cores/Install", "POST")]
    [Authenticated(Roles = "Admin")]
    public class InstallCoresRequest : IReturn<object> { }

    [Route("/Moonfin/Games/Cores/Upload", "POST")]
    [Authenticated(Roles = "Admin")]
    public class UploadCoresRequest : IReturn<object>, IRequiresRequestStream
    {
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/EmulatorJS/player.html", "GET")]
    [Unauthenticated]
    public class GetEmulatorPlayerRequest : IReturn<object> { }

    [Route("/Moonfin/EmulatorJS/moonfin-bridge.js", "GET")]
    [Unauthenticated]
    public class GetEmulatorBridgeRequest : IReturn<object> { }

    [Route("/Moonfin/Games/ArtworkCapabilities", "GET")]
    [Authenticated]
    public class GetArtworkCapabilitiesRequest : IReturn<object> { }

    [Route("/Moonfin/Games/{LibraryId}/ArtworkManifest", "GET")]
    [Authenticated]
    public class GetArtworkManifestRequest : IReturn<object>
    {
        public string LibraryId { get; set; } = string.Empty;
        public string? System { get; set; }

        /// <summary>The generation the client already has. A match answers 304.</summary>
        public string? Generation { get; set; }
    }

    [Route("/Moonfin/Games/{LibraryId}/ArtworkPriority", "POST")]
    [Authenticated]
    public class PostArtworkPriorityRequest : IReturn<object>, IRequiresRequestStream
    {
        public string LibraryId { get; set; } = string.Empty;
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Games/{LibraryId}/Artwork/{GameId}/{Role}/{Revision}", "GET")]
    [Authenticated]
    public class GetGameArtworkRequest : IReturn<object>
    {
        public string LibraryId { get; set; } = string.Empty;
        public string GameId { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string Revision { get; set; } = string.Empty;
    }

    [Route("/Moonfin/Games/{LibraryId}/Artwork/{GameId}/Refresh", "POST")]
    [Authenticated(Roles = "Admin")]
    public class RefreshGameArtworkRequest : IReturn<object>
    {
        public string LibraryId { get; set; } = string.Empty;
        public string GameId { get; set; } = string.Empty;

        /// <summary>boxart, snap or title. Every role when left out.</summary>
        public string? Type { get; set; }
    }

    [Route("/Moonfin/Games/{LibraryId}/Artwork/Refresh", "POST")]
    [Authenticated(Roles = "Admin")]
    public class RefreshLibraryArtworkRequest : IReturn<object>
    {
        public string LibraryId { get; set; } = string.Empty;
        public string? System { get; set; }
    }

    [Route("/Moonfin/Games/{LibraryId}/Games/{GameId}/Core", "PUT")]
    [Authenticated]
    public class PutGameCoreRequest : IReturn<object>, IRequiresRequestStream
    {
        public string LibraryId { get; set; } = string.Empty;
        public string GameId { get; set; } = string.Empty;
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Games/{LibraryId}/Games/{GameId}/Backend", "PUT")]
    [Authenticated]
    public class PutGameBackendRequest : IReturn<object>, IRequiresRequestStream
    {
        public string LibraryId { get; set; } = string.Empty;
        public string GameId { get; set; } = string.Empty;
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Games/ArcadeCompatibility/Status", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetArcadeCompatibilityStatusRequest : IReturn<object> { }

    [Route("/Moonfin/Games/ArcadeCompatibility/{Core}/Dat", "PUT")]
    [Authenticated(Roles = "Admin")]
    public class PutArcadeCompatibilityDatRequest : IReturn<object>, IRequiresRequestStream
    {
        /// <summary>arcade for FBNeo or mame for MAME.</summary>
        public string Core { get; set; } = string.Empty;
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/EmulatorJS/data/{Path*}", "GET")]
    [Unauthenticated]
    public class GetEmulatorDataRequest : IReturn<object>
    {
        public string? Path { get; set; }
    }
}
