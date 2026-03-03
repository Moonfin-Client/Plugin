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

    async getItemTrailers(itemId) {
        const api = this.getApiClient();
        if (!api || !itemId) return [];

        try {
            const userId = api.getCurrentUserId();
            const result = await api.getItems(userId, {
                ids: itemId,
                userId: userId,
                fields: 'RemoteTrailers',
                limit: 1
            });
            const item = result.Items && result.Items[0];
            return (item && item.RemoteTrailers) || [];
        } catch (e) {
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

    async getLibraryItems(parentId, options) {
        var api = this.getApiClient();
        if (!api) return { Items: [], TotalRecordCount: 0 };

        try {
            var userId = api.getCurrentUserId();
            var params = {
                userId: userId,
                parentId: parentId,
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
            if (options.nameStartsWith) {
                params.nameStartsWith = options.nameStartsWith;
            }
            if (options.nameLessThan) {
                params.nameLessThan = options.nameLessThan;
            }
            var result = await api.getItems(userId, params);
            return result;
        } catch (e) {
            console.error('[Moonfin] Failed to get library items:', e);
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
    },

    // ===== Webpack Interop for Jellyfin Web Editor Dialogs =====
    // Editor modules (metadataEditor, imageeditor, subtitleeditor, itemidentifier) are in
    // separate lazy-loaded webpack chunks. We capture __webpack_require__ via the chunk push
    // trick, find the shortcuts.js + itemContextMenu.js modules by their unique strings,
    // extract the chunk IDs from their compiled source, load those chunks, then find the
    // editor modules by their export shapes.

    getServerId: function() {
        var api = this.getApiClient();
        if (!api) return null;
        try {
            if (api.serverInfo && typeof api.serverInfo === 'function') return api.serverInfo().Id;
            if (api._serverInfo) return api._serverInfo.Id;
            if (api.serverId && typeof api.serverId === 'function') return api.serverId();
        } catch(e) {}
        return null;
    },

    _initWebpackRequire: function() {
        if (window.__moonfin_wp_require) return true;
        // Auto-detect: the compiled global varies by build config
        // Production jellyfin-web uses just 'webpackChunk' (no suffix)
        var chunkArr = window.webpackChunk
            || window.webpackChunkjellyfin_web
            || window['webpackChunk_jellyfin_web'];
        if (!chunkArr) {
            // Scan window for any webpackChunk* array
            var keys = Object.keys(window);
            for (var i = 0; i < keys.length; i++) {
                if (keys[i].indexOf('webpackChunk') === 0 && Array.isArray(window[keys[i]])) {
                    chunkArr = window[keys[i]];
                    break;
                }
            }
        }
        if (!chunkArr) {
            console.warn('[Moonfin] No webpackChunk array found');
            return false;
        }
        chunkArr.push([['moonfin'], {}, function(__webpack_require__) {
            window.__moonfin_wp_require = __webpack_require__;
        }]);
        return !!window.__moonfin_wp_require;
    },

    _extractChunkIds: function(factorySource) {
        var ids = [];
        // Match numeric chunk IDs: .e(12345)
        var regex = /\.e\((\d+)\)/g;
        var match;
        while ((match = regex.exec(factorySource)) !== null) {
            var id = parseInt(match[1]);
            if (ids.indexOf(id) === -1) ids.push(id);
        }
        // Match string chunk IDs: .e("chunk-name")
        regex = /\.e\("([^"]+)"\)/g;
        while ((match = regex.exec(factorySource)) !== null) {
            if (ids.indexOf(match[1]) === -1) ids.push(match[1]);
        }
        return ids;
    },

    _loadChunks: function(req, chunkIds) {
        var loads = [];
        for (var i = 0; i < chunkIds.length; i++) {
            loads.push(req.e(chunkIds[i]).catch(function() {}));
        }
        return Promise.all(loads);
    },

    _findFactoryByHint: function(req, hint) {
        var factories = req.m || {};
        var keys = Object.keys(factories);
        for (var i = 0; i < keys.length; i++) {
            try {
                if (factories[keys[i]].toString().indexOf(hint) !== -1) {
                    return keys[i];
                }
            } catch(e) {}
        }
        return null;
    },

    _findFactoryByHints: function(req, hints) {
        var factories = req.m || {};
        var keys = Object.keys(factories);
        for (var i = 0; i < keys.length; i++) {
            try {
                var src = factories[keys[i]].toString();
                var allMatch = true;
                for (var h = 0; h < hints.length; h++) {
                    if (src.indexOf(hints[h]) === -1) { allMatch = false; break; }
                }
                if (allMatch) return keys[i];
            } catch(e) {}
        }
        return null;
    },

    _editorModulesPromise: null,

    _loadEditorModules: function() {
        if (this._editorModulesPromise) return this._editorModulesPromise;
        if (window.__moonfin_editors && Object.keys(window.__moonfin_editors).length >= 4) {
            return Promise.resolve(true);
        }

        var apiSelf = this;
        this._editorModulesPromise = new Promise(function(resolve) {
            if (!apiSelf._initWebpackRequire()) {
                resolve(false);
                return;
            }

            var req = window.__moonfin_wp_require;

            // Step 1: Find shortcuts.js module by its unique 'playAllFromHere' export
            var shortcutsId = apiSelf._findFactoryByHint(req, 'playAllFromHere');
            if (!shortcutsId) {
                console.warn('[Moonfin] Could not find shortcuts module in webpack factories');
                resolve(false);
                return;
            }

            // Step 2: Extract chunk IDs from shortcuts factory and load them
            var shortcutChunkIds = apiSelf._extractChunkIds(req.m[shortcutsId].toString());
            console.log('[Moonfin] Loading shortcuts chunks:', shortcutChunkIds);

            apiSelf._loadChunks(req, shortcutChunkIds).then(function() {
                // Step 3: Find itemContextMenu module by its unique command strings
                var icmId = apiSelf._findFactoryByHints(req, ['editimages', 'editsubtitles', 'identify']);
                if (icmId) {
                    // Step 4: Extract and load itemContextMenu's chunks (editor modules)
                    var icmChunkIds = apiSelf._extractChunkIds(req.m[icmId].toString());
                    console.log('[Moonfin] Loading itemContextMenu chunks:', icmChunkIds);
                    return apiSelf._loadChunks(req, icmChunkIds);
                }
            }).then(function() {
                // Step 5: All editor chunks loaded. Find editor modules by export shape.
                var editors = {};
                var factories = req.m;
                var cache = req.c || {};
                var fkeys = Object.keys(factories);

                // Source hints to narrow down which factories to try instantiating
                var editorHints = [
                    'editItemMetadataForm', 'MessageItemSaved',
                    'imageType', 'hasChanges',
                    'subtitleList', 'btnOpenUploadMenu',
                    'showFindNew', 'identifyResults'
                ];

                for (var i = 0; i < fkeys.length; i++) {
                    var id = fkeys[i];
                    var mod;

                    // Check cache first (no side effects)
                    if (cache[id]) {
                        mod = cache[id].exports;
                    } else {
                        // Only try modules whose factory contains editor-related strings
                        var src;
                        try { src = factories[id].toString(); } catch(e) { continue; }
                        var isEditor = false;
                        for (var h = 0; h < editorHints.length; h++) {
                            if (src.indexOf(editorHints[h]) !== -1) { isEditor = true; break; }
                        }
                        if (!isEditor) continue;

                        try { mod = req(id); } catch(e) { continue; }
                    }

                    if (!mod) continue;
                    apiSelf._matchEditorExports(mod, editors);

                    if (editors.metadata && editors.identifier && editors.image && editors.subtitle) break;
                }

                window.__moonfin_editors = editors;
                var found = Object.keys(editors);
                console.log('[Moonfin] Found editor modules:', found.join(', ') || 'none');
                resolve(found.length > 0);
            }).catch(function(e) {
                console.error('[Moonfin] Error loading editor chunks:', e);
                resolve(false);
            });
        });

        // Allow retry on failure
        this._editorModulesPromise.then(function(success) {
            if (!success) apiSelf._editorModulesPromise = null;
        });

        return this._editorModulesPromise;
    },

    _matchEditorExports: function(mod, editors) {
        if (!mod) return;
        var d = mod.default || mod;

        // metadataEditor: default export with show() + embed() — unique shape
        if (!editors.metadata && d && typeof d.show === 'function' && typeof d.embed === 'function') {
            editors.metadata = d;
            return;
        }

        // itemIdentifier: named exports show() + showFindNew() — unique shape
        if (!editors.identifier && typeof mod.show === 'function' && typeof mod.showFindNew === 'function') {
            editors.identifier = mod;
            return;
        }

        // imageeditor: named export show(options), length <= 1, no showFindNew/embed
        if (!editors.image && typeof mod.show === 'function' && !mod.showFindNew &&
            !(d && typeof d.embed === 'function') && mod.show.length <= 1 &&
            mod !== (editors.identifier || null)) {
            editors.image = mod;
            return;
        }

        // subtitleeditor: default export with show(itemId, serverId), no embed
        if (!editors.subtitle && d && d !== mod && typeof d.show === 'function' &&
            !d.embed && d.show.length >= 2) {
            editors.subtitle = d;
            return;
        }
    },

    openMetadataEditor: function(itemId) {
        var serverId = this.getServerId();
        return this._loadEditorModules().then(function(loaded) {
            var ed = window.__moonfin_editors && window.__moonfin_editors.metadata;
            if (!ed) return false;
            ed.show(itemId, serverId);
            return true;
        }).catch(function(e) {
            console.warn('[Moonfin] Failed to open metadata editor:', e);
            return false;
        });
    },

    openImageEditor: function(itemId) {
        var serverId = this.getServerId();
        return this._loadEditorModules().then(function(loaded) {
            var ed = window.__moonfin_editors && window.__moonfin_editors.image;
            if (!ed) return false;
            var showFn = ed.show || (ed.default && ed.default.show);
            if (!showFn) return false;
            showFn({ itemId: itemId, serverId: serverId });
            return true;
        }).catch(function(e) {
            console.warn('[Moonfin] Failed to open image editor:', e);
            return false;
        });
    },

    openSubtitleEditor: function(itemId) {
        var serverId = this.getServerId();
        return this._loadEditorModules().then(function(loaded) {
            var ed = window.__moonfin_editors && window.__moonfin_editors.subtitle;
            if (!ed) return false;
            ed.show(itemId, serverId);
            return true;
        }).catch(function(e) {
            console.warn('[Moonfin] Failed to open subtitle editor:', e);
            return false;
        });
    },

    openItemIdentifier: function(itemId) {
        var serverId = this.getServerId();
        return this._loadEditorModules().then(function(loaded) {
            var ed = window.__moonfin_editors && window.__moonfin_editors.identifier;
            if (!ed) return false;
            ed.show(itemId, serverId);
            return true;
        }).catch(function(e) {
            console.warn('[Moonfin] Failed to open item identifier:', e);
            return false;
        });
    }
};
