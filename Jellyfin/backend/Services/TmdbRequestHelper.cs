using System.Net.Http;
using System.Net.Http.Headers;
using System.Web;

namespace Moonfin.Server.Services;

/// <summary>
/// Shared TMDB request auth: v4 read tokens (JWTs starting "eyJ") use a Bearer
/// header, v3 keys go in the query string.
/// </summary>
internal static class TmdbRequestHelper
{
    public static void ApplyAuth(HttpRequestMessage request, string apiKey)
    {
        if (apiKey.StartsWith("eyJ", StringComparison.Ordinal))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        else
        {
            var uriBuilder = new UriBuilder(request.RequestUri!);
            var query = HttpUtility.ParseQueryString(uriBuilder.Query);
            query["api_key"] = apiKey;
            uriBuilder.Query = query.ToString();
            request.RequestUri = uriBuilder.Uri;
        }
    }
}
