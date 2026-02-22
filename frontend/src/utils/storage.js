const Storage = {
    STORAGE_KEY: 'moonfin_settings',
    PROFILES_KEY: 'moonfin_profiles',
    SNAPSHOT_KEY: 'moonfin_sync_snapshot',
    SYNC_PREF_KEY: 'moonfin_sync_enabled',
    CLIENT_ID: 'moonfin-web',

    syncState: {
        serverAvailable: null,
        lastSyncTime: null,
        lastSyncError: null,
        syncing: false,
        mdblistAvailable: false,
        tmdbAvailable: false,
        adminDefaults: null
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

    // ─── Profile Storage ────────────────────────────────────────────

    getProfiles() {
        try {
            const stored = localStorage.getItem(this.PROFILES_KEY);
            if (stored) {
                return JSON.parse(stored);
            }
        } catch (e) {
            console.error('[Moonfin] Failed to read profiles:', e);
        }
        return {};
    },

    getProfile(profileName) {
        const profiles = this.getProfiles();
        return profiles[profileName] || {};
    },

    saveProfile(profileName, settings, syncToServer = true) {
        try {
            const profiles = this.getProfiles();
            profiles[profileName] = settings;
            localStorage.setItem(this.PROFILES_KEY, JSON.stringify(profiles));

            // Dispatch change event with resolved settings for current device
            const resolved = this.getAll();
            window.dispatchEvent(new CustomEvent('moonfin-settings-changed', { detail: resolved }));

            if (syncToServer && this.syncState.serverAvailable && this.isSyncEnabled()) {
                this.saveProfileToServer(profileName, settings);
            }
        } catch (e) {
            console.error('[Moonfin] Failed to save profile:', e);
        }
    },

    deleteProfile(profileName) {
        if (profileName === 'global') return;
        const profiles = this.getProfiles();
        delete profiles[profileName];
        localStorage.setItem(this.PROFILES_KEY, JSON.stringify(profiles));

        if (this.syncState.serverAvailable && this.isSyncEnabled()) {
            this.deleteProfileFromServer(profileName);
        }
    },

    // ─── Resolution Chain ───────────────────────────────────────────

    /**
     * Gets resolved flat settings for the current device.
     * Resolution: device profile → global → admin defaults → built-in defaults.
     */
    getAll(profileOverride) {
        const deviceProfile = profileOverride || Device.getProfileName();
        return this.resolveSettings(deviceProfile);
    },

    resolveSettings(profileName) {
        const profiles = this.getProfiles();
        const global = profiles.global || {};
        const device = (profileName !== 'global') ? (profiles[profileName] || {}) : {};
        const adminDefaults = this.syncState.adminDefaults || {};

        const resolved = {};
        const allKeys = Object.keys(this.defaults);

        for (const key of allKeys) {
            // Resolution chain: device → global → admin defaults → built-in
            if (device[key] !== undefined && device[key] !== null) {
                resolved[key] = device[key];
            } else if (global[key] !== undefined && global[key] !== null) {
                resolved[key] = global[key];
            } else if (adminDefaults[key] !== undefined && adminDefaults[key] !== null) {
                resolved[key] = adminDefaults[key];
            } else {
                resolved[key] = this.defaults[key];
            }
        }

        return resolved;
    },

    get(key, defaultValue = null) {
        const settings = this.getAll();
        return key in settings ? settings[key] : (defaultValue !== null ? defaultValue : this.defaults[key]);
    },

    set(key, value, profileName) {
        profileName = profileName || this._activeEditProfile || 'global';
        const profile = this.getProfile(profileName);
        profile[key] = value;
        this.saveProfile(profileName, profile);
    },

    saveAll(settings, syncToServer = true) {
        this.saveProfile('global', settings, syncToServer);
    },

    reset(profileName) {
        if (profileName && profileName !== 'global') {
            this.deleteProfile(profileName);
        } else {
            // Reset all profiles
            localStorage.removeItem(this.PROFILES_KEY);
            localStorage.removeItem(this.SNAPSHOT_KEY);
            if (this.syncState.serverAvailable && this.isSyncEnabled()) {
                this.saveAllProfilesToServer({});
            }
        }
    },

    // ─── Active Edit Profile ────────────────────────────────────────

    _activeEditProfile: 'global',

    setActiveEditProfile(profileName) {
        this._activeEditProfile = profileName;
    },

    getActiveEditProfile() {
        return this._activeEditProfile;
    },

    // ─── Sync Preference ────────────────────────────────────────────

    isSyncEnabled() {
        try {
            const val = localStorage.getItem(this.SYNC_PREF_KEY);
            return val === null ? true : val === 'true';
        } catch (e) {
            return true;
        }
    },

    setSyncEnabled(enabled) {
        localStorage.setItem(this.SYNC_PREF_KEY, String(enabled));
    },

    // ─── Backward Compatibility ─────────────────────────────────────

    _migrateFromLegacy() {
        try {
            const legacy = localStorage.getItem(this.STORAGE_KEY);
            const profiles = localStorage.getItem(this.PROFILES_KEY);

            if (legacy && !profiles) {
                const legacySettings = JSON.parse(legacy);
                console.log('[Moonfin] Migrating legacy settings to profile format');
                this.saveProfile('global', legacySettings, false);
                // Keep the legacy key around for one session as backup
                localStorage.setItem(this.STORAGE_KEY + '_backup', legacy);
                localStorage.removeItem(this.STORAGE_KEY);
            }
        } catch (e) {
            console.error('[Moonfin] Legacy migration failed:', e);
        }
    },

    // ─── Color Helpers ──────────────────────────────────────────────

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

    // ─── Server Communication ───────────────────────────────────────

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

                // Store admin defaults for the resolution chain
                if (data.defaultSettings) {
                    this.syncState.adminDefaults = this._mapProfileFromServer(data.defaultSettings);
                }

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
                const serverData = API.toCamelCase(await response.json());
                console.log('[Moonfin] Fetched settings from server');
                return this._mapEnvelopeFromServer(serverData);
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

    async saveAllProfilesToServer(profiles) {
        if (this.syncState.serverAvailable === false || !this.isSyncEnabled()) {
            return false;
        }

        try {
            this.syncState.syncing = true;
            const serverUrl = window.ApiClient?.serverAddress?.() || '';
            
            const envelope = this._mapEnvelopeToServer(profiles);
            envelope.syncEnabled = this.isSyncEnabled();

            const response = await fetch(`${serverUrl}/Moonfin/Settings`, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    ...this.getAuthHeader()
                },
                body: JSON.stringify({
                    settings: envelope,
                    clientId: this.CLIENT_ID,
                    mergeMode: 'replace'
                })
            });

            if (response.ok) {
                this.syncState.lastSyncTime = Date.now();
                this.syncState.lastSyncError = null;
                console.log('[Moonfin] All profiles saved to server');
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

    async saveProfileToServer(profileName, profileSettings) {
        if (this.syncState.serverAvailable === false || !this.isSyncEnabled()) {
            return false;
        }

        try {
            this.syncState.syncing = true;
            const serverUrl = window.ApiClient?.serverAddress?.() || '';
            const serverProfile = this._mapProfileToServer(profileSettings);
            
            const response = await fetch(`${serverUrl}/Moonfin/Settings/Profile/${profileName}`, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    ...this.getAuthHeader()
                },
                body: JSON.stringify({
                    profile: serverProfile,
                    clientId: this.CLIENT_ID
                })
            });

            if (response.ok) {
                this.syncState.lastSyncTime = Date.now();
                this.syncState.lastSyncError = null;
                console.log('[Moonfin] Profile "' + profileName + '" saved to server');
                return true;
            }
        } catch (e) {
            console.error('[Moonfin] Failed to save profile to server:', e);
            this.syncState.lastSyncError = e.message;
        } finally {
            this.syncState.syncing = false;
        }
        
        return false;
    },

    async deleteProfileFromServer(profileName) {
        if (this.syncState.serverAvailable === false || !this.isSyncEnabled()) {
            return false;
        }

        try {
            const serverUrl = window.ApiClient?.serverAddress?.() || '';
            await fetch(`${serverUrl}/Moonfin/Settings/Profile/${profileName}`, {
                method: 'DELETE',
                headers: this.getAuthHeader()
            });
        } catch (e) {
            console.error('[Moonfin] Failed to delete profile from server:', e);
        }

        return false;
    },


    // ─── Server ↔ Local Mapping ─────────────────────────────────────

    _mapProfileFromServer(serverProfile) {
        if (!serverProfile) return {};
        return {
            navbarEnabled: serverProfile.navbarEnabled,
            detailsPageEnabled: serverProfile.detailsPageEnabled,
            mediaBarEnabled: serverProfile.mediaBarEnabled,
            mediaBarContentType: serverProfile.mediaBarContentType,
            mediaBarItemCount: serverProfile.mediaBarItemCount,
            mediaBarOverlayOpacity: serverProfile.mediaBarOpacity,
            mediaBarOverlayColor: serverProfile.mediaBarOverlayColor,
            mediaBarAutoAdvance: serverProfile.mediaBarAutoAdvance,
            mediaBarIntervalMs: serverProfile.mediaBarIntervalMs,
            mediaBarTrailerPreview: serverProfile.mediaBarTrailerPreview,
            showShuffleButton: serverProfile.showShuffleButton,
            showGenresButton: serverProfile.showGenresButton,
            showFavoritesButton: serverProfile.showFavoritesButton,
            showCastButton: serverProfile.showCastButton,
            showSyncPlayButton: serverProfile.showSyncPlayButton,
            showLibrariesInToolbar: serverProfile.showLibrariesInToolbar,
            shuffleContentType: serverProfile.shuffleContentType,
            seasonalSurprise: serverProfile.seasonalSurprise,
            backdropEnabled: serverProfile.backdropEnabled,
            confirmExit: serverProfile.confirmExit,
            navbarPosition: serverProfile.navbarPosition,
            showClock: serverProfile.showClock,
            use24HourClock: serverProfile.use24HourClock,
            mdblistEnabled: serverProfile.mdblistEnabled,
            mdblistApiKey: serverProfile.mdblistApiKey,
            mdblistRatingSources: serverProfile.mdblistRatingSources,
            tmdbApiKey: serverProfile.tmdbApiKey,
            tmdbEpisodeRatingsEnabled: serverProfile.tmdbEpisodeRatingsEnabled
        };
    },

    _mapProfileToServer(localProfile) {
        if (!localProfile) return {};
        return {
            navbarEnabled: localProfile.navbarEnabled,
            detailsPageEnabled: localProfile.detailsPageEnabled,
            mediaBarEnabled: localProfile.mediaBarEnabled,
            mediaBarContentType: localProfile.mediaBarContentType,
            mediaBarItemCount: localProfile.mediaBarItemCount,
            mediaBarOpacity: localProfile.mediaBarOverlayOpacity,
            mediaBarOverlayColor: localProfile.mediaBarOverlayColor,
            mediaBarAutoAdvance: localProfile.mediaBarAutoAdvance,
            mediaBarIntervalMs: localProfile.mediaBarIntervalMs,
            mediaBarTrailerPreview: localProfile.mediaBarTrailerPreview,
            showShuffleButton: localProfile.showShuffleButton,
            showGenresButton: localProfile.showGenresButton,
            showFavoritesButton: localProfile.showFavoritesButton,
            showCastButton: localProfile.showCastButton,
            showSyncPlayButton: localProfile.showSyncPlayButton,
            showLibrariesInToolbar: localProfile.showLibrariesInToolbar,
            shuffleContentType: localProfile.shuffleContentType,
            seasonalSurprise: localProfile.seasonalSurprise,
            backdropEnabled: localProfile.backdropEnabled,
            confirmExit: localProfile.confirmExit,
            navbarPosition: localProfile.navbarPosition,
            showClock: localProfile.showClock,
            use24HourClock: localProfile.use24HourClock,
            mdblistEnabled: localProfile.mdblistEnabled,
            mdblistApiKey: localProfile.mdblistApiKey,
            mdblistRatingSources: localProfile.mdblistRatingSources,
            tmdbApiKey: localProfile.tmdbApiKey,
            tmdbEpisodeRatingsEnabled: localProfile.tmdbEpisodeRatingsEnabled
        };
    },

    /**
     * Maps server envelope (v2) to local profiles object.
     * Also handles v1 legacy format from the server.
     */
    _mapEnvelopeFromServer(serverData) {
        // v2 profiled format
        if (serverData.global || serverData.desktop || serverData.mobile || serverData.tv) {
            const profiles = {};
            if (serverData.global) profiles.global = this._mapProfileFromServer(serverData.global);
            if (serverData.desktop) profiles.desktop = this._mapProfileFromServer(serverData.desktop);
            if (serverData.mobile) profiles.mobile = this._mapProfileFromServer(serverData.mobile);
            if (serverData.tv) profiles.tv = this._mapProfileFromServer(serverData.tv);
            return {
                profiles: profiles,
                syncEnabled: serverData.syncEnabled !== false
            };
        }

        // v1 legacy flat format — treat as global
        const mapped = this._mapProfileFromServer(serverData);
        return {
            profiles: { global: mapped },
            syncEnabled: true
        };
    },

    _mapEnvelopeToServer(profiles) {
        const envelope = { schemaVersion: 2 };
        if (profiles.global) envelope.global = this._mapProfileToServer(profiles.global);
        if (profiles.desktop) envelope.desktop = this._mapProfileToServer(profiles.desktop);
        if (profiles.mobile) envelope.mobile = this._mapProfileToServer(profiles.mobile);
        if (profiles.tv) envelope.tv = this._mapProfileToServer(profiles.tv);
        return envelope;
    },

    // ─── Sync Snapshots ─────────────────────────────────────────────

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

    saveSnapshot(profiles) {
        try {
            localStorage.setItem(this.SNAPSHOT_KEY, JSON.stringify(profiles));
        } catch (e) {
            console.error('[Moonfin] Failed to save sync snapshot:', e);
        }
    },

    // ─── Three-Way Merge ────────────────────────────────────────────

    threeWayMergeProfiles(localProfiles, serverProfiles, snapshotProfiles) {
        const merged = {};
        const allProfileNames = new Set([
            ...Object.keys(localProfiles || {}),
            ...Object.keys(serverProfiles || {}),
            ...Object.keys(snapshotProfiles || {})
        ]);

        for (const name of allProfileNames) {
            merged[name] = this._threeWayMergeFlat(
                localProfiles[name] || {},
                serverProfiles[name] || {},
                snapshotProfiles[name] || {}
            );
        }

        return merged;
    },

    _threeWayMergeFlat(local, server, snapshot) {
        const merged = {};
        const allKeys = new Set([...Object.keys(local), ...Object.keys(server), ...Object.keys(this.defaults)]);

        for (const key of allKeys) {
            const localVal = local[key];
            const serverVal = server[key];
            const snapVal = snapshot[key];

            const localChanged = !this._deepEqual(localVal, snapVal);
            const serverChanged = !this._deepEqual(serverVal, snapVal);

            if (localChanged && !serverChanged) {
                if (localVal !== undefined) merged[key] = localVal;
            } else if (serverChanged && !localChanged) {
                if (serverVal !== undefined) merged[key] = serverVal;
            } else if (localChanged && serverChanged) {
                if (localVal !== undefined) merged[key] = localVal;
                console.log('[Moonfin] Merge conflict on "' + key + '" — local wins');
            } else {
                if (localVal !== undefined) merged[key] = localVal;
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

    // ─── Full Sync ──────────────────────────────────────────────────

    async sync(forceFromServer = false) {
        console.log('[Moonfin] Starting settings sync...' + (forceFromServer ? ' (server wins)' : ''));
        
        const pingResult = await this.pingServer();
        if (!pingResult?.installed || !pingResult?.settingsSyncEnabled) {
            console.log('[Moonfin] Server sync not available');
            return;
        }

        if (!this.isSyncEnabled()) {
            console.log('[Moonfin] User has disabled sync');
            return;
        }

        const localProfiles = this.getProfiles();
        const hasLocalProfiles = Object.keys(localProfiles).length > 0;
        const serverResult = await this.fetchFromServer();
        const serverProfiles = serverResult?.profiles || null;
        const snapshot = this.getSnapshot();

        let merged;

        if (forceFromServer && serverProfiles) {
            // Server wins — overwrite local with server profiles
            merged = {};
            const allNames = new Set([...Object.keys(localProfiles), ...Object.keys(serverProfiles)]);
            for (const name of allNames) {
                merged[name] = { ...(localProfiles[name] || {}), ...(serverProfiles[name] || {}) };
            }
            console.log('[Moonfin] Applied server profiles (manual sync)');
        } else if (serverProfiles && hasLocalProfiles && snapshot) {
            merged = this.threeWayMergeProfiles(localProfiles, serverProfiles, snapshot);
            console.log('[Moonfin] Three-way merged profiles');
        } else if (serverProfiles && hasLocalProfiles && !snapshot) {
            // First sync — local wins for conflicts
            merged = {};
            const allNames = new Set([...Object.keys(serverProfiles), ...Object.keys(localProfiles)]);
            for (const name of allNames) {
                merged[name] = { ...(serverProfiles[name] || {}), ...(localProfiles[name] || {}) };
            }
            console.log('[Moonfin] First sync — local wins, pushed to server');
        } else if (serverProfiles && !hasLocalProfiles) {
            merged = serverProfiles;
            console.log('[Moonfin] Restored profiles from server (fresh install)');
        } else if (hasLocalProfiles) {
            merged = localProfiles;
            console.log('[Moonfin] Pushed local profiles to server');
        } else {
            return;
        }

        // Save locally without triggering sync
        try {
            localStorage.setItem(this.PROFILES_KEY, JSON.stringify(merged));
            window.dispatchEvent(new CustomEvent('moonfin-settings-changed', { detail: this.getAll() }));
        } catch (e) {
            console.error('[Moonfin] Failed to save merged profiles:', e);
        }

        // Update sync preference from server if available
        if (serverResult && serverResult.syncEnabled !== undefined) {
            this.setSyncEnabled(serverResult.syncEnabled);
        }

        await this.saveAllProfilesToServer(merged);
        this.saveSnapshot(merged);
    },

    initSync() {
        if (this._initialSyncDone) return;
        this._initialSyncDone = true;

        // Migrate legacy flat settings
        this._migrateFromLegacy();

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
