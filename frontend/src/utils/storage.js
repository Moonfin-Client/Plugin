const Storage = {
    STORAGE_KEY: 'moonfin_settings',
    SNAPSHOT_KEY: 'moonfin_sync_snapshot',
    SYNC_STATUS_KEY: 'moonfin_sync_status',
    CLIENT_ID: 'moonfin-web',

    syncState: {
        serverAvailable: null,
        lastSyncTime: null,
        lastSyncError: null,
        syncing: false,
        mdblistAvailable: false,
        tmdbAvailable: false
    },

    defaults: {
        navbarEnabled: false,
        detailsPageEnabled: false,

        mediaBarEnabled: false,
        mediaBarContentType: 'both',
        mediaBarItemCount: 10,
        mediaBarOverlayOpacity: 50,
        mediaBarOverlayColor: 'gray',
        mediaBarAutoAdvance: true,
        mediaBarIntervalMs: 7000,
        mediaBarTrailerPreview: true,

        showShuffleButton: true,
        showGenresButton: true,
        showFavoritesButton: true,
        showCastButton: true,
        showSyncPlayButton: true,
        showLibrariesInToolbar: true,
        shuffleContentType: 'both',

        seasonalSurprise: 'none',
        backdropEnabled: true,
        confirmExit: true,

        navbarPosition: 'top',
        showClock: true,
        use24HourClock: false,

        mdblistEnabled: false,
        mdblistApiKey: '',
        mdblistRatingSources: ['imdb', 'tmdb', 'tomatoes', 'metacritic'],

        tmdbApiKey: '',
        tmdbEpisodeRatingsEnabled: false
    },

    colorOptions: {
        'gray': { name: 'Gray', hex: '#808080' },
        'black': { name: 'Black', hex: '#000000' },
        'dark_blue': { name: 'Dark Blue', hex: '#1A2332' },
        'purple': { name: 'Purple', hex: '#4A148C' },
        'teal': { name: 'Teal', hex: '#00695C' },
        'navy': { name: 'Navy', hex: '#0D1B2A' },
        'charcoal': { name: 'Charcoal', hex: '#36454F' },
        'brown': { name: 'Brown', hex: '#3E2723' },
        'dark_red': { name: 'Dark Red', hex: '#8B0000' },
        'dark_green': { name: 'Dark Green', hex: '#0B4F0F' },
        'slate': { name: 'Slate', hex: '#475569' },
        'indigo': { name: 'Indigo', hex: '#1E3A8A' }
    },

    seasonalOptions: {
        'none': { name: 'None' },
        'winter': { name: 'Winter' },
        'spring': { name: 'Spring' },
        'summer': { name: 'Summer' },
        'fall': { name: 'Fall' },
        'halloween': { name: 'Halloween' }
    },

    getAll() {
        try {
            const stored = localStorage.getItem(this.STORAGE_KEY);
            if (stored) {
                return { ...this.defaults, ...JSON.parse(stored) };
            }
        } catch (e) {
            console.error('[Moonfin] Failed to read settings:', e);
        }
        return { ...this.defaults };
    },

    get(key, defaultValue = null) {
        const settings = this.getAll();
        return key in settings ? settings[key] : (defaultValue !== null ? defaultValue : this.defaults[key]);
    },

    set(key, value) {
        const settings = this.getAll();
        settings[key] = value;
        this.saveAll(settings);
    },

    saveAll(settings, syncToServer = true) {
        try {
            localStorage.setItem(this.STORAGE_KEY, JSON.stringify(settings));
            window.dispatchEvent(new CustomEvent('moonfin-settings-changed', { detail: settings }));
            
            if (syncToServer && this.syncState.serverAvailable) {
                this.saveToServer(settings);
            }
        } catch (e) {
            console.error('[Moonfin] Failed to save settings:', e);
        }
    },

    reset() {
        this.saveAll({ ...this.defaults });
    },

    getColorHex(colorKey) {
        return this.colorOptions[colorKey]?.hex || this.colorOptions['gray'].hex;
    },

    getColorRgba(colorKey, opacity = 50) {
        const hex = this.getColorHex(colorKey);
        const r = parseInt(hex.slice(1, 3), 16);
        const g = parseInt(hex.slice(3, 5), 16);
        const b = parseInt(hex.slice(5, 7), 16);
        return `rgba(${r}, ${g}, ${b}, ${opacity / 100})`;
    },

    async pingServer() {
        try {
            const serverUrl = window.ApiClient?.serverAddress?.() || '';
            const response = await fetch(`${serverUrl}/Moonfin/Ping`, {
                method: 'GET',
                headers: {
                    'Content-Type': 'application/json',
                    ...this.getAuthHeader()
                }
            });

            if (response.ok) {
                const data = API.toCamelCase(await response.json());
                this.syncState.serverAvailable = data.installed && data.settingsSyncEnabled;
                this.syncState.mdblistAvailable = data.mdblistAvailable || false;
                this.syncState.tmdbAvailable = data.tmdbAvailable || false;
                console.log('[Moonfin] Server plugin detected:', data);
                return data;
            }
        } catch (e) {
            console.log('[Moonfin] Server plugin not available:', e.message);
        }
        
        this.syncState.serverAvailable = false;
        return null;
    },

    getAuthHeader() {
        const token = window.ApiClient?.accessToken?.();
        if (token) {
            return { 'Authorization': `MediaBrowser Token="${token}"` };
        }
        return {};
    },

    async fetchFromServer() {
        if (this.syncState.serverAvailable === false) {
            return null;
        }

        try {
            const serverUrl = window.ApiClient?.serverAddress?.() || '';
            const response = await fetch(`${serverUrl}/Moonfin/Settings`, {
                method: 'GET',
                headers: {
                    'Content-Type': 'application/json',
                    ...this.getAuthHeader()
                }
            });

            if (response.ok) {
                const serverSettings = API.toCamelCase(await response.json());
                console.log('[Moonfin] Fetched settings from server');
                return this.mapServerToLocal(serverSettings);
            } else if (response.status === 404) {
                console.log('[Moonfin] No settings found on server');
                return null;
            }
        } catch (e) {
            console.error('[Moonfin] Failed to fetch from server:', e);
            this.syncState.lastSyncError = e.message;
        }
        
        return null;
    },

    async saveToServer(settings, mergeMode = 'merge') {
        if (this.syncState.serverAvailable === false) {
            return false;
        }

        try {
            this.syncState.syncing = true;
            const serverUrl = window.ApiClient?.serverAddress?.() || '';
            
            const response = await fetch(`${serverUrl}/Moonfin/Settings`, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    ...this.getAuthHeader()
                },
                body: JSON.stringify({
                    settings: this.mapLocalToServer(settings),
                    clientId: this.CLIENT_ID,
                    mergeMode: mergeMode
                })
            });

            if (response.ok) {
                this.syncState.lastSyncTime = Date.now();
                this.syncState.lastSyncError = null;
                console.log('[Moonfin] Settings saved to server');
                return true;
            }
        } catch (e) {
            console.error('[Moonfin] Failed to save to server:', e);
            this.syncState.lastSyncError = e.message;
        } finally {
            this.syncState.syncing = false;
        }
        
        return false;
    },

    mapServerToLocal(serverSettings) {
        return {
            navbarEnabled: serverSettings.navbarEnabled ?? this.defaults.navbarEnabled,
            detailsPageEnabled: serverSettings.detailsPageEnabled ?? this.defaults.detailsPageEnabled,

            mediaBarEnabled: serverSettings.mediaBarEnabled ?? this.defaults.mediaBarEnabled,
            mediaBarContentType: serverSettings.mediaBarContentType ?? this.defaults.mediaBarContentType,
            mediaBarItemCount: serverSettings.mediaBarItemCount ?? this.defaults.mediaBarItemCount,
            mediaBarOverlayOpacity: serverSettings.mediaBarOpacity ?? this.defaults.mediaBarOverlayOpacity,
            mediaBarOverlayColor: serverSettings.mediaBarOverlayColor ?? this.defaults.mediaBarOverlayColor,
            mediaBarAutoAdvance: serverSettings.mediaBarAutoAdvance ?? this.defaults.mediaBarAutoAdvance,
            mediaBarIntervalMs: serverSettings.mediaBarIntervalMs ?? this.defaults.mediaBarIntervalMs,

            showShuffleButton: serverSettings.showShuffleButton ?? this.defaults.showShuffleButton,
            showGenresButton: serverSettings.showGenresButton ?? this.defaults.showGenresButton,
            showFavoritesButton: serverSettings.showFavoritesButton ?? this.defaults.showFavoritesButton,
            showCastButton: serverSettings.showCastButton ?? this.defaults.showCastButton,
            showSyncPlayButton: serverSettings.showSyncPlayButton ?? this.defaults.showSyncPlayButton,
            showLibrariesInToolbar: serverSettings.showLibrariesInToolbar ?? this.defaults.showLibrariesInToolbar,
            shuffleContentType: serverSettings.shuffleContentType ?? this.defaults.shuffleContentType,

            seasonalSurprise: serverSettings.seasonalSurprise ?? this.defaults.seasonalSurprise,
            backdropEnabled: serverSettings.backdropEnabled ?? this.defaults.backdropEnabled,
            confirmExit: serverSettings.confirmExit ?? this.defaults.confirmExit,

            navbarPosition: serverSettings.navbarPosition ?? this.defaults.navbarPosition,
            showClock: serverSettings.showClock ?? this.defaults.showClock,
            use24HourClock: serverSettings.use24HourClock ?? this.defaults.use24HourClock,

            mdblistEnabled: serverSettings.mdblistEnabled ?? this.defaults.mdblistEnabled,
            mdblistApiKey: serverSettings.mdblistApiKey ?? this.defaults.mdblistApiKey,
            mdblistRatingSources: serverSettings.mdblistRatingSources ?? this.defaults.mdblistRatingSources,

            tmdbApiKey: serverSettings.tmdbApiKey ?? this.defaults.tmdbApiKey,
            tmdbEpisodeRatingsEnabled: serverSettings.tmdbEpisodeRatingsEnabled ?? this.defaults.tmdbEpisodeRatingsEnabled
        };
    },

    getSnapshot() {
        try {
            const stored = localStorage.getItem(this.SNAPSHOT_KEY);
            if (stored) {
                return JSON.parse(stored);
            }
        } catch (e) {
            console.error('[Moonfin] Failed to read sync snapshot:', e);
        }
        return null;
    },

    saveSnapshot(settings) {
        try {
            localStorage.setItem(this.SNAPSHOT_KEY, JSON.stringify(settings));
        } catch (e) {
            console.error('[Moonfin] Failed to save sync snapshot:', e);
        }
    },

    /**
     * Three-way merge using last-synced snapshot as common ancestor.
     * Changed locally only → local; server only → server; both → local wins.
     */
    threeWayMerge(local, server, snapshot) {
        const merged = {};
        const allKeys = Object.keys(this.defaults);

        for (const key of allKeys) {
            const localVal = local[key];
            const serverVal = server[key];
            const snapVal = snapshot[key];

            const localChanged = !this._deepEqual(localVal, snapVal);
            const serverChanged = !this._deepEqual(serverVal, snapVal);

            if (localChanged && !serverChanged) {
                merged[key] = localVal;
            } else if (serverChanged && !localChanged) {
                merged[key] = serverVal;
            } else if (localChanged && serverChanged) {
                merged[key] = localVal;
                console.log('[Moonfin] Merge conflict on "' + key + '" — local wins');
            } else {
                merged[key] = localVal;
            }
        }

        return merged;
    },

    _deepEqual(a, b) {
        if (a === b) return true;
        if (a == null || b == null) return a == b;
        if (Array.isArray(a) && Array.isArray(b)) {
            if (a.length !== b.length) return false;
            for (let i = 0; i < a.length; i++) {
                if (!this._deepEqual(a[i], b[i])) return false;
            }
            return true;
        }
        if (typeof a === 'object' && typeof b === 'object') {
            const ka = Object.keys(a), kb = Object.keys(b);
            if (ka.length !== kb.length) return false;
            for (const k of ka) {
                if (!this._deepEqual(a[k], b[k])) return false;
            }
            return true;
        }
        return false;
    },

    mapLocalToServer(localSettings) {
        return {
            navbarEnabled: localSettings.navbarEnabled,
            detailsPageEnabled: localSettings.detailsPageEnabled,

            mediaBarEnabled: localSettings.mediaBarEnabled,
            mediaBarContentType: localSettings.mediaBarContentType,
            mediaBarItemCount: localSettings.mediaBarItemCount,
            mediaBarOpacity: localSettings.mediaBarOverlayOpacity,
            mediaBarOverlayColor: localSettings.mediaBarOverlayColor,
            mediaBarAutoAdvance: localSettings.mediaBarAutoAdvance,
            mediaBarIntervalMs: localSettings.mediaBarIntervalMs,

            showShuffleButton: localSettings.showShuffleButton,
            showGenresButton: localSettings.showGenresButton,
            showFavoritesButton: localSettings.showFavoritesButton,
            showCastButton: localSettings.showCastButton,
            showSyncPlayButton: localSettings.showSyncPlayButton,
            showLibrariesInToolbar: localSettings.showLibrariesInToolbar,
            shuffleContentType: localSettings.shuffleContentType,

            seasonalSurprise: localSettings.seasonalSurprise,
            backdropEnabled: localSettings.backdropEnabled,
            confirmExit: localSettings.confirmExit,

            navbarPosition: localSettings.navbarPosition,
            showClock: localSettings.showClock,
            use24HourClock: localSettings.use24HourClock,

            mdblistEnabled: localSettings.mdblistEnabled,
            mdblistApiKey: localSettings.mdblistApiKey,
            mdblistRatingSources: localSettings.mdblistRatingSources,

            tmdbApiKey: localSettings.tmdbApiKey,
            tmdbEpisodeRatingsEnabled: localSettings.tmdbEpisodeRatingsEnabled
        };
    },

    async sync(forceFromServer = false) {
        console.log('[Moonfin] Starting settings sync...' + (forceFromServer ? ' (server wins)' : ''));
        
        const pingResult = await this.pingServer();
        if (!pingResult?.installed || !pingResult?.settingsSyncEnabled) {
            console.log('[Moonfin] Server sync not available');
            return;
        }

        const hasLocalSettings = localStorage.getItem(this.STORAGE_KEY) !== null;
        const localSettings = this.getAll();
        const serverSettings = await this.fetchFromServer();
        const snapshot = this.getSnapshot();

        let merged;

        if (forceFromServer && serverSettings) {
            merged = { ...localSettings, ...serverSettings };
            console.log('[Moonfin] Applied server settings (manual sync)');
        } else if (serverSettings && hasLocalSettings && snapshot) {
            merged = this.threeWayMerge(localSettings, serverSettings, snapshot);
            console.log('[Moonfin] Three-way merged settings');
        } else if (serverSettings && hasLocalSettings && !snapshot) {
            merged = { ...serverSettings, ...localSettings };
            console.log('[Moonfin] First sync — local wins, pushed to server');
        } else if (serverSettings && !hasLocalSettings) {
            merged = serverSettings;
            console.log('[Moonfin] Restored settings from server (fresh install)');
        } else if (hasLocalSettings) {
            merged = localSettings;
            console.log('[Moonfin] Pushed local settings to server');
        } else {
            return;
        }

        this.saveAll(merged, false);
        await this.saveToServer(merged);
        this.saveSnapshot(merged);
    },

    initSync() {
        if (this._initialSyncDone) return;
        this._initialSyncDone = true;

        if (window.ApiClient?.isLoggedIn?.()) {
            setTimeout(() => this.sync(), 2000);
        } else {
            const onLogin = () => {
                if (window.ApiClient?.isLoggedIn?.()) {
                    document.removeEventListener('viewshow', onLogin);
                    setTimeout(() => this.sync(), 2000);
                }
            };
            document.addEventListener('viewshow', onLogin);
        }
    },

    getSyncStatus() {
        return {
            available: this.syncState.serverAvailable,
            lastSync: this.syncState.lastSyncTime,
            error: this.syncState.lastSyncError,
            syncing: this.syncState.syncing
        };
    }
};
