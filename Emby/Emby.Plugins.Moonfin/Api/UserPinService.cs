using System;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;
using Moonfin.Pin;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>
    /// Per-user PINs kept on the server, so a PIN set on one device holds on every other and a
    /// wrong guess is counted here. Nothing here ever returns a PIN or its hash.
    /// </summary>
    public class UserPinService : IService, IRequiresRequest, IHasResultFactory
    {
        private readonly IAuthorizationContext _authContext;

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        public UserPinService(IApplicationHost appHost)
        {
            _authContext = appHost.Resolve<IAuthorizationContext>();
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
        }

        private object Json(object? body) => MoonfinJson.Result(Request, ResultFactory, body);
        private object Json(int statusCode, object? body) { Request.Response.StatusCode = statusCode; return Json(body); }

        public object Get(GetPinRequest request)
        {
            var pins = Plugin.Instance?.Pins;
            if (pins == null) return NotReady();
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return NoUser();

            return Json(new { enabled = pins.HasPin(userId.Value) });
        }

        public async Task<object> Put(SetPinRequest request)
        {
            var pins = Plugin.Instance?.Pins;
            if (pins == null) return NotReady();
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return NoUser();

            var body = await MoonfinJson.ReadBodyAsync<PinBody>(request.RequestStream).ConfigureAwait(false);
            if (!PinService.IsPinShaped(body?.Pin)) return Json(400, new { error = "A PIN is exactly four digits" });

            var wrong = RefuseUnlessCurrent(pins, userId.Value, body?.CurrentPin);
            if (wrong != null) return wrong;

            pins.Set(userId.Value, body?.Pin);
            return Json(new { enabled = true });
        }

        public async Task<object> Post(VerifyPinRequest request)
        {
            var pins = Plugin.Instance?.Pins;
            if (pins == null) return NotReady();
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return NoUser();

            var body = await MoonfinJson.ReadBodyAsync<PinBody>(request.RequestStream).ConfigureAwait(false);
            var result = pins.Verify(userId.Value, body?.Pin);
            switch (result.Check)
            {
                case PinCheck.NoPin:
                    return Json(404, new { error = "No PIN is set" });
                case PinCheck.Locked:
                    return TooManyAttempts(result.RetryAfterSeconds);
                default:
                    return Json(new { valid = result.IsValid });
            }
        }

        public async Task<object> Delete(RemovePinRequest request)
        {
            var pins = Plugin.Instance?.Pins;
            if (pins == null) return NotReady();
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return NoUser();

            var body = await MoonfinJson.ReadBodyAsync<PinBody>(request.RequestStream).ConfigureAwait(false);
            var wrong = RefuseUnlessCurrent(pins, userId.Value, body?.CurrentPin ?? body?.Pin);
            if (wrong != null) return wrong;

            pins.Remove(userId.Value);
            return Json(new { enabled = false });
        }

        /// <summary>
        /// Every user and whether they have a PIN, administrators first, so the config page can
        /// reset a forgotten one.
        /// </summary>
        public object Get(GetAdminPinsRequest request)
        {
            var pins = Plugin.Instance?.Pins;
            if (pins == null) return NotReady();

            var users = PluginServices.UserManager?.GetUserList(new UserQuery()) ?? Array.Empty<User>();
            return Json(users
                .OrderByDescending(u => u.Policy?.IsAdministrator == true)
                .ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
                .Select(u => new
                {
                    id = u.Id.ToString("N"),
                    name = u.Name,
                    isAdministrator = u.Policy?.IsAdministrator == true,
                    hasPin = pins.HasPin(u.Id),
                })
                .ToList());
        }

        public async Task<object> Put(SetPinForRequest request)
        {
            var pins = Plugin.Instance?.Pins;
            if (pins == null) return NotReady();

            var userId = KnownUser(request.UserId);
            if (userId == null) return Json(404, new { error = "That user does not exist" });

            var body = await MoonfinJson.ReadBodyAsync<PinBody>(request.RequestStream).ConfigureAwait(false);
            if (!PinService.IsPinShaped(body?.Pin)) return Json(400, new { error = "A PIN is exactly four digits" });

            pins.Set(userId.Value, body?.Pin);
            return Json(new { enabled = true });
        }

        public object Delete(RemovePinForRequest request)
        {
            var pins = Plugin.Instance?.Pins;
            if (pins == null) return NotReady();

            var userId = KnownUser(request.UserId);
            if (userId == null) return Json(404, new { error = "That user does not exist" });

            pins.Remove(userId.Value);
            return Json(new { enabled = false });
        }

        private object? RefuseUnlessCurrent(PinService pins, Guid userId, string? current)
        {
            var result = pins.Verify(userId, current);
            switch (result.Check)
            {
                case PinCheck.Valid:
                case PinCheck.NoPin:
                    return null;
                case PinCheck.Locked:
                    return TooManyAttempts(result.RetryAfterSeconds);
                default:
                    return Json(403, new { error = "The current PIN is wrong" });
            }
        }

        private object TooManyAttempts(int retryAfterSeconds)
        {
            Request.Response.AddHeader("Retry-After", retryAfterSeconds.ToString());
            return Json(429, new { error = "Too many attempts", retryAfterSeconds });
        }

        private object NotReady() => Json(503, new { error = "The plugin is not ready" });

        private object NoUser() => Json(401, new { error = "A valid user token is required" });

        private static Guid? KnownUser(string? id) =>
            Guid.TryParse(id, out var userId) && PluginServices.UserManager?.GetUserById(userId) != null
                ? userId
                : (Guid?)null;
    }
}
