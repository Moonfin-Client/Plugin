using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Emby.Plugins.Moonfin.Models
{
    /// <summary>
    /// One resolved item in a custom home row. Shared wire model returned by the CustomRows
    /// endpoint and stored by the IMDb-lists and custom-rows caches.
    /// </summary>
    public class CustomRowItem
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
        [JsonPropertyName("productionYear")] public int? ProductionYear { get; set; }
        [JsonPropertyName("rank")] public int? Rank { get; set; }

        // Casing matches the client's AggregatedItem.providerIds (Imdb/Tmdb/Tvdb).
        [JsonPropertyName("providerIds")] public CustomRowItemProviderIds ProviderIds { get; set; } = new CustomRowItemProviderIds();

        [JsonPropertyName("userRating")] public string? UserRating { get; set; }
        [JsonPropertyName("rating")] public double? Rating { get; set; }

        /// <summary>The certification for the server's country, looked up on TMDB. Null when unknown.</summary>
        [JsonPropertyName("officialRating")] public string? OfficialRating { get; set; }

        [JsonPropertyName("posterUrl")] public string? PosterUrl { get; set; }
        [JsonPropertyName("backdropUrl")] public string? BackdropUrl { get; set; }
        [JsonPropertyName("overview")] public string? Overview { get; set; }
        [JsonPropertyName("genres")] public List<string>? Genres { get; set; }
        [JsonPropertyName("runTimeTicks")] public long? RunTimeTicks { get; set; }

        /// <summary>
        /// The library item this entry is, as Emby's numeric item id, or null when the library
        /// lacks it. The cache keeps the match for every user, so the endpoint only hands it to
        /// clients that ask for it and only when the user can see that item.
        /// </summary>
        [JsonPropertyName("localItemId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LocalItemId { get; set; }

        /// <summary>
        /// A copy carrying <paramref name="localItemId"/>, or this item when it already does. The
        /// cached items are shared by every request, so the endpoint never changes them in place.
        /// </summary>
        public CustomRowItem WithLocalItemId(string? localItemId)
        {
            if (LocalItemId == localItemId) return this;
            var copy = (CustomRowItem)MemberwiseClone();
            copy.LocalItemId = localItemId;
            return copy;
        }
    }

    public class CustomRowItemProviderIds
    {
        [JsonPropertyName("Imdb")] public string? Imdb { get; set; }
        [JsonPropertyName("Tmdb")] public string? Tmdb { get; set; }
        [JsonPropertyName("Tvdb")] public string? Tvdb { get; set; }
    }
}
