using System.Reflection;
using Jellyfin.Data.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Tasks;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

internal sealed class RecordingHostTestContext : IDisposable
{
    public Dictionary<Guid, BaseItem> Items { get; } = new();

    public User User { get; } = new("viewer", "auth", "reset");

    public ILiveTvManager LiveTv { get; } = RecordingHostProxy.Create<ILiveTvManager>();

    public JellyfinLiveTvRecordingHost Host { get; }

    public RecordingHostTestContext()
    {
        var library = RecordingHostProxy.Create<ILibraryManager>((method, args) =>
            method.Name == "GetItemById" ? Items.GetValueOrDefault((Guid)args![0]!) : throw new NotSupportedException(method.Name));
        var users = RecordingHostProxy.Create<IUserManager>((method, _) =>
            method.Name == "GetUserById" ? User : throw new NotSupportedException(method.Name));
        var services = RecordingHostProxy.Create<IServiceProvider>((_, args) =>
            (Type)args![0]! == typeof(IUserManager) ? users : null);
        Host = new JellyfinLiveTvRecordingHost(
            LiveTv, RecordingHostProxy.Create<IRecordingsManager>(), library,
            RecordingHostProxy.Create<ITaskManager>(), services, new NoOpLogger<JellyfinLiveTvRecordingHost>());
    }

    public void Dispose() => Host.Dispose();
}

public class RecordingHostProxy : DispatchProxy
{
    private Func<MethodInfo, object?[]?, object?>? _handler;

    public static T Create<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var proxy = Create<T, RecordingHostProxy>();
        ((RecordingHostProxy)(object)proxy)._handler = handler;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name.StartsWith("add_", StringComparison.Ordinal) || method.Name.StartsWith("remove_", StringComparison.Ordinal))
        {
            return null;
        }

        return _handler != null ? _handler(method, args) : throw new NotSupportedException(method.Name);
    }
}

public sealed class LiveTvRecordingHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moonfin-recording-host-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LibraryVisibilityIsRequiredEvenWhenParentalRulesAllowTheItem(bool allowed)
    {
        using var context = new RecordingHostTestContext();
        var item = new AccessCheckedVideo { Id = Guid.NewGuid(), Allowed = allowed };
        context.Items.Add(item.Id, item);

        Assert.Equal(allowed, context.Host.IsVisibleTo(item.Id, context.User.Id));
    }

    [Fact]
    public void AGuideProgramAlsoRequiresAccessToItsChannel()
    {
        using var context = new RecordingHostTestContext();
        var channel = new AccessCheckedChannel { Id = Guid.NewGuid() };
        var program = new AccessCheckedProgram { Id = Guid.NewGuid(), ChannelId = channel.Id };
        context.Items.Add(channel.Id, channel);
        context.Items.Add(program.Id, program);

        Assert.False(context.Host.IsVisibleTo(program.Id, context.User.Id));
    }

    [Fact]
    public void TwoPlayableSiblingsRemainAmbiguous()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "show.mkv"), "data");
        File.WriteAllText(Path.Combine(_dir, "show.mp4"), "data");
        using var context = new RecordingHostTestContext();

        Assert.Equal(2, context.Host.FindRenamedSiblings(Path.Combine(_dir, "show.ts")).Count);
    }

    private sealed class AccessCheckedVideo : Video
    {
        public bool Allowed { get; init; }

        public override bool IsVisible(User user) => true;

        public override bool IsVisibleStandalone(User user) => Allowed;
    }

    private sealed class AccessCheckedChannel : LiveTvChannel
    {
        public override bool IsVisibleStandalone(User user) => false;
    }

    private sealed class AccessCheckedProgram : LiveTvProgram
    {
        public override bool IsVisibleStandalone(User user) => true;
    }
}
