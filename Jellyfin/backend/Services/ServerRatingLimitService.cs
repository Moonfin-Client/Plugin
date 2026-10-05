using System.Reflection;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging;
using Moonfin.Seasonal;
using Moonfin.Server.Helpers;

namespace Moonfin.Server.Services;

/// <summary>
/// A user's parental limit as the server holds it, for rows whose items never pass through
/// the server's own item query. The policy comes from the user DTO rather than the User
/// entity because the DTO kept its shape from 10.10 through 12 while the entity's rating
/// members were renamed, and the user manager is bound by reflection for the same reason.
/// GetParentalRatings changed its return type in 10.11, so it is bound by name too.
/// </summary>
public class ServerRatingLimitService
{
    private static readonly MethodInfo? GetUserDtoMethod = Type
        .GetType("MediaBrowser.Controller.Library.IUserManager, MediaBrowser.Controller")?
        .GetMethods()
        .Where(m => m.Name == "GetUserDto" && typeof(UserDto).IsAssignableFrom(m.ReturnType))
        .OrderBy(m => m.GetParameters().Length)
        .FirstOrDefault();

    private static readonly MethodInfo? GetParentalRatingsMethod =
        typeof(ILocalizationManager).GetMethod("GetParentalRatings", Type.EmptyTypes);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILocalizationManager _localization;
    private readonly IServerConfigurationManager _configManager;
    private readonly ILogger<ServerRatingLimitService> _logger;

    public ServerRatingLimitService(
        IServiceProvider serviceProvider,
        ILocalizationManager localization,
        IServerConfigurationManager configManager,
        ILogger<ServerRatingLimitService> logger)
    {
        _serviceProvider = serviceProvider;
        _localization = localization;
        _configManager = configManager;
        _logger = logger;
    }

    public RatingLimit ForUser(Guid userId)
    {
        var max = MaxParentalRating(userId);
        if (max == null)
        {
            return RatingLimit.None;
        }

        return new RatingLimit(max, RatingValues(), _configManager.Configuration.MetadataCountryCode);
    }

    private IEnumerable<KeyValuePair<string, int>> RatingValues()
    {
        var ratings = GetParentalRatingsMethod?.Invoke(_localization, null) as System.Collections.IEnumerable;
        if (ratings == null)
        {
            yield break;
        }

        foreach (var entry in ratings)
        {
            if (entry is ParentalRating rating && rating.Value.HasValue && !string.IsNullOrWhiteSpace(rating.Name))
            {
                yield return new KeyValuePair<string, int>(rating.Name, rating.Value.Value);
            }
        }
    }

    public List<CustomRowItem> Filter(RatingLimit limit, List<CustomRowItem> items)
    {
        return limit.HasLimit ? items.Where(i => limit.IsAllowed(i.OfficialRating)).ToList() : items;
    }

    // A policy the plugin can't read is treated as no limit. The server still applies it to
    // every library item it serves, so this only affects titles from outside lists.
    private int? MaxParentalRating(Guid userId)
    {
        if (GetUserDtoMethod == null)
        {
            return null;
        }

        try
        {
            var userManager = UserReflection.UserManager(_serviceProvider);
            var user = UserReflection.ResolveUser(_serviceProvider, userId);
            if (userManager == null || user == null)
            {
                return null;
            }

            var args = new object?[GetUserDtoMethod.GetParameters().Length];
            args[0] = user;
            return (GetUserDtoMethod.Invoke(userManager, args) as UserDto)?.Policy?.MaxParentalRating;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the parental policy for user {UserId}", userId);
            return null;
        }
    }
}
