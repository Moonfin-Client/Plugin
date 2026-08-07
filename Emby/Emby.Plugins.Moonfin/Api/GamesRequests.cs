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
    }

    [Route("/Moonfin/Games/{LibraryId}/Rom/{Token}", "GET")]
    [Authenticated]
    public class GetGameRomRequest : IReturn<object>
    {
        public string LibraryId { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
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
    [Authenticated]
    public class HeadGameRomRequest : IReturn<object>
    {
        public string LibraryId { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
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

    [Route("/Moonfin/EmulatorJS/data/{Path*}", "GET")]
    [Unauthenticated]
    public class GetEmulatorDataRequest : IReturn<object>
    {
        public string? Path { get; set; }
    }
}
