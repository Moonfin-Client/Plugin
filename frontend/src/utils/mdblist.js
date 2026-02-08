var MdbList = {
    // In-memory cache: key = "type:tmdbId" => { ratings, fetchedAt }
    _cache: {},
    _cacheTtlMs: 30 * 60 * 1000, // 30 minutes client-side cache

    // Rating source metadata with icon filenames served from Moonfin/Assets/
    sources: {
        imdb:           { name: 'IMDb',            iconFile: 'imdb.png',            color: '#F5C518', textColor: '#000' },
        tmdb:           { name: 'TMDb',            iconFile: 'tmdb.png',            color: '#01D277', textColor: '#fff' },
        trakt:          { name: 'Trakt',           iconFile: 'trakt.png',           color: '#ED1C24', textColor: '#fff' },
        tomatoes:       { name: 'Rotten Tomatoes', iconFile: 'rt-fresh.png',        color: '#FA320A', textColor: '#fff' },
        popcorn:        { name: 'RT Audience',     iconFile: 'rt-audience-up.png',  color: '#FA320A', textColor: '#fff' },
        metacritic:     { name: 'Metacritic',      iconFile: 'metacritic.png',      color: '#FFCC34', textColor: '#000' },
        metacriticuser: { name: 'Metacritic User', iconFile: 'metacritic-user.png', color: '#00CE7A', textColor: '#000' },
        letterboxd:     { name: 'Letterboxd',      iconFile: 'letterboxd.png',      color: '#00E054', textColor: '#fff' },
        rogerebert:     { name: 'RogerEbert',      iconFile: 'rogerebert.png',      color: '#E50914', textColor: '#fff' },
        myanimelist:    { name: 'MyAnimeList',     iconFile: 'mal.png',             color: '#2E51A2', textColor: '#fff' },
        anilist:        { name: 'AniList',         iconFile: 'anilist.png',         color: '#02A9FF', textColor: '#fff' }
    },

    getIconUrl: function(source, rating) {
        var info = this.sources[source];
        if (!info) return '';
        var api = API.getApiClient();
        if (!api) return '';
        var serverUrl = api._serverAddress || '';

        // Special icon variants based on score
        var score = rating ? rating.score : null;

        // Rotten Tomatoes tomatometer: Certified Fresh >= 75, Fresh >= 60, Rotten < 60
        if (source === 'tomatoes' && score != null && score > 0) {
            if (score >= 75) return serverUrl + '/Moonfin/Assets/rt-certified.png';
            if (score < 60) return serverUrl + '/Moonfin/Assets/rt-rotten.png';
        }

        // RT Audience: Verified Hot >= 90, upright popcorn >= 60, spilled < 60
        if (source === 'popcorn' && score != null && score > 0) {
            if (score >= 90) return serverUrl + '/Moonfin/Assets/rt-verified.png';
            if (score < 60) return serverUrl + '/Moonfin/Assets/rt-audience-down.png';
        }

        // Metacritic: Must-play/Must-see badge >= 81
        if (source === 'metacritic' && score != null && score >= 81) {
            return serverUrl + '/Moonfin/Assets/metacritic-score.png';
        }

        return serverUrl + '/Moonfin/Assets/' + info.iconFile;
    },

    isEnabled: function() {
        var settings = Storage.getAll();
        return settings.mdblistEnabled === true;
    },

    getSelectedSources: function() {
        var settings = Storage.getAll();
        var selected = settings.mdblistRatingSources;
        if (selected && selected.length > 0) {
            return selected;
        }
        // Default: show the most common ones
        return ['imdb', 'tmdb', 'tomatoes', 'metacritic'];
    },

    // Returns 'movie' or 'show', or null if unsupported
    getContentType: function(item) {
        if (!item) return null;
        var type = item.Type || item.type;
        if (type === 'Movie') return 'movie';
        if (type === 'Series') return 'show';
        // Episodes and Seasons map to their parent series
        if (type === 'Episode' || type === 'Season') return 'show';
        return null;
    },

    getTmdbId: function(item) {
        if (!item) return null;
        var providerIds = item.ProviderIds || item.providerIds;
        if (!providerIds) return null;
        return providerIds.Tmdb || providerIds.tmdb || null;
    },

    fetchRatings: function(item) {
        if (!this.isEnabled()) return Promise.resolve([]);

        var contentType = this.getContentType(item);
        var tmdbId = this.getTmdbId(item);

        if (!contentType || !tmdbId) return Promise.resolve([]);

        return this.fetchRatingsByTmdb(contentType, tmdbId);
    },

    fetchRatingsByTmdb: function(type, tmdbId) {
        var self = this;
        var cacheKey = type + ':' + tmdbId;

        // Check client cache
        var cached = this._cache[cacheKey];
        if (cached && (Date.now() - cached.fetchedAt) < this._cacheTtlMs) {
            return Promise.resolve(cached.ratings);
        }

        var api = API.getApiClient();
        if (!api) return Promise.resolve([]);

        var url = api.getUrl('Moonfin/MdbList/Ratings', {
            type: type,
            tmdbId: tmdbId
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
                if (resp && resp.success && resp.ratings) {
                    // Normalize rating keys
                    var ratings = [];
                    for (var i = 0; i < resp.ratings.length; i++) {
                        ratings.push(API.toCamelCase(resp.ratings[i]));
                    }
                    self._cache[cacheKey] = { ratings: ratings, fetchedAt: Date.now() };
                    resolve(ratings);
                } else {
                    if (resp && resp.error) {
                        console.warn('[Moonfin] MDBList:', resp.error);
                    }
                    resolve([]);
                }
            }, function(err) {
                console.warn('[Moonfin] MDBList fetch failed:', err);
                resolve([]);
            });
        });
    },

    // MDBList returns `value` (native scale) and `score` (0-100 normalized)
    formatRating: function(rating) {
        if (!rating || !rating.source) return null;
        var source = rating.source.toLowerCase();
        var value = rating.value;
        var score = rating.score;

        if (value == null && score == null) return null;

        // Use native value when available for better display
        switch (source) {
            case 'imdb':
                // IMDb: 0-10 scale
                return value != null ? value.toFixed(1) : (score != null ? (score / 10).toFixed(1) : null);
            case 'tmdb':
                // TMDb: 0-10 scale
                return value != null ? value.toFixed(0) + '%' : (score != null ? score.toFixed(0) + '%' : null);
            case 'tomatoes':
            case 'popcorn':
            case 'metacritic':
            case 'metacriticuser':
                // Percentage-based
                return score != null ? score.toFixed(0) + '%' : (value != null ? value.toFixed(0) + '%' : null);
            case 'letterboxd':
                // Letterboxd: 0-5 scale (value), score is 0-100
                return value != null ? value.toFixed(1) : (score != null ? (score / 20).toFixed(1) : null);
            case 'trakt':
                // Trakt: percentage
                return score != null ? score.toFixed(0) + '%' : null;
            case 'rogerebert':
                // Roger Ebert: 0-4 scale (value), score is 0-100
                return value != null ? value.toFixed(1) + '/4' : (score != null ? score.toFixed(0) + '%' : null);
            case 'myanimelist':
                // MAL: 0-10 scale
                return value != null ? value.toFixed(1) : (score != null ? (score / 10).toFixed(1) : null);
            case 'anilist':
                // AniList: percentage
                return score != null ? score.toFixed(0) + '%' : null;
            default:
                return score != null ? score.toFixed(0) + '%' : (value != null ? String(value) : null);
        }
    },

    getSourceInfo: function(source) {
        return this.sources[source] || { name: source, icon: source, color: '#666', textColor: '#fff' };
    },

    buildRatingsHtml: function(ratings, mode) {
        if (!ratings || ratings.length === 0) return '';

        var selectedSources = this.getSelectedSources();
        var html = '';

        for (var i = 0; i < selectedSources.length; i++) {
            var source = selectedSources[i];
            // Find this source in the ratings
            var rating = null;
            for (var j = 0; j < ratings.length; j++) {
                if (ratings[j].source && ratings[j].source.toLowerCase() === source) {
                    rating = ratings[j];
                    break;
                }
            }

            if (!rating) continue;

            var formatted = this.formatRating(rating);
            if (!formatted) continue;

            var info = this.getSourceInfo(source);
            var iconUrl = this.getIconUrl(source, rating);

            if (mode === 'compact') {
                html += '<span class="moonfin-mdblist-rating-compact">' +
                    '<img class="moonfin-mdblist-icon" src="' + iconUrl + '" alt="' + info.name + '" title="' + info.name + '" loading="lazy">' +
                    '<span class="moonfin-mdblist-value">' + formatted + '</span>' +
                '</span>';
            } else {
                html += '<div class="moonfin-mdblist-rating-full">' +
                    '<img class="moonfin-mdblist-icon-lg" src="' + iconUrl + '" alt="' + info.name + '" title="' + info.name + '" loading="lazy">' +
                    '<div class="moonfin-mdblist-rating-info">' +
                        '<span class="moonfin-mdblist-rating-value">' + formatted + '</span>' +
                        '<span class="moonfin-mdblist-rating-name">' + info.name + '</span>' +
                    '</div>' +
                '</div>';
            }
        }

        return html;
    }
};
