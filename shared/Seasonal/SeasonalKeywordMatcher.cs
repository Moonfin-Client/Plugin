using System.Collections.Generic;
using System.Linq;

namespace Moonfin.Seasonal;

/// <summary>
/// A holiday's TMDB keywords: the ids for the plugin's keyword store and the names for the
/// tags Jellyfin keeps.
/// </summary>
public static class SeasonalKeywordMatcher
{
    public static HashSet<int> KeywordIds(SeasonalHoliday holiday) =>
        new HashSet<int>(holiday.Keywords.Select(k => k.Id));

    public static string[] KeywordNames(SeasonalHoliday holiday) =>
        holiday.Keywords.Select(k => k.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
}
