using System.Text;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moonfin.LiveTv;

namespace Moonfin.Server.Services;

public sealed class LiveTvChannelGroupsService(
    ILiveTvManager liveTv, ITunerHostManager tuners, IServerConfigurationManager config,
    ILibraryManager library, IUserManager users, ITaskManager tasks, ILogger<LiveTvChannelGroupsService> logger,
    IHostApplicationLifetime lifetime, IDtoService dtos)
{
    private readonly ChannelGroupReader _m3uReader = new();
    private readonly ChannelGroupReader _xtreamReader = new();
    private static readonly TimeSpan XtreamGuideWait = TimeSpan.FromSeconds(3);

    internal async Task<ChannelGroupResult> GetAsync(Guid userId, CancellationToken token)
    {
        var importTime = ChannelGroupResult.StableImportTime(tasks);
        var query = new LiveTvChannelQuery { UserId = userId, AddCurrentProgram = false };
        var options = new DtoOptions(false) { EnableImages = true, EnableUserData = true, AddCurrentProgram = false };
        var visible = liveTv.GetInternalChannels(query, options, token).Items;
        // Jellyfin moved the User type between 10.10 and 10.11.
        var viewer = new Lazy<object>(() => typeof(IUserManager).GetMethod(nameof(IUserManager.GetUserById), [typeof(Guid)])!
            .Invoke(users, [userId]) ?? throw new InvalidOperationException("Guide user is unavailable"));
        // Jellyfin lists its built-in tuners under the service name "Emby".
        var channels = visible.OfType<LiveTvChannel>()
            .Where(c => c.ServiceName == "Emby" && c.ExternalId is { } id
                && (id.StartsWith("m3u_", StringComparison.Ordinal) || IsNativeXtreamChannel(id)))
            .Select(c => (ClientId: c.Id.ToString("N"), c.ExternalId)).ToArray();
        var result = new ChannelGroupResult();
        var m3u = channels.Where(c => !IsNativeXtreamChannel(c.ExternalId)).ToArray();
        if (m3u.Length != 0)
        {
            var snapshot = await _m3uReader.GetAsync(m3u.Select(c => c.ExternalId), ReadM3uAsync, token);
            snapshot.AddVisibleChannelsTo(result, m3u);
        }
        var xtream = channels.Where(c => IsNativeXtreamChannel(c.ExternalId)).ToArray();
        if (xtream.Length != 0)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(XtreamGuideWait);
            try
            {
                var snapshot = await _xtreamReader.GetAsync(xtream.Select(c => c.ExternalId), ReadXtreamAsync, deadline.Token)
                    .WaitAsync(deadline.Token);
                snapshot.AddVisibleChannelsTo(result, xtream);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
            {
                result.Partial = true;
                (result.UnresolvedChannelIds ??= []).AddRange(xtream.Select(c => c.ClientId));
            }
        }
        try
        {
            result.ReadProgrammeCategories(tasks, importTime, () => ReadCategories(viewer, visible.Select(c => c.Id).ToArray(), token));
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read guide programme categories");
        }
        var items = visible.Count == 0 ? [] : BuildChannelDtos(visible, options, viewer.Value);
        result.SetChannels(items, item => item.Id.ToString("N"));
        return result;
    }

    private IEnumerable<BaseItemDto> BuildChannelDtos(IReadOnlyList<BaseItem> channels, DtoOptions options, object viewer)
    {
        // Jellyfin 12 adds skipVisibilityCheck; leave visibility checks enabled.
        var method = typeof(IDtoService).GetMethod(nameof(IDtoService.GetBaseItemDtos))!;
        object?[] args = method.GetParameters().Length == 4
            ? [channels, options, viewer, null]
            : [channels, options, viewer, null, false];
        return (IEnumerable<BaseItemDto>)method.Invoke(dtos, args)!;
    }

    private string[] ReadCategories(Lazy<object> viewer, Guid[] channelIds, CancellationToken token)
    {
        if (channelIds.Length == 0) return [];
        var query = (InternalItemsQuery)Activator.CreateInstance(typeof(InternalItemsQuery), viewer.Value)!;
        query.IncludeItemTypes = [BaseItemKind.LiveTvProgram];
        query.ChannelIds = channelIds;
        query.Limit = 1;
        query.EnableTotalRecordCount = false;

        // Program categories come from tags on the programs, not the channels.
        var found = new List<string>();
        foreach (var (key, tag) in new[] { ("movies", "Movie"), ("series", "Series"), ("sports", "Sports"),
                                           ("news", "News"), ("kids", "Kids"), ("premiere", "Premiere") })
        {
            token.ThrowIfCancellationRequested();
            query.Tags = [tag];
            if (library.GetItemsResult(query).Items.Count != 0) found.Add(key);
        }
        return found.ToArray();
    }

    private async Task ReadM3uAsync(ChannelGroupSnapshot result, CancellationToken token)
    {
        var configured = ((LiveTvOptions)config.GetConfiguration("livetv")).TunerHosts;
        // Read the imported M3U lineup instead of downloading the playlist again.
        // Jellyfin's tuner may refetch it even with caching enabled.
        foreach (var host in configured.Where(host => IsM3u(host.Type)))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrEmpty(host.Id) || Path.GetFileName(host.Id) != host.Id)
                {
                    result.MissingTuner = true;
                    continue;
                }
                var path = Path.Combine(config.ApplicationPaths.CachePath, host.Id + "_channels");
                var bytes = await File.ReadAllBytesAsync(path, token);
                var channels = ReadImportedChannels(bytes);
                foreach (var channel in channels)
                    result.Add(0, host.Id, channel?.Id, channel?.ChannelGroup is { } group ? [group] : null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                result.MissingTuner = true;
                logger.LogDebug("Could not read the imported M3U lineup for tuner {HostId}", host.Id);
            }
        }
    }

    private static ImportedM3uChannel[] ReadImportedChannels(ReadOnlySpan<byte> bytes)
    {
        // Jellyfin may leave extra bytes from an older, longer lineup on disk.
        // Read one complete array and reject a truncated one.
        var preamble = Encoding.UTF8.Preamble;
        if (bytes.StartsWith(preamble)) bytes = bytes[preamble.Length..];
        var reader = new Utf8JsonReader(bytes);
        return JsonSerializer.Deserialize<ImportedM3uChannel[]>(ref reader)
            ?? throw new JsonException("Expected an imported channel list");
    }

    private async Task ReadXtreamAsync(ChannelGroupSnapshot result, CancellationToken token)
    {
        try
        {
            var xtream = tuners.TunerHosts.FirstOrDefault(t => string.Equals(t.Type, "xtream-library", StringComparison.OrdinalIgnoreCase));
            if (xtream == null)
            {
                result.MissingTuner = true;
                return;
            }
            // A tuner lookup may finish filling its cache after the viewer stops waiting.
            var channels = await xtream.GetChannels(true, lifetime.ApplicationStopping);
            token.ThrowIfCancellationRequested();
            foreach (var channel in channels)
                result.Add(1, channel.TunerHostId, channel.Id, [channel.ChannelGroup]);
        }
        catch (Exception) when (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            token.ThrowIfCancellationRequested();
            result.MissingTuner = true;
            logger.LogWarning("Could not read channel groups from Xtream Library");
        }
    }

    private static bool IsM3u(string type) => string.Equals(type, "m3u", StringComparison.OrdinalIgnoreCase);
    private static bool IsNativeXtreamChannel(string id) => id.StartsWith("xtream_", StringComparison.Ordinal);

    private sealed record ImportedM3uChannel(string? Id, string? ChannelGroup);
}
