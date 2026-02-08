const Storage = {
    STORAGE_KEY: 'moonfin_settings',
    SYNC_STATUS_KEY: 'moonfin_sync_status',
    CLIENT_ID: 'moonfin-web',

    syncState: {
        serverAvailable: null,  // null = unknown, true/false
        lastSyncTime: null,
        lastSyncError: null,
        syncing: false
    },

    defaults: {
        navbarEnabled: false,
        detailsPageEnabled: false,

        mediaBarEnabled: false,
        mediaBarContentType: 'both',     // 'movies', 'tv', 'both'
        mediaBarItemCount: 10,
        mediaBarOverlayOpacity: 50,      // 0-100
        mediaBarOverlayColor: 'gray',    // color key
        mediaBarAutoAdvance: true,
        mediaBarIntervalMs: 7000,

        showShuffleButton: true,
        showGenresButton: true,
        showFavoritesButton: true,
        showCastButton: true,
        showSyncPlayButton: true,
        showLibrariesInToolbar: true,
        shuffleContentType: 'both',      // 'movies', 'tv', 'both'

        seasonalSurprise: 'none',        // 'none', 'winter', 'spring', 'summer', 'fall', 'halloween'
        backdropEnabled: true,
        confirmExit: true,

        navbarPosition: 'top',           // 'top', 'side'
        showClock: true,
        use24HourClock: false,

        mdblistEnabled: false,
        mdblistApiKey: '',
        mdblistRatingSources: ['imdb', 'tmdb', 'tomatoes', 'metacritic']
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
                console.log('[Moonfin] Fetched settings from server:', serverSettings);
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

            mediaBarEnabled: serverSettings.mediaBarEnabled ?? this.defaults.mediaBarEnabled,
            mediaBarContentType: serverSettings.mediaBarContentType ?? this.defaults.mediaBarContentType,
            mediaBarItemCount: serverSettings.mediaBarItemCount ?? this.defaults.mediaBarItemCount,
            mediaBarOverlayOpacity: serverSettings.overlayOpacity ?? this.defaults.mediaBarOverlayOpacity,
            mediaBarOverlayColor: serverSettings.overlayColor ?? this.defaults.mediaBarOverlayColor,

            showShuffleButton: serverSettings.showShuffleButton ?? this.defaults.showShuffleButton,
            showGenresButton: serverSettings.showGenresButton ?? this.defaults.showGenresButton,
            showFavoritesButton: serverSettings.showFavoritesButton ?? this.defaults.showFavoritesButton,
            showLibrariesInToolbar: serverSettings.showLibrariesInToolbar ?? this.defaults.showLibrariesInToolbar,
            shuffleContentType: serverSettings.shuffleContentType ?? this.defaults.shuffleContentType,

            seasonalSurprise: serverSettings.seasonalSurprise ?? this.defaults.seasonalSurprise,

            navbarPosition: serverSettings.navbarPosition ?? this.defaults.navbarPosition,
            showClock: serverSettings.showClock ?? this.defaults.showClock,
            use24HourClock: serverSettings.use24HourClock ?? this.defaults.use24HourClock,

            mdblistEnabled: serverSettings.mdblistEnabled ?? this.defaults.mdblistEnabled,
            mdblistApiKey: serverSettings.mdblistApiKey ?? this.defaults.mdblistApiKey,
            mdblistRatingSources: serverSettings.mdblistRatingSources ?? this.defaults.mdblistRatingSources
        };
    },

    mapLocalToServer(localSettings) {
        return {
            navbarEnabled: localSettings.navbarEnabled,

            mediaBarEnabled: localSettings.mediaBarEnabled,
            mediaBarContentType: localSettings.mediaBarContentType,
            mediaBarItemCount: localSettings.mediaBarItemCount,
            overlayOpacity: localSettings.mediaBarOverlayOpacity,
            overlayColor: localSettings.mediaBarOverlayColor,

            showShuffleButton: localSettings.showShuffleButton,
            showGenresButton: localSettings.showGenresButton,
            showFavoritesButton: localSettings.showFavoritesButton,
            showLibrariesInToolbar: localSettings.showLibrariesInToolbar,
            shuffleContentType: localSettings.shuffleContentType,

            seasonalSurprise: localSettings.seasonalSurprise,

            navbarPosition: localSettings.navbarPosition,
            showClock: localSettings.showClock,
            use24HourClock: localSettings.use24HourClock,

            mdblistEnabled: localSettings.mdblistEnabled,
            mdblistApiKey: localSettings.mdblistApiKey,
            mdblistRatingSources: localSettings.mdblistRatingSources
        };
    },

    async sync() {
        console.log('[Moonfin] Starting settings sync...');
        
        const pingResult = await this.pingServer();
        if (!pingResult?.installed || !pingResult?.settingsSyncEnabled) {
            console.log('[Moonfin] Server sync not available');
            return;
        }

        const hasLocalSettings = localStorage.getItem(this.STORAGE_KEY) !== null;
        const localSettings = this.getAll();

        const serverSettings = await this.fetchFromServer();

        if (serverSettings && hasLocalSettings) {
            // Both exist: local wins — user's local changes are most recent
            const merged = { ...serverSettings, ...localSettings };
            this.saveAll(merged, false);  // Don't re-dispatch to server yet
            // Push merged result to server so it stays in sync
            await this.saveToServer(merged);
            console.log('[Moonfin] Merged settings (local wins), pushed to server');
        } else if (serverSettings && !hasLocalSettings) {
            // Fresh install: restore from server
            this.saveAll(serverSettings, false);
            console.log('[Moonfin] Restored settings from server (fresh install)');
        } else if (hasLocalSettings) {
            // No server settings, but we have local - push to server
            await this.saveToServer(localSettings);
            console.log('[Moonfin] Pushed local settings to server');
        }
    },

    initSync() {
        // Only sync once on initial load — repeated syncs on every viewshow
        // can overwrite local changes with stale server data
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
