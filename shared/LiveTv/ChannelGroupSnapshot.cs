using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Tasks;

namespace Moonfin.LiveTv
{
    internal sealed class ChannelGroupResult
    {
        [JsonPropertyName("supported")] public bool Supported => Groups.Count != 0;
        [JsonPropertyName("partial")] public bool Partial { get; set; }
        [JsonIgnore]
        public Dictionary<string, string[]> Groups { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal);
        [JsonPropertyName("channels")] public object[] Channels { get; private set; } = Array.Empty<object>();

        public void SetChannels(IEnumerable<BaseItemDto> channels, Func<BaseItemDto, string> clientId)
        {
            Channels = channels.Select(channel =>
            {
                var id = clientId(channel);
                return new
                {
                    Id = id, channel.Name, channel.ChannelNumber, channel.ServerId,
                    Type = Convert.ToString(channel.Type), MediaType = Convert.ToString(channel.MediaType),
                    channel.ImageTags, channel.UserData,
                    groups = Groups.TryGetValue(id, out var labels) ? labels : Array.Empty<string>()
                };
            }).ToArray();
        }

        // Include only IDs we couldn't match in a partial result.
        // The other channels were checked, even if they have no group.
        [JsonPropertyName("unresolvedChannelIds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? UnresolvedChannelIds { get; set; }

        [JsonPropertyName("programmeCategories"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? ProgrammeCategories { get; private set; }

        // Check the import state for each request; an import can change the channels and listings.
        public static long? StableImportTime(ITaskManager tasks)
        {
            var import = tasks.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == "RefreshGuide");
            if (import != null && import.State != TaskState.Idle)
                return null;
            return import?.LastExecutionResult?.EndTimeUtc.Ticks ?? 0;
        }

        public void ReadProgrammeCategories(ITaskManager tasks, long? importTime, Func<string[]> read)
        {
            if (!importTime.HasValue || StableImportTime(tasks) != importTime) return;
            var categories = read();
            if (StableImportTime(tasks) == importTime) ProgrammeCategories = categories;
        }
    }

    internal sealed class ChannelGroupSnapshot
    {
        private const int MaxGroupsPerChannel = 256;
        private readonly HashSet<string> _visibleIds;
        private readonly Dictionary<string, (int Adapter, string? HostId, string ExternalId, HashSet<string> Groups)> _channels =
            new Dictionary<string, (int, string?, string, HashSet<string>)>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _incompleteGroups = new HashSet<string>(StringComparer.Ordinal);
        public bool MissingTuner { get; set; }

        // Only a complete read can clear a previous ID conflict.
        // Each viewer can only resolve conflicts for channels they can access.
        public void UpdateConflicts(HashSet<string> known, bool preserveKnown = false)
        {
            if (MissingTuner || preserveKnown)
                _ambiguous.UnionWith(known.Where(_visibleIds.Contains));
            else
                known.ExceptWith(_visibleIds);
            known.UnionWith(_ambiguous);
        }

        public ChannelGroupSnapshot(IEnumerable<string> visibleIds) =>
            _visibleIds = new HashSet<string>(visibleIds, StringComparer.OrdinalIgnoreCase);

        public bool IsVisible(string id) => _visibleIds.Contains(id);

        public bool Covers(IEnumerable<string> visibleIds) => _visibleIds.IsSupersetOf(visibleIds);

        public bool CanShare => _visibleIds.Count <= 100_000;

        public void Add(int adapter, string? hostId, string? externalId, IEnumerable<string>? labels, bool incomplete = false)
        {
            if (string.IsNullOrEmpty(externalId) || !_visibleIds.Contains(externalId) || _ambiguous.Contains(externalId)) return;
            if (!_channels.TryGetValue(externalId, out var source))
            {
                source = (adapter, hostId, externalId, new HashSet<string>(StringComparer.Ordinal));
                _channels.Add(externalId, source);
            }
            else if (source.Adapter != adapter || source.HostId != hostId || source.ExternalId != externalId)
            {
                _channels.Remove(externalId);
                _ambiguous.Add(externalId);
                return;
            }

            if (incomplete) _incompleteGroups.Add(externalId);
            if (labels == null) return;
            foreach (var label in labels)
            {
                if (string.IsNullOrWhiteSpace(label)) continue;
                var name = label.Trim();
                if (source.Groups.Count == MaxGroupsPerChannel && !source.Groups.Contains(name))
                {
                    _incompleteGroups.Add(externalId);
                    continue;
                }
                source.Groups.Add(name);
            }
        }

        public ChannelGroupResult ForVisibleChannels(IEnumerable<(string ClientId, string ExternalId)> channels)
        {
            var result = new ChannelGroupResult();
            AddVisibleChannelsTo(result, channels);
            return result;
        }

        public void AddVisibleChannelsTo(ChannelGroupResult result, IEnumerable<(string ClientId, string ExternalId)> channels)
        {
            foreach (var channel in channels)
            {
                if (string.IsNullOrEmpty(channel.ExternalId) || _ambiguous.Contains(channel.ExternalId) ||
                    !_channels.TryGetValue(channel.ExternalId, out var source) || source.ExternalId != channel.ExternalId)
                {
                    // Either server may return no tuners after losing its cache.
                    result.Partial = true;
                    result.UnresolvedChannelIds ??= new List<string>();
                    // A duplicate channel ID has no safe group. Only a missing tuner result
                    // can reuse the viewer's previous group.
                    if (!_ambiguous.Contains(channel.ExternalId))
                        result.UnresolvedChannelIds.Add(channel.ClientId);
                    continue;
                }
                if (_incompleteGroups.Contains(channel.ExternalId))
                {
                    result.Partial = true;
                    (result.UnresolvedChannelIds ??= new List<string>()).Add(channel.ClientId);
                }
                if (source.Groups.Count > 0)
                    result.Groups[channel.ClientId] = source.Groups.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            }
        }
    }

    // Overlapping requests can share a tuner read if it covers their channels.
    internal sealed class ChannelGroupReader
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1);
        private readonly HashSet<string> _conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private ChannelGroupSnapshot? _lastRead;

        public async Task<ChannelGroupSnapshot> GetAsync(
            IEnumerable<string> visibleIds, Func<ChannelGroupSnapshot, CancellationToken, Task> read, CancellationToken token)
        {
            var previous = Volatile.Read(ref _lastRead);
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_lastRead != null && _lastRead != previous && _lastRead.Covers(visibleIds))
                    return _lastRead;
                var current = new ChannelGroupSnapshot(visibleIds);
                await read(current, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                current.UpdateConflicts(_conflicts);
                Volatile.Write(ref _lastRead, current.CanShare ? current : null);
                return current;
            }
            finally { _gate.Release(); }
        }
    }
}
