using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;

namespace Emby.Plugins.Moonfin.Api
{
    internal static class EmbyTunerGroupReader
    {
        internal static readonly TimeSpan TunerBudget = TimeSpan.FromSeconds(3);
        private static readonly ConcurrentDictionary<(ITunerHost, string, bool), Lazy<Task<List<ChannelInfo>>>> Reading = new();

        public static async Task<List<ChannelInfo>> GetChannelsAsync(ITunerHost tuner, TunerHostInfo host,
            CancellationToken token, CancellationToken deadline, Func<string, bool> isVisible, Action<string> unresolved)
        {
            var configuration = JsonSerializer.Serialize(host);
            // The playlist may finish loading before this viewer's imported channels are ready.
            Reading.TryGetValue((tuner, configuration, true), out var activePlaylist);
            var imported = await ReadWithinDeadlineAsync(tuner, configuration, playlist: false, deadline,
                ct => tuner.GetChannels(host, ct)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (imported.Count == 0 || tuner.Type != "m3u" || string.IsNullOrWhiteSpace(host.ProviderOptions))
                return imported;

            var options = JsonNode.Parse(host.ProviderOptions, new JsonNodeOptions { PropertyNameCaseInsensitive = true });
            var additional = options?["AdditionalTags"]?.Deserialize<string[]>();
            if (additional == null || additional.Length == 0) return imported;

            var comparer = StringComparer.OrdinalIgnoreCase;
            var tunerTags = new HashSet<string>(additional, comparer);
            var playlist = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (!deadline.IsCancellationRequested && imported.Any(c =>
                isVisible(c.Id) && (c.Tags?.Any(tunerTags.Contains) ?? false)))
            {
                var entries = await ReadWithinDeadlineAsync(tuner, configuration, playlist: true, deadline,
                    ct => ReadPlaylistAsync(tuner, configuration, options!.AsObject(), tunerTags, ct), activePlaylist).ConfigureAwait(false);
                foreach (var entry in entries)
                {
                    if (!playlist.TryGetValue(entry.Id, out var labels)) playlist[entry.Id] = labels = new List<string>();
                    labels.AddRange(entry.Tags ?? Array.Empty<string>());
                }
            }
            token.ThrowIfCancellationRequested();

            var result = new List<ChannelInfo>(imported.Count);
            foreach (var channel in imported)
            {
                var tags = channel.Tags ?? Array.Empty<string>();
                var groups = tags.Where(tag => !tunerTags.Contains(tag)).ToList();
                if (playlist.TryGetValue(channel.Id, out var labels))
                    groups.AddRange(labels.Where(label => tunerTags.Contains(label) && tags.Contains(label, comparer)));
                else if (groups.Count != tags.Length)
                    unresolved(channel.Id);
                result.Add(new ChannelInfo { Id = channel.Id, Tags = groups.ToArray() });
            }
            return result;
        }

        private static async Task<List<ChannelInfo>> ReadWithinDeadlineAsync(ITunerHost tuner, string configuration,
            bool playlist, CancellationToken deadline, Func<CancellationToken, Task<List<ChannelInfo>>> read,
            Lazy<Task<List<ChannelInfo>>>? active = null)
        {
            if (deadline.IsCancellationRequested) return new List<ChannelInfo>();
            var key = (tuner, configuration, playlist);
            var shared = active ?? Reading.GetOrAdd(key, _ => new Lazy<Task<List<ChannelInfo>>>(StartRead));
            var pending = shared.Value;
            var expired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (deadline.Register(() => expired.TrySetResult(true)))
            {
                if (await Task.WhenAny(pending, expired.Task).ConfigureAwait(false) == pending)
                    return await pending.ConfigureAwait(false);
            }
            return new List<ChannelInfo>();

            Task<List<ChannelInfo>> StartRead()
            {
                // Some tuners block before returning or ignore cancellation.
                // Keep one read shared by viewers until it actually finishes.
                var task = Task.Run(async () =>
                {
                    using var timeout = new CancellationTokenSource(TunerBudget);
                    return await read(timeout.Token).ConfigureAwait(false);
                });
                _ = task.ContinueWith(done =>
                {
                    _ = done.Exception; // All viewers may already have reached their deadlines.
                    Reading.TryRemove(key, out _);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return task;
            }
        }

        private static async Task<List<ChannelInfo>> ReadPlaylistAsync(ITunerHost tuner, string configuration,
            JsonObject options, HashSet<string> tunerTags, CancellationToken token)
        {
            try
            {
                options.Remove("AdditionalTags");
                // Don't remove required tags unless the tuner already supplies a match.
                var required = options["RequiredTags"]?.Deserialize<string[]>();
                if (required?.Any(tunerTags.Contains) == true) options.Remove("RequiredTags");
                var playlistHost = JsonSerializer.Deserialize<TunerHostInfo>(configuration)!;
                playlistHost.ProviderOptions = options.ToJsonString();

                // Use Emby's parser to keep channel IDs intact without changing the imported cache.
                var parser = tuner.GetType().GetMethod("GetChannelsInternal", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(TunerHostInfo), typeof(CancellationToken) }, null)
                    ?? throw new MissingMethodException(tuner.GetType().Name, "GetChannelsInternal");
                var read = (Func<TunerHostInfo, CancellationToken, Task<List<ChannelInfo>>>)parser.CreateDelegate(
                    typeof(Func<TunerHostInfo, CancellationToken, Task<List<ChannelInfo>>>), tuner);
                return await read(playlistHost, token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // If parsing fails, keep the groups we already have from imported channels.
                return new List<ChannelInfo>();
            }
        }
    }
}
