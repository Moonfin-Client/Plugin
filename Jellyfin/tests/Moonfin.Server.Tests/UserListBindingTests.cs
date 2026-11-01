using System.Collections;
using Moonfin.Server.Helpers;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// The user manager listed its users through a Users property up to Jellyfin 10.11 and through a
/// GetUsers() method from 12.2, which dropped the property. Binding only the property left the
/// directory empty on 12.2, so the admin PIN list was blank and resetting a PIN said the user did
/// not exist.
/// </summary>
public class UserListBindingTests
{
    private sealed class PropertyManager
    {
        public IEnumerable<string> Users { get; } = ["a", "b"];
    }

    private sealed class MethodManager
    {
        public IEnumerable<string> GetUsers() => ["c", "d", "e"];
    }

    private sealed class BothManager
    {
        public IEnumerable<string> Users { get; } = ["from property"];

        public IEnumerable<string> GetUsers() => ["from method"];
    }

    private sealed class NeitherManager
    {
        public IEnumerable<Guid> GetUsersIds() => [Guid.NewGuid()];
    }

    private static List<object> List(Func<object, IEnumerable?> binding, object manager) =>
        binding(manager)!.Cast<object>().ToList();

    [Fact]
    public void A_10_10_or_10_11_manager_is_listed_through_its_Users_property()
    {
        var binding = UserReflection.UserListBinding(typeof(PropertyManager));

        Assert.NotNull(binding);
        Assert.Equal(["a", "b"], List(binding, new PropertyManager()));
    }

    [Fact]
    public void A_12_2_manager_is_listed_through_GetUsers()
    {
        var binding = UserReflection.UserListBinding(typeof(MethodManager));

        Assert.NotNull(binding);
        Assert.Equal(["c", "d", "e"], List(binding, new MethodManager()));
    }

    [Fact]
    public void GetUsers_is_preferred_when_a_manager_has_both()
    {
        var binding = UserReflection.UserListBinding(typeof(BothManager));

        Assert.NotNull(binding);
        Assert.Equal(["from method"], List(binding, new BothManager()));
    }

    [Fact]
    public void A_manager_with_neither_has_no_binding()
    {
        Assert.Null(UserReflection.UserListBinding(typeof(NeitherManager)));
        Assert.Null(UserReflection.UserListBinding(null));
    }

    [Fact]
    public void The_running_server_build_has_a_binding()
    {
        // The test project builds against Jellyfin 10.10, whose IUserManager has the property
        var managerType = Type.GetType("MediaBrowser.Controller.Library.IUserManager, MediaBrowser.Controller");

        Assert.NotNull(UserReflection.UserListBinding(managerType));
    }
}
