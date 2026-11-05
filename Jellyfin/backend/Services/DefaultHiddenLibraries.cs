using System.Reflection;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Helpers;

namespace Moonfin.Server.Services;

/// <summary>
/// Hides the libraries the admin picked by adding them to each user's own My Media and Latest
/// exclude lists, the ones Library Visibility switches. Some are picked for the Latest list only,
/// so they stay in navigation. Nothing is ever removed, so libraries a user hid themselves stay
/// hidden and a user can still show these again.
/// </summary>
public class DefaultHiddenLibraries
{
    private static readonly Type? UserManagerType = Type.GetType("MediaBrowser.Controller.Library.IUserManager, MediaBrowser.Controller");

    private static readonly MethodInfo? GetUserDtoMethod = UserManagerType?
        .GetMethods()
        .Where(m => m.Name == "GetUserDto" && typeof(UserDto).IsAssignableFrom(m.ReturnType))
        .OrderBy(m => m.GetParameters().Length)
        .FirstOrDefault();

    private static readonly MethodInfo? UpdateConfigurationMethod = UserManagerType?
        .GetMethod("UpdateConfigurationAsync", [typeof(Guid), typeof(UserConfiguration)]);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DefaultHiddenLibraries> _logger;

    public DefaultHiddenLibraries(IServiceProvider serviceProvider, ILogger<DefaultHiddenLibraries> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task ApplyAsync(IEnumerable<Guid> userIds)
    {
        var configuration = MoonfinPlugin.Instance?.Configuration;
        var hidden = ParseIds(configuration?.DefaultHiddenLibraryIds);
        // A hidden library leaves the recently added rows too.
        var hiddenFromLatest = hidden.Union(ParseIds(configuration?.DefaultHiddenFromLatestLibraryIds)).ToList();
        var userManager = UserReflection.UserManager(_serviceProvider);
        if (hiddenFromLatest.Count == 0 || userManager == null || GetUserDtoMethod == null || UpdateConfigurationMethod == null)
        {
            return;
        }

        foreach (var userId in userIds)
        {
            try
            {
                var user = UserReflection.ResolveUser(_serviceProvider, userId);
                if (user == null)
                {
                    continue;
                }

                var args = new object?[GetUserDtoMethod.GetParameters().Length];
                args[0] = user;
                var config = (GetUserDtoMethod.Invoke(userManager, args) as UserDto)?.Configuration;
                if (config == null)
                {
                    continue;
                }

                var myMedia = Merge(config.MyMediaExcludes, hidden);
                var latest = Merge(config.LatestItemsExcludes, hiddenFromLatest);
                if (myMedia == null && latest == null)
                {
                    continue;
                }

                config.MyMediaExcludes = myMedia ?? config.MyMediaExcludes;
                config.LatestItemsExcludes = latest ?? config.LatestItemsExcludes;
                if (UpdateConfigurationMethod.Invoke(userManager, [userId, config]) is Task update)
                {
                    await update.ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not hide the default libraries for user {UserId}", userId);
            }
        }
    }

    private static List<Guid> ParseIds(IEnumerable<string>? ids) =>
        (ids ?? Enumerable.Empty<string>())
            .Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

    /// <summary>The list with the missing libraries added, or null when it already has them all.</summary>
    internal static Guid[]? Merge(Guid[]? current, IReadOnlyCollection<Guid> libraries)
    {
        var existing = current ?? Array.Empty<Guid>();
        var missing = libraries.Where(id => !existing.Contains(id)).ToArray();
        return missing.Length == 0 ? null : existing.Concat(missing).ToArray();
    }
}

/// <summary>Hides the default libraries for each account as it's created.</summary>
public class DefaultHiddenLibrariesOnUserCreated : IEventConsumer<UserCreatedEventArgs>
{
    private readonly DefaultHiddenLibraries _libraries;

    public DefaultHiddenLibrariesOnUserCreated(DefaultHiddenLibraries libraries)
    {
        _libraries = libraries;
    }

    public Task OnEvent(UserCreatedEventArgs eventArgs)
    {
        // The User the event carries changed type in 10.11, so its id is read by reflection.
        var user = eventArgs.GetType().GetProperty("Argument")?.GetValue(eventArgs);
        return user?.GetType().GetProperty("Id")?.GetValue(user) is Guid id
            ? _libraries.ApplyAsync([id])
            : Task.CompletedTask;
    }
}
