var Tmdb = {
    // In-memory cache: key = "tmdbId:season" => { episodes, fetchedAt }
    _seasonCache: {},
    // key = "tmdbId:season:episode" => { rating, fetchedAt }
    _episodeCache: {},
    _cacheTtlMs: 30 * 60 * 1000, // 30 minutes client-side cache

    // Cache resolved series TMDB IDs: jellyfinSeriesId => tmdbSeriesId
    _seriesIdCache: {},

    isEnabled: function() {
        var settings = Storage.getAll();
        return settings.tmdbEpisodeRatingsEnabled === true;
    },

    getIconUrl: function() {
        var api = API.getApiClient();
        if (!api) return '';
        var serverUrl = api._serverAddress || '';
        return serverUrl + '/Moonfin/Assets/tmdb.svg';
    },

    /**
     * Resolve TMDB series ID from a Jellyfin series ID.
     * Episodes have their own TMDB ID in ProviderIds, so we need to
     * fetch the parent series item to get the series-level TMDB ID.
     */
    resolveSeriesTmdbId: function(jellyfinSeriesId) {
        if (!jellyfinSeriesId) return Promise.resolve(null);

        if (this._seriesIdCache[jellyfinSeriesId] !== undefined) {
            return Promise.resolve(this._seriesIdCache[jellyfinSeriesId]);
        }

        var self = this;
        var api = API.getApiClient();
        if (!api) return Promise.resolve(null);

        var userId = api.getCurrentUserId();
        return api.getItem(userId, jellyfinSeriesId).then(function(seriesItem) {
            var providerIds = seriesItem.ProviderIds || seriesItem.providerIds;
            var tmdbId = providerIds ? (providerIds.Tmdb || providerIds.tmdb) : null;
            self._seriesIdCache[jellyfinSeriesId] = tmdbId || null;
            return tmdbId || null;
        }).catch(function(err) {
            console.warn('[Moonfin] TMDB: Failed to resolve series TMDB ID:', err);
            self._seriesIdCache[jellyfinSeriesId] = null;
            return null;
        });
    },

    /**
     * Fetch a single episode rating.
     * Returns a promise resolving to { voteAverage, voteCount, name, ... } or null.
     */
    fetchEpisodeRating: function(tmdbId, season, episode) {
        if (!tmdbId || season == null || episode == null) return Promise.resolve(null);

        var cacheKey = tmdbId + ':' + season + ':' + episode;
        var cached = this._episodeCache[cacheKey];
        if (cached && (Date.now() - cached.fetchedAt) < this._cacheTtlMs) {
            return Promise.resolve(cached.rating);
        }

        var api = API.getApiClient();
        if (!api) return Promise.resolve(null);

        var self = this;
        var url = api.getUrl('Moonfin/Tmdb/EpisodeRating', {
            tmdbId: tmdbId,
            season: season,
            episode: episode
        });

        return new Promise(function(resolve) {
            api.ajax({
                type: 'GET',
                url: url,
                dataType: 'json',
                headers: {
                    'Authorization': 'MediaBrowser Token="' + api.accessToken() + '"'
                }
            }).then(function(response) {
                var resp = API.toCamelCase(response);
                if (resp && resp.success && resp.voteAverage != null) {
                    self._episodeCache[cacheKey] = { rating: resp, fetchedAt: Date.now() };
                    resolve(resp);
                } else {
                    if (resp && resp.error) {
                        console.warn('[Moonfin] TMDB:', resp.error);
                    }
                    resolve(null);
                }
            }, function(err) {
                console.warn('[Moonfin] TMDB episode rating fetch failed:', err);
                resolve(null);
            });
        });
    },

    /**
     * Fetch all episode ratings for a season (bulk).
     * Returns a promise resolving to an array of episode rating objects.
     * Also populates the individual episode cache.
     */
    fetchSeasonRatings: function(tmdbId, season) {
        if (!tmdbId || season == null) return Promise.resolve([]);

        var seasonKey = tmdbId + ':' + season;
        var cached = this._seasonCache[seasonKey];
        if (cached && (Date.now() - cached.fetchedAt) < this._cacheTtlMs) {
            return Promise.resolve(cached.episodes);
        }

        var api = API.getApiClient();
        if (!api) return Promise.resolve([]);

        var self = this;
        var url = api.getUrl('Moonfin/Tmdb/SeasonRatings', {
            tmdbId: tmdbId,
            season: season
        });

        return new Promise(function(resolve) {
            api.ajax({
                type: 'GET',
                url: url,
                dataType: 'json',
                headers: {
                    'Authorization': 'MediaBrowser Token="' + api.accessToken() + '"'
                }
            }).then(function(response) {
                var resp = API.toCamelCase(response);
                if (resp && resp.success && resp.episodes) {
                    var episodes = [];
                    for (var i = 0; i < resp.episodes.length; i++) {
                        var ep = API.toCamelCase(resp.episodes[i]);
                        episodes.push(ep);
                        if (ep.episodeNumber != null) {
                            var epKey = tmdbId + ':' + season + ':' + ep.episodeNumber;
                            self._episodeCache[epKey] = { rating: ep, fetchedAt: Date.now() };
                        }
                    }
                    self._seasonCache[seasonKey] = { episodes: episodes, fetchedAt: Date.now() };
                    resolve(episodes);
                } else {
                    if (resp && resp.error) {
                        console.warn('[Moonfin] TMDB:', resp.error);
                    }
                    resolve([]);
                }
            }, function(err) {
                console.warn('[Moonfin] TMDB season ratings fetch failed:', err);
                resolve([]);
            });
        });
    },

    /**
     * Get the rating for a specific episode from a Jellyfin item.
     * Resolves the TMDB series ID from the parent series item.
     * For efficiency, fetches the whole season and caches it.
     */
    fetchRatingForEpisode: function(item) {
        if (!this.isEnabled()) return Promise.resolve(null);
        if (!item || item.Type !== 'Episode') return Promise.resolve(null);

        var season = item.ParentIndexNumber;
        var episode = item.IndexNumber;
        if (season == null || episode == null) return Promise.resolve(null);

        // We need the parent series' TMDB ID, not the episode's
        var seriesId = item.SeriesId;
        if (!seriesId) return Promise.resolve(null);

        var self = this;
        return this.resolveSeriesTmdbId(seriesId).then(function(tmdbId) {
            if (!tmdbId) return null;

            var cacheKey = tmdbId + ':' + season + ':' + episode;
            var cached = self._episodeCache[cacheKey];
            if (cached && (Date.now() - cached.fetchedAt) < self._cacheTtlMs) {
                return cached.rating;
            }

            return self.fetchSeasonRatings(tmdbId, season).then(function(episodes) {
                for (var i = 0; i < episodes.length; i++) {
                    if (episodes[i].episodeNumber === episode) {
                        return episodes[i];
                    }
                }
                return self.fetchEpisodeRating(tmdbId, season, episode);
            });
        });
    },

    /**
     * Format a TMDB vote_average (0-10) as a display string.
     * Uses TMDB's native format: X.X
     */
    formatRating: function(voteAverage) {
        if (voteAverage == null) return null;
        // Show one decimal place, but drop .0 for whole numbers
        var val = Math.round(voteAverage * 10) / 10;
        return val % 1 === 0 ? val.toFixed(0) : val.toFixed(1);
    },

    /**
     * Build HTML for a single TMDB episode rating pill (matches mdblist style).
     */
    buildRatingHtml: function(rating) {
        if (!rating || rating.voteAverage == null) return '';
        var formatted = this.formatRating(rating.voteAverage);
        if (!formatted) return '';

        var iconUrl = this.getIconUrl();

        return '<div class="moonfin-mdblist-rating-full moonfin-tmdb-episode-rating">' +
            '<img class="moonfin-mdblist-icon-lg" src="' + iconUrl + '" alt="TMDB" title="TMDB Episode Rating" loading="lazy">' +
            '<div class="moonfin-mdblist-rating-info">' +
                '<span class="moonfin-mdblist-rating-value">' + formatted + '<span class="moonfin-tmdb-scale">/10</span></span>' +
                '<span class="moonfin-mdblist-rating-name">Episode</span>' +
            '</div>' +
        '</div>';
    },

    /**
     * Build compact HTML for episode rating (used in episode lists).
     */
    buildCompactRatingHtml: function(rating) {
        if (!rating || rating.voteAverage == null) return '';
        var formatted = this.formatRating(rating.voteAverage);
        if (!formatted) return '';

        var iconUrl = this.getIconUrl();

        return '<span class="moonfin-tmdb-ep-rating-compact">' +
            '<img class="moonfin-mdblist-icon" src="' + iconUrl + '" alt="TMDB" title="TMDB Episode Rating" loading="lazy">' +
            '<span class="moonfin-mdblist-value">' + formatted + '<span class="moonfin-tmdb-scale-sm">/10</span></span>' +
        '</span>';
    }
};
