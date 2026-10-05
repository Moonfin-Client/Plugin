using System.Collections.Generic;
using System.Linq;
using Emby.Plugins.Moonfin.Models;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Globalization;
using Moonfin.Seasonal;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// A user's parental limit as the server holds it, for rows whose items never pass through
    /// the server's own item query.
    /// </summary>
    public class ServerRatingLimitService
    {
        private readonly ILocalizationManager _localization;
        private readonly IServerConfigurationManager _configManager;

        public ServerRatingLimitService(ILocalizationManager localization, IServerConfigurationManager configManager)
        {
            _localization = localization;
            _configManager = configManager;
        }

        public RatingLimit ForUser(User? user)
        {
            var max = user?.Policy?.MaxParentalRating;
            if (max == null) return RatingLimit.None;

            var values = new List<KeyValuePair<string, int>>();
            foreach (var rating in _localization.GetParentalRatings())
            {
                int? value = rating.Value;
                if (value.HasValue && !string.IsNullOrWhiteSpace(rating.Name))
                    values.Add(new KeyValuePair<string, int>(rating.Name, value.Value));
            }

            return new RatingLimit(max, values, _configManager.Configuration.MetadataCountryCode);
        }

        public List<CustomRowItem> Filter(RatingLimit limit, List<CustomRowItem> items)
        {
            return limit.HasLimit ? items.Where(i => limit.IsAllowed(i.OfficialRating)).ToList() : items;
        }
    }
}
