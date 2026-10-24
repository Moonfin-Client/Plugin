using System.Reflection;
using MediaBrowser.Model.Querying;
using Jellyfin.Data.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Tasks;
using Moonfin.LiveTv;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

internal sealed class RecordingHostTestContext : IDisposable
{
    public Dictionary<Guid, BaseItem> Items { get; } = new();

    public InternalItemsQuery? LastQuery { get; private set; }

    public User User { get; } = new("viewer", "auth", "reset");

    public ILiveTvManager LiveTv { get; }

    public JellyfinLiveTvRecordingHost Host { get; }

    public RecordingHostTestContext(Func<MethodInfo, object?[]?, object?>? liveTvHandler = null)
    {
        LiveTv = RecordingHostProxy.Create<ILiveTvManager>(liveTvHandler);
        var library = RecordingHostProxy.Create<ILibraryManager>((method, args) =>
        {
            if (method.Name == "GetItemById") return Items.GetValueOrDefault((Guid)args![0]!);
            if (method.Name == "GetItemsResult")
            {
                LastQuery = (InternalItemsQuery)args![0]!;
                return new QueryResult<BaseItem> { Items = Items.Values.ToArray() };
            }
            throw new NotSupportedException(method.Name);
        });
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledTimersRetainTheServicesManualFlag(bool isManual)
    {
        var service = RecordingHostProxy.Create<ILiveTvService>((method, _) => method.Name switch
        {
            "get_Name" => "Dvr",
            "GetTimersAsync" => Task.FromResult<IEnumerable<TimerInfo>>(new[]
            {
                new TimerInfo { Id = "raw-id", IsManual = isManual },
            }),
            _ => throw new NotSupportedException(method.Name),
        });
        using var context = new RecordingHostTestContext((method, _) => method.Name switch
        {
            "get_Services" => new[] { service },
            "GetTimers" => Task.FromResult(new QueryResult<MediaBrowser.Model.LiveTv.TimerInfoDto>
            {
                Items = new[]
                {
                    new MediaBrowser.Model.LiveTv.TimerInfoDto
                    {
                        Id = "api-id", ExternalId = "raw-id", ServiceName = "Dvr",
                        Status = MediaBrowser.Model.LiveTv.RecordingStatus.Cancelled,
                    },
                },
            }),
            _ => throw new NotSupportedException(method.Name),
        });
        Assert.Equal(isManual, (await context.Host.GetTimersAsync(CancellationToken.None)).Single().IsManual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AServiceThatFailsToListItsTimersLeavesTheManualFlagUnknown(bool timesOut)
    {
        Exception failure = timesOut ? new TaskCanceledException("timeout") : new InvalidOperationException("offline");
        var service = RecordingHostProxy.Create<ILiveTvService>((method, _) => method.Name switch
        {
            "get_Name" => "Dvr",
            "GetTimersAsync" => Task.FromException<IEnumerable<TimerInfo>>(failure),
            _ => throw new NotSupportedException(method.Name),
        });
        using var context = new RecordingHostTestContext((method, _) => method.Name switch
        {
            "get_Services" => new[] { service },
            "GetTimers" => Task.FromResult(new QueryResult<MediaBrowser.Model.LiveTv.TimerInfoDto>
            {
                Items = new[]
                {
                    new MediaBrowser.Model.LiveTv.TimerInfoDto
                    {
                        Id = "api-id", ExternalId = "raw-id", ServiceName = "Dvr",
                        Status = MediaBrowser.Model.LiveTv.RecordingStatus.Cancelled,
                    },
                },
            }),
            _ => throw new NotSupportedException(method.Name),
        });
        Assert.Null((await context.Host.GetTimersAsync(CancellationToken.None)).Single().IsManual);
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moonfin-recording-host-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Fact]
    public void MovieGuideQueriesAndProjectionRetainOnlyContentIdentity()
    {
        using var context = new RecordingHostTestContext();
        var movie = new LiveTvProgram
        {
            Id = Guid.NewGuid(),
            Name = "The Movie",
            Overview = "The plot.",
            IsMovie = true,
            ProductionYear = 1990,
            StartDate = DateTime.UtcNow.AddDays(1),
        };
        var channel = new LiveTvChannel { Id = Guid.NewGuid(), Name = "WXIXDT3", Number = "19.3" };
        context.Items.Add(channel.Id, channel);
        movie.ChannelId = channel.Id;
        movie.ProviderIds["Imdb"] = "tt123";
        movie.ProviderIds["ProgramEtag"] = "airing-specific-value";
        context.Items.Add(movie.Id, movie);
        var projected = context.Host.GetProgram(movie.Id.ToString("N"))!;
        Assert.True(projected.Key!.IsMovie);
        Assert.Equal(movie.Overview, projected.Overview);
        Assert.Equal(1990, projected.ProductionYear);
        Assert.Single(projected.ContentIds);
        var showing = SuggestedShowing.FromProgram(projected);
        Assert.Equal("19.3", showing.ChannelNumber);
        Assert.Equal("WXIXDT3", showing.ChannelName);
        Assert.Equal("tt123", projected.ContentIds["Imdb"]);
        Assert.Single(context.Host.FindAirings(projected.Key, DateTime.UtcNow));
        Assert.True(context.LastQuery!.IsMovie);
        Assert.Null(context.LastQuery.IndexNumber);
        Assert.Null(context.LastQuery.ParentIndexNumber);
        Assert.Equal(movie.Name, context.LastQuery.Name);
        context.Host.FindProgramsByName(movie.Name, DateTime.UtcNow);
        Assert.Null(context.LastQuery!.IsMovie);
        Assert.Null(context.LastQuery.IndexNumber);
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

    [Fact]
    public void MetadataAndSubtitlesBesideARemuxAreNotSiblings()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "show.mkv"), "video");
        File.WriteAllText(Path.Combine(_dir, "show.nfo"), "metadata");
        File.WriteAllText(Path.Combine(_dir, "show.srt"), "subtitles");
        using var context = new RecordingHostTestContext();

        Assert.Equal(new[] { Path.Combine(_dir, "show.mkv") }, context.Host.FindRenamedSiblings(Path.Combine(_dir, "show.ts")));
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
