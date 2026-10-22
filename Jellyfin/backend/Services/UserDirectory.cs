using System.Collections;
using System.Reflection;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Helpers;

namespace Moonfin.Server.Services;

/// <summary>A user as the server lists them, with only what Moonfin needs.</summary>
public sealed record DirectoryUser(Guid Id, string Name, string? ImageTag, bool IsAdministrator);

/// <summary>The users on the server.</summary>
public interface IUserDirectory
{
    IReadOnlyList<DirectoryUser> Users();
}

/// <summary>
/// Lists users from the user manager, which is bound by reflection because the User entity it
/// hands out changed between 10.10 and 10.11. The DTO kept its shape, so everything read here
/// comes from that.
/// </summary>
public class UserDirectory : IUserDirectory
{
    private static readonly PropertyInfo? UsersProperty = Type
        .GetType("MediaBrowser.Controller.Library.IUserManager, MediaBrowser.Controller")?
        .GetProperty("Users");

    private static readonly MethodInfo? GetUserDtoMethod = Type
        .GetType("MediaBrowser.Controller.Library.IUserManager, MediaBrowser.Controller")?
        .GetMethods()
        .Where(m => m.Name == "GetUserDto" && typeof(UserDto).IsAssignableFrom(m.ReturnType))
        .OrderBy(m => m.GetParameters().Length)
        .FirstOrDefault();

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<UserDirectory> _logger;

    public UserDirectory(IServiceProvider serviceProvider, ILogger<UserDirectory> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public IReadOnlyList<DirectoryUser> Users()
    {
        var result = new List<DirectoryUser>();
        if (UsersProperty == null || GetUserDtoMethod == null)
        {
            return result;
        }

        try
        {
            var userManager = UserReflection.UserManager(_serviceProvider);
            if (userManager == null || UsersProperty.GetValue(userManager) is not IEnumerable users)
            {
                return result;
            }

            foreach (var user in users)
            {
                var args = new object?[GetUserDtoMethod.GetParameters().Length];
                args[0] = user;
                if (GetUserDtoMethod.Invoke(userManager, args) is UserDto dto)
                {
                    result.Add(new DirectoryUser(dto.Id, dto.Name ?? string.Empty, dto.PrimaryImageTag, dto.Policy?.IsAdministrator == true));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list the users on the server");
            result.Clear();
        }

        return result;
    }
}
