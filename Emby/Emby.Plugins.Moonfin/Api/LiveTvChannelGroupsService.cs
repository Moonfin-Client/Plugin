using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Tasks;
using Moonfin.LiveTv;

namespace Emby.Plugins.Moonfin.Api
{
    public sealed class LiveTvChannelGroupsService : IService, IRequiresRequest, IHasResultFactory
    {
        private static readonly HashSet<string> Conflicts = new(StringComparer.OrdinalIgnoreCase);
        private static int _completedReads;
        private readonly ILiveTvManager _manager;
        private readonly ILibraryManager _library;
        private readonly ITaskManager _tasks;
        private readonly IAuthorizationContext _auth;
        private readonly IDtoService _dtos;
        private readonly ILogger _logger;
        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; }

        public LiveTvChannelGroupsService(IApplicationHost appHost)
        {
            _dtos = appHost.Resolve<IDtoService>();
            _manager = appHost.Resolve<ILiveTvManager>();
            _library = appHost.Resolve<ILibraryManager>();
            _tasks = appHost.Resolve<ITaskManager>();
            _auth = appHost.Resolve<IAuthorizationContext>();
            _logger = appHost.Resolve<ILogManager>().GetLogger("Moonfin");
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
        }

        public async Task<object> Get(GetLiveTvChannelGroups request)
        {
            var user = AuthHelpers.GetCurrentUser(Request, _auth);
            if (user == null || user.Id == Guid.Empty || user.Policy?.EnableLiveTvAccess != true)
            {
                Request.Response.StatusCode = user == null || user.Id == Guid.Empty ? 401 : 403;
                return MoonfinJson.Result(Request, ResultFactory, null);
            }

            var token = Request.CancellationToken;
            var importTime = ChannelGroupResult.StableImportTime(_tasks);
            var query = new InternalItemsQuery(user) { EnableTotalRecordCount = false };
            var channels = _manager.GetInternalChannels(query, false, null, token).Items;
            var result = new ChannelGroupResult();
            if (channels.Length != 0)
            {
                var snapshot = await ReadTunersAsync(channels.Select(c => c.ExternalId), token).ConfigureAwait(false);
                result = snapshot.ForVisibleChannels(channels.Select(c => (c.GetClientId(), c.ExternalId)));
            }
            try
            {
                result.ReadProgrammeCategories(_tasks, importTime, () => ReadCategories(user, channels.Select(c => c.InternalId).ToArray(), token));
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                _logger.Warn("Could not read guide programme categories");
            }
            var options = new DtoOptions { EnableImages = true, EnableUserData = true, AddCurrentProgram = false };
            result.SetChannels(_dtos.GetBaseItemDtos(channels, options, user), item => item.Id);
            return MoonfinJson.Result(Request, ResultFactory, result);
        }

        private string[] ReadCategories(User user, long[] channelIds, CancellationToken token)
        {
            if (channelIds.Length == 0) return Array.Empty<string>();
            var found = new List<string>();
            Check("movies", q => q.IsMovie = true);
            Check("series", q => q.IsSeries = true);
            Check("sports", q => q.IsSports = true);
            Check("news", q => q.IsNews = true);
            Check("kids", q => q.IsKids = true);
            Check("premiere", q => q.IsPremiere = true);
            return found.ToArray();

            void Check(string key, Action<InternalItemsQuery> applyFilter)
            {
                var query = new InternalItemsQuery(user)
                {
                    IncludeItemTypes = new[] { nameof(LiveTvProgram) },
                    ParentIds = channelIds,
                    SkipAncestorNormalization = true,
                    Limit = 1,
                    EnableTotalRecordCount = false
                };
                applyFilter(query);
                if (_library.GetInternalItemIds(query, token).Length != 0) found.Add(key);
            }
        }

        internal async Task<ChannelGroupSnapshot> ReadTunersAsync(IEnumerable<string> visibleIds, CancellationToken token)
        {
            var revision = Volatile.Read(ref _completedReads);
            var result = new ChannelGroupSnapshot(visibleIds);
            await ReadHostsAsync(result, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (Conflicts)
            {
                // Don't let an older read clear a channel-ID collision found by a newer one.
                result.UpdateConflicts(Conflicts, preserveKnown: revision != _completedReads);
                _completedReads++;
            }
            return result;
        }

        private async Task ReadHostsAsync(ChannelGroupSnapshot result, CancellationToken token)
        {
            // Emby 4.9 only exposes running tuners through the manager.
            // Creating new instances would miss their caches and settings.
            if (!(_manager.GetType().GetProperty("TunerHosts")?.GetValue(_manager) is IEnumerable<ITunerHost> tuners))
            {
                result.MissingTuner = true;
                return;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(EmbyTunerGroupReader.TunerBudget);
            var reads = tuners.SelectMany(tuner => _manager.GetTunerHostInfos(tuner.Type)
                .Select(host => ReadHostAsync(tuner, host))).ToArray();
            var hosts = await Task.WhenAll(reads).ConfigureAwait(false);
            for (var index = 0; index < hosts.Length; index++)
            {
                var (host, channels, uncertain) = hosts[index];
                if (channels.Count == 0) result.MissingTuner = true;
                foreach (var channel in channels)
                    result.Add(index, host.Id, channel.Id, channel.Tags, uncertain.Contains(channel.Id));
            }

            async Task<(TunerHostInfo, List<ChannelInfo>, HashSet<string>)> ReadHostAsync(ITunerHost tuner, TunerHostInfo host)
            {
                token.ThrowIfCancellationRequested();
                var uncertain = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    var channels = await EmbyTunerGroupReader.GetChannelsAsync(
                        tuner, host, token, deadline.Token, result.IsVisible, id => uncertain.Add(id)).ConfigureAwait(false);
                    return (host, channels, uncertain);
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    _logger.Warn("Could not read channel groups from tuner {0}", host.Id);
                    return (host, new List<ChannelInfo>(), uncertain);
                }
            }
        }
    }
}
