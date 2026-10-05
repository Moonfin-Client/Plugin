using System.Reflection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Maps a BaseItem to the trimmed BaseItemDto shape the browse endpoints hand to clients, using
/// only BaseItem members that are the same across Jellyfin versions.
/// </summary>
public class ItemCardDtoMapper
{
    // Takes the User the endpoints only ever hold as an object, so it's bound the same
    // reflective way the user manager calls are.
    private static readonly MethodInfo? _getUserDataDto = typeof(IUserDataManager)
        .GetMethods()
        .FirstOrDefault(m =>
        {
            if (m.Name != "GetUserDataDto")
            {
                return false;
            }

            var parameters = m.GetParameters();
            return parameters.Length == 2 &&
                parameters[0].ParameterType == typeof(BaseItem) &&
                typeof(UserItemDataDto).IsAssignableFrom(m.ReturnType);
        });

    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<ItemCardDtoMapper> _logger;

    public ItemCardDtoMapper(IUserDataManager userDataManager, ILogger<ItemCardDtoMapper> logger)
    {
        _userDataManager = userDataManager;
        _logger = logger;
    }

    public object Map(BaseItem item, object? user)
    {
        var imageTags = new Dictionary<string, string>();
        var imageInfo = item.GetImageInfo(ImageType.Primary, 0);
        if (imageInfo != null)
        {
            imageTags["Primary"] = GetTag(imageInfo);
        }

        var logoInfo = item.GetImageInfo(ImageType.Logo, 0);
        if (logoInfo != null)
        {
            imageTags["Logo"] = GetTag(logoInfo);
        }

        var backdropTags = item.GetImages(ImageType.Backdrop).Select(GetTag).ToList();

        return new
        {
            item.Id,
            item.Name,
            Type = item.GetBaseItemKind().ToString(),
            item.ProductionYear,
            item.OfficialRating,
            item.RunTimeTicks,
            item.Genres,
            item.Overview,
            item.CommunityRating,
            item.CriticRating,
            ImageTags = imageTags,
            BackdropImageTags = backdropTags,
            UserData = BuildUserData(item, user)
        };
    }

    /// <summary>
    /// The calling user's played state, built by the server so it matches what a stock item
    /// response carries. Cards read Played for the checkmark, PlaybackPositionTicks for the resume
    /// bar and UnplayedItemCount for the series badge, so leaving it off makes everything these
    /// endpoints return look unwatched.
    /// </summary>
    private UserItemDataDto? BuildUserData(BaseItem item, object? user)
    {
        if (user == null || _getUserDataDto == null)
        {
            return null;
        }

        try
        {
            return _getUserDataDto.Invoke(_userDataManager, [item, user]) as UserItemDataDto;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read user data for {ItemId}", item.Id);
            return null;
        }
    }

    /// <summary>A stable tag string from an ItemImageInfo for cache-busting image URLs.</summary>
    private static string GetTag(ItemImageInfo info) => info.DateModified.Ticks.ToString("X");
}
