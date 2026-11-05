using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Hides the libraries the admin picked by adding them to each user's own My Media and Latest
    /// exclude lists, the ones Library Visibility switches. Some are picked for the Latest list only,
    /// so they stay in navigation. Nothing is ever removed, so libraries a user hid themselves stay
    /// hidden and a user can still show these again.
    /// </summary>
    public sealed class DefaultHiddenLibraries : IDisposable
    {
        private readonly IUserManager _userManager;
        private readonly ILogger _logger;

        public DefaultHiddenLibraries(IUserManager userManager, ILogger logger)
        {
            _userManager = userManager;
            _logger = logger;
            _userManager.UserCreated += OnUserCreated;
        }

        public void Dispose() => _userManager.UserCreated -= OnUserCreated;

        public void ApplyToAll() => Apply(_userManager.GetUserList(new UserQuery()));

        public void ApplyTo(Guid userId)
        {
            if (_userManager.GetUserById(userId) is { } user)
            {
                Apply(new[] { user });
            }
        }

        /// <summary>The list with the missing libraries added, or null when it already has them all.</summary>
        internal static string[]? Merge(string[]? current, IReadOnlyCollection<string> libraries)
        {
            var existing = current ?? Array.Empty<string>();
            var missing = libraries.Where(id => !existing.Contains(id, StringComparer.OrdinalIgnoreCase)).ToArray();
            return missing.Length == 0 ? null : existing.Concat(missing).ToArray();
        }

        private void Apply(IEnumerable<User> users)
        {
            var configuration = Plugin.Instance?.Configuration;
            var hidden = CleanIds(configuration?.DefaultHiddenLibraryIds);
            // A hidden library leaves the recently added rows too.
            var hiddenFromLatest = hidden
                .Union(CleanIds(configuration?.DefaultHiddenFromLatestLibraryIds), StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (hiddenFromLatest.Count == 0)
            {
                return;
            }

            foreach (var user in users)
            {
                try
                {
                    var config = _userManager.GetUserConfiguration(user);
                    var myMedia = Merge(config.MyMediaExcludes, hidden);
                    var latest = Merge(config.LatestItemsExcludes, hiddenFromLatest);
                    if (myMedia == null && latest == null)
                    {
                        continue;
                    }

                    config.MyMediaExcludes = myMedia ?? config.MyMediaExcludes;
                    config.LatestItemsExcludes = latest ?? config.LatestItemsExcludes;
                    _userManager.UpdateConfiguration(user, config);
                }
                catch (Exception ex)
                {
                    _logger.Warn("{0}", "Could not hide the default libraries for " + user.Name + ": " + ex.Message);
                }
            }
        }

        private static List<string> CleanIds(IEnumerable<string>? ids) =>
            (ids ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        private void OnUserCreated(object? sender, GenericEventArgs<User> e)
        {
            if (e.Argument != null)
            {
                Apply(new[] { e.Argument });
            }
        }
    }
}
