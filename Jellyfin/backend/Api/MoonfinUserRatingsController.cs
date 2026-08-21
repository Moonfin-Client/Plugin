using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Api;

/// <summary>
/// Serves the calling user's personal ratings in one response, so clients can
/// sort and filter a library by them. The server itself has no user rating
/// sort, and its Likes and Dislikes item filters are defined against the
/// rating threshold, which makes their union exactly the set of rated items.
/// </summary>
[ApiController]
[Route("Moonfin/UserRatings")]
[Produces(MediaTypeNames.Application.Json)]
[Authorize]
public sealed class MoonfinUserRatingsController : ControllerBase
{
    // The concrete User type has moved namespaces across server versions, so
    // it is resolved reflectively rather than referenced directly.
    private static readonly Type? _userManagerType =
        Type.GetType("MediaBrowser.Controller.Library.IUserManager, MediaBrowser.Controller");

    private static readonly MethodInfo? _userManagerGetUserById =
        _userManagerType?.GetMethod("GetUserById", [typeof(Guid)]);

    private static readonly MethodInfo? _internalItemsQuerySetUser =
        typeof(InternalItemsQuery).GetMethod("SetUser", BindingFlags.Public | BindingFlags.Instance);

    private static readonly PropertyInfo? _internalItemsQueryUserProperty =
        typeof(InternalItemsQuery).GetProperty(nameof(InternalItemsQuery.User), BindingFlags.Public | BindingFlags.Instance);

    private static readonly MethodInfo? _getUserData = typeof(IUserDataManager)
        .GetMethods()
        .FirstOrDefault(m =>
        {
            if (m.Name != "GetUserData")
            {
                return false;
            }

            var parameters = m.GetParameters();
            return parameters.Length == 2 &&
                parameters[1].ParameterType == typeof(BaseItem) &&
                typeof(UserItemData).IsAssignableFrom(m.ReturnType);
        });

    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<MoonfinUserRatingsController> _logger;

    public MoonfinUserRatingsController(
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        ILogger<MoonfinUserRatingsController> logger)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _logger = logger;
    }

    /// <summary>
    /// Every item the calling user has rated, with the score and the thumb it
    /// reads as. Ids only, so the response stays small enough to fetch per
    /// browse and the client pages the items themselves through the Items API.
    /// </summary>
    [HttpGet("Mine")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetMyRatings()
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { Error = "User not authenticated" });
        }

        var queryUser = ResolveQueryUser(userId.Value);
        if (queryUser == null || _getUserData == null)
        {
            _logger.LogWarning("Could not resolve the user or user data reader on this server version");
            return Ok(new { Items = Array.Empty<object>() });
        }

        var seen = new HashSet<Guid>();
        var items = new List<object>();

        // Liked is rating at or above the threshold and disliked is below it,
        // and a SQL comparison is never true for a null rating, so the two
        // passes cover the rated items and nothing else.
        foreach (var liked in new[] { true, false })
        {
            var query = new InternalItemsQuery
            {
                Recursive = true,
                IsLiked = liked
            };

            if (!TryApplyQueryUser(query, queryUser))
            {
                return Ok(new { Items = Array.Empty<object>() });
            }

            foreach (var item in _libraryManager.GetItemList(query))
            {
                if (!seen.Add(item.Id))
                {
                    continue;
                }

                UserItemData? userData;
                try
                {
                    userData = _getUserData.Invoke(_userDataManager, [queryUser, item]) as UserItemData;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not read user data for {ItemId}", item.Id);
                    continue;
                }

                if (userData?.Rating == null)
                {
                    continue;
                }

                items.Add(new
                {
                    ItemId = item.Id.ToString("N"),
                    Rating = userData.Rating,
                    Likes = userData.Likes
                });
            }
        }

        return Ok(new { Items = items });
    }

    private object? ResolveQueryUser(Guid userId)
    {
        if (_userManagerType == null || _userManagerGetUserById == null)
        {
            return null;
        }

        var userManager = HttpContext?.RequestServices.GetService(_userManagerType);
        if (userManager == null)
        {
            return null;
        }

        return _userManagerGetUserById.Invoke(userManager, [userId]);
    }

    private static bool TryApplyQueryUser(InternalItemsQuery query, object queryUser)
    {
        if (_internalItemsQuerySetUser != null)
        {
            try
            {
                _internalItemsQuerySetUser.Invoke(query, [queryUser]);
                return true;
            }
            catch
            {
            }
        }

        if (_internalItemsQueryUserProperty?.CanWrite != true ||
            !_internalItemsQueryUserProperty.PropertyType.IsInstanceOfType(queryUser))
        {
            return false;
        }

        try
        {
            _internalItemsQueryUserProperty.SetValue(query, queryUser);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
