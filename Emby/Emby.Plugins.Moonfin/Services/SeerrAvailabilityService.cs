using System;
using System.Threading.Tasks;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Whether Seerr can be offered to a user right now: switched on by the admin, not switched
    /// off by the user, and with a session Seerr still accepts.
    /// </summary>
    public class SeerrAvailabilityService
    {
        private static readonly TimeSpan SessionFreshness = TimeSpan.FromMinutes(5);

        private readonly SeerrSessionService _sessions;
        private readonly MoonfinSettingsService _settings;

        public SeerrAvailabilityService(SeerrSessionService sessions, MoonfinSettingsService settings)
        {
            _sessions = sessions;
            _settings = settings;
        }

        public async Task<bool> IsReadyForUserAsync(Guid userId)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.SeerrEnabled != true || string.IsNullOrEmpty(config.GetEffectiveSeerrUrl())) return false;

            var userSettings = await _settings.GetUserSettingsAsync(userId).ConfigureAwait(false);
            var userEnabled = userSettings?.Global?.SeerrEnabled ?? userSettings?.SeerrEnabled ?? true;
            if (!userEnabled) return false;

            return await _sessions.GetSessionAsync(userId, validate: true, maxAge: SessionFreshness).ConfigureAwait(false) != null;
        }
    }
}
