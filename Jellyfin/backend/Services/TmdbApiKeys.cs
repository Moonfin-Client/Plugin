using System;
using System.Threading.Tasks;

namespace Moonfin.Server.Services;

/// <summary>
/// Which TMDB key a request on a user's behalf uses: the user's own from their profile when
/// they set one, otherwise the server-wide key from the plugin configuration.
/// </summary>
public static class TmdbApiKeys
{
    public static async Task<string?> ResolveAsync(MoonfinSettingsService settings, Guid? userId)
    {
        if (userId.HasValue)
        {
            var resolved = await settings.GetResolvedProfileAsync(userId.Value, "global").ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(resolved?.TmdbApiKey))
            {
                return resolved!.TmdbApiKey;
            }
        }

        return MoonfinPlugin.Instance?.Configuration?.TmdbApiKey;
    }
}
