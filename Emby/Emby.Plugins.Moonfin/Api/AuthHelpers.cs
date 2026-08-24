using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    internal static class AuthHelpers
    {
        /// <summary>Returns the authenticated user's GUID, or null if not authenticated.</summary>
        public static Guid? GetCurrentUserId(IRequest request, IAuthorizationContext authContext)
        {
            var user = GetCurrentUser(request, authContext);
            return (user != null && user.Id != Guid.Empty) ? user.Id : (Guid?)null;
        }

        /// <summary>Returns the authenticated Emby user, or null if not authenticated.</summary>
        public static User? GetCurrentUser(IRequest request, IAuthorizationContext authContext)
        {
            try { return authContext.GetAuthorizationInfo(request)?.User; }
            catch { return null; }
        }

        /// <summary>True when the caller is a server admin, the same check the "Admin" role uses.</summary>
        public static bool IsCurrentUserAdmin(IRequest request, IAuthorizationContext authContext)
        {
            try { return GetCurrentUser(request, authContext)?.Policy?.IsAdministrator == true; }
            catch { return false; }
        }

        /// <summary>Returns all server user GUIDs, or null if the user manager is unavailable.</summary>
        public static IReadOnlyCollection<Guid>? GetAllServerUserIds()
        {
            var um = PluginServices.UserManager;
            if (um == null) return null;
            try
            {
                var ids = new List<Guid>();
                foreach (var u in um.GetUserList(new UserQuery()))
                    if (u != null && u.Id != Guid.Empty) ids.Add(u.Id);
                return ids;
            }
            catch { return null; }
        }
    }

    /// <summary>Request URL helpers.</summary>
    internal static class Http
    {
        /// <summary>Derives the scheme://host base URL from the request's absolute URI.</summary>
        public static string BaseUrl(IRequest request)
        {
            try
            {
                if (!string.IsNullOrEmpty(request.AbsoluteUri))
                {
                    var uri = new Uri(request.AbsoluteUri);
                    return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
                }
            }
            catch { }
            return string.Empty;
        }

        /// <summary>Extracts the raw query string (without leading '?') from the request URI.</summary>
        public static string QueryString(IRequest request)
        {
            var uri = request.AbsoluteUri ?? string.Empty;
            var idx = uri.IndexOf('?');
            return idx >= 0 ? uri.Substring(idx + 1) : string.Empty;
        }
    }
}
