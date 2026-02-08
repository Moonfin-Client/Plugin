const API = {
    toCamelCase: function(obj) {
        if (!obj || typeof obj !== 'object' || Array.isArray(obj)) return obj;
        var result = {};
        var keys = Object.keys(obj);
        for (var i = 0; i < keys.length; i++) {
            var key = keys[i];
            var camel = key.charAt(0).toLowerCase() + key.slice(1);
            result[camel] = obj[key];
        }
        return result;
    },

    getApiClient() {
        return window.ApiClient || (window.connectionManager && window.connectionManager.currentApiClient());
    },

    async getCurrentUser() {
        const api = this.getApiClient();
        if (!api) return null;
        
        try {
            const user = await api.getCurrentUser();
            return user;
        } catch (e) {
            console.error('[Moonfin] Failed to get current user:', e);
            return null;
        }
    },

    async getUserViews() {
        const api = this.getApiClient();
        if (!api) return [];

        try {
            const userId = api.getCurrentUserId();
            const result = await api.getUserViews(userId);
            return result.Items || [];
        } catch (e) {
            console.error('[Moonfin] Failed to get user views:', e);
            return [];
        }
    },

    async getRandomItems(options = {}) {
        const api = this.getApiClient();
        if (!api) return [];

        const { contentType = 'both', limit = 10 } = options;

        try {
            const userId = api.getCurrentUserId();
            
            let includeItemTypes = [];
            if (contentType === 'movies' || contentType === 'both') {
                includeItemTypes.push('Movie');
            }
            if (contentType === 'tv' || contentType === 'both') {
                includeItemTypes.push('Series');
            }

            const params = {
                userId: userId,
                includeItemTypes: includeItemTypes.join(','),
                sortBy: 'Random',
                limit: limit,
                recursive: true,
                hasThemeSong: false,
                hasThemeVideo: false,
                fields: 'Overview,Genres,CommunityRating,CriticRating,OfficialRating,RunTimeTicks,ProductionYear,ProviderIds',
                imageTypeLimit: 1,
                enableImageTypes: 'Backdrop,Logo,Primary'
            };

            const result = await api.getItems(userId, params);
            return result.Items || [];
        } catch (e) {
            console.error('[Moonfin] Failed to get random items:', e);
            return [];
        }
    },

    getImageUrl(item, imageType = 'Backdrop', options = {}) {
        const api = this.getApiClient();
        if (!api || !item) return null;

        const itemId = item.Id;
        const { maxWidth = 1920, maxHeight = 1080, quality = 96 } = options;

        if (!item.ImageTags || !item.ImageTags[imageType]) {
            // For backdrop, check BackdropImageTags
            if (imageType === 'Backdrop' && item.BackdropImageTags && item.BackdropImageTags.length > 0) {
                return api.getScaledImageUrl(itemId, {
                    type: 'Backdrop',
                    maxWidth,
                    maxHeight,
                    quality,
                    tag: item.BackdropImageTags[0]
                });
            }
            return null;
        }

        return api.getScaledImageUrl(itemId, {
            type: imageType,
            maxWidth,
            maxHeight,
            quality,
            tag: item.ImageTags[imageType]
        });
    },

    getUserAvatarUrl(user) {
        const api = this.getApiClient();
        if (!api || !user) return null;

        if (user.PrimaryImageTag) {
            return api.getUserImageUrl(user.Id, {
                type: 'Primary',
                tag: user.PrimaryImageTag
            });
        }
        return null;
    },

    navigateToItem(itemId) {
        if (window.Emby && window.Emby.Page) {
            window.Emby.Page.show('/details?id=' + itemId);
        } else if (window.appRouter) {
            window.appRouter.show('/details?id=' + itemId);
        }
    },

    navigateTo(path) {
        if (window.Emby && window.Emby.Page) {
            window.Emby.Page.show(path);
        } else if (window.appRouter) {
            window.appRouter.show(path);
        }
    },

    async getGenres(parentId) {
        var api = this.getApiClient();
        if (!api) return [];

        try {
            var userId = api.getCurrentUserId();
            var params = {
                userId: userId,
                includeItemTypes: 'Movie,Series',
                sortBy: 'SortName',
                sortOrder: 'Ascending',
                recursive: true,
                enableTotalRecordCount: true
            };
            if (parentId) {
                params.parentId = parentId;
            }
            var result = await api.getGenres(userId, params);
            return result.Items || [];
        } catch (e) {
            console.error('[Moonfin] Failed to get genres:', e);
            return [];
        }
    },

    async getGenreItems(genreName, options) {
        var api = this.getApiClient();
        if (!api) return { Items: [], TotalRecordCount: 0 };

        try {
            var userId = api.getCurrentUserId();
            var params = {
                userId: userId,
                genres: genreName,
                includeItemTypes: options.includeItemTypes || 'Movie,Series',
                sortBy: options.sortBy || 'SortName',
                sortOrder: options.sortOrder || 'Ascending',
                recursive: true,
                startIndex: options.startIndex || 0,
                limit: options.limit || 100,
                enableTotalRecordCount: true,
                fields: 'PrimaryImageAspectRatio,ProductionYear,CommunityRating,OfficialRating,RunTimeTicks,Overview,Genres',
                imageTypeLimit: 1,
                enableImageTypes: 'Primary,Backdrop'
            };
            if (options.parentId) {
                params.parentId = options.parentId;
            }
            if (options.nameStartsWith) {
                params.nameStartsWith = options.nameStartsWith;
            }
            if (options.nameLessThan) {
                params.nameLessThan = options.nameLessThan;
            }
            var result = await api.getItems(userId, params);
            return result;
        } catch (e) {
            console.error('[Moonfin] Failed to get genre items:', e);
            return { Items: [], TotalRecordCount: 0 };
        }
    },

    getPrimaryImageUrl(item, options) {
        var api = this.getApiClient();
        if (!api || !item) return null;

        var opts = options || {};
        var maxWidth = opts.maxWidth || 300;
        var quality = opts.quality || 90;

        if (item.ImageTags && item.ImageTags.Primary) {
            return api.getScaledImageUrl(item.Id, {
                type: 'Primary',
                maxWidth: maxWidth,
                quality: quality,
                tag: item.ImageTags.Primary
            });
        }
        return null;
    },

    getBackdropUrl(item, options) {
        var api = this.getApiClient();
        if (!api || !item) return null;

        var opts = options || {};
        var maxWidth = opts.maxWidth || 780;
        var quality = opts.quality || 80;

        if (item.BackdropImageTags && item.BackdropImageTags.length > 0) {
            return api.getScaledImageUrl(item.Id, {
                type: 'Backdrop',
                maxWidth: maxWidth,
                quality: quality,
                tag: item.BackdropImageTags[0]
            });
        }
        // Fallback for items without their own backdrop
        if (item.ParentBackdropItemId && item.ParentBackdropImageTags && item.ParentBackdropImageTags.length > 0) {
            return api.getScaledImageUrl(item.ParentBackdropItemId, {
                type: 'Backdrop',
                maxWidth: maxWidth,
                quality: quality,
                tag: item.ParentBackdropImageTags[0]
            });
        }
        return null;
    }
};
