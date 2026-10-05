using System.Reflection;
using MediaBrowser.Controller.Entities;

namespace Moonfin.Server.Helpers;

/// <summary>
/// The user manager, InternalItemsQuery.SetUser and BaseItem.IsVisible all take Jellyfin's
/// User type, which changed between 10.10 and 10.11, so services hold users as object and
/// go through these bindings.
/// </summary>
internal static class UserReflection
{
    private static readonly Type? UserManagerType = Type.GetType("MediaBrowser.Controller.Library.IUserManager, MediaBrowser.Controller");
    private static readonly MethodInfo? GetUserById = UserManagerType?.GetMethod("GetUserById", [typeof(Guid)]);
    private static readonly MethodInfo? SetUser = typeof(InternalItemsQuery).GetMethod("SetUser", BindingFlags.Public | BindingFlags.Instance);
    private static readonly PropertyInfo? UserProperty = typeof(InternalItemsQuery).GetProperty(nameof(InternalItemsQuery.User), BindingFlags.Public | BindingFlags.Instance);

    // IsVisible grew a skipAllowedTagsCheck parameter in 10.11, so any overload whose extra
    // parameters are booleans is taken and those are passed as false.
    private static readonly MethodInfo? IsVisible = typeof(BaseItem)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Where(m => m.Name == "IsVisible" && m.GetParameters().Length >= 1 && m.GetParameters().Skip(1).All(p => p.ParameterType == typeof(bool)))
        .OrderBy(m => m.GetParameters().Length)
        .FirstOrDefault();

    public static object? ResolveUser(IServiceProvider services, Guid userId)
    {
        if (userId == Guid.Empty || GetUserById == null)
        {
            return null;
        }

        var userManager = UserManager(services);
        return userManager == null ? null : GetUserById.Invoke(userManager, [userId]);
    }

    public static object? UserManager(IServiceProvider services) =>
        UserManagerType == null ? null : services.GetService(UserManagerType);

    /// <summary>Applies the user's parental limits and blocked tags to the query.</summary>
    public static bool ApplyUser(InternalItemsQuery query, object user)
    {
        if (SetUser != null)
        {
            try
            {
                SetUser.Invoke(query, [user]);
                return true;
            }
            catch
            {
            }
        }

        if (UserProperty?.CanWrite == true && UserProperty.PropertyType.IsInstanceOfType(user))
        {
            UserProperty.SetValue(query, user);
            return true;
        }

        return false;
    }

    /// <summary>Library access on top of the query's filters. Fails closed when it can't be checked.</summary>
    public static bool IsVisibleTo(BaseItem item, object user)
    {
        if (IsVisible == null || !IsVisible.GetParameters()[0].ParameterType.IsInstanceOfType(user))
        {
            return false;
        }

        var args = new object?[IsVisible.GetParameters().Length];
        args[0] = user;
        for (var i = 1; i < args.Length; i++)
        {
            args[i] = false;
        }

        try
        {
            return IsVisible.Invoke(item, args) is true;
        }
        catch
        {
            return false;
        }
    }
}
