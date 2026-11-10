using MediaBrowser.Common;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Moonfin.Bookmarks;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>Syncs each user's audiobook and book bookmarks and notes across their apps.</summary>
    public class BookmarksService : IService, IRequiresRequest, IHasResultFactory
    {
        private readonly IAuthorizationContext _authContext;
        private readonly ILogger _logger;

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        public BookmarksService(IApplicationHost appHost)
        {
            _authContext = appHost.Resolve<IAuthorizationContext>();
            _logger = appHost.Resolve<ILogManager>().GetLogger("MoonfinBookmarks");
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
        }

        private object Json(object? body) => MoonfinJson.Result(Request, ResultFactory, body);
        private object Json(int statusCode, object? body) { Request.Response.StatusCode = statusCode; return Json(body); }

        public Task<object> Get(GetAllBookmarksRequest request) =>
            Run("reading bookmarks", null, (store, userId) => store.GetUserBookmarksAsync(userId));

        public Task<object> Get(GetItemBookmarksRequest request) =>
            Run("reading bookmarks", request.ItemId, (store, userId) => store.GetItemUserDataAsync(userId, request.ItemId));

        public async Task<object> Post(SaveItemBookmarksRequest request)
        {
            var bookmarks = await MoonfinJson.ReadBodyAsync<List<BookmarkDto>>(request.RequestStream).ConfigureAwait(false);
            return await Run("saving bookmarks", request.ItemId, async (store, userId) =>
            {
                await store.SaveItemBookmarksAsync(userId, request.ItemId, bookmarks ?? new List<BookmarkDto>()).ConfigureAwait(false);
                return new { Success = true, request.ItemId };
            }).ConfigureAwait(false);
        }

        public async Task<object> Post(SaveItemNotesRequest request)
        {
            var notes = await MoonfinJson.ReadBodyAsync<List<NoteDto>>(request.RequestStream).ConfigureAwait(false);
            return await Run("saving notes", request.ItemId, async (store, userId) =>
            {
                await store.SaveItemNotesAsync(userId, request.ItemId, notes ?? new List<NoteDto>()).ConfigureAwait(false);
                return new { Success = true, request.ItemId };
            }).ConfigureAwait(false);
        }

        private async Task<object> Run<T>(string action, string? itemId, Func<UserBookmarkStore, Guid, Task<T>> work)
        {
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return Json(401, new { Error = "User not authenticated" });

            var store = Plugin.Instance?.Bookmarks;
            if (store == null) return Json(503, new { Error = "The plugin isn't ready yet." });

            try
            {
                return Json(await work(store, userId.Value).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                // Reporting success on a write that never landed, or an empty list for a file that
                // couldn't be read, lets the client drop the copy it was holding.
                _logger.ErrorException($"Error {action} for user {userId}, item {itemId ?? "(all)"}", ex);
                return Json(500, $"Error {action}.");
            }
        }
    }
}
