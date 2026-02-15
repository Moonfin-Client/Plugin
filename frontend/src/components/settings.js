var Settings = {
    dialog: null,
    isOpen: false,
    _toastTimeout: null,

    show: function() {
        if (this.isOpen) return;

        this.createDialog();
        // Trigger animation after append
        var self = this;
        requestAnimationFrame(function() {
            requestAnimationFrame(function() {
                if (self.dialog) {
                    self.dialog.classList.add('open');
                }
            });
        });
        this.isOpen = true;
        history.pushState({ moonfinSettings: true }, '');
    },

    hide: function(skipHistoryBack) {
        if (!this.isOpen) return;
        var self = this;

        this.isOpen = false;

        this.dialog.classList.remove('open');
        setTimeout(function() {
            if (self.dialog) {
                self.dialog.remove();
                self.dialog = null;
            }
        }, 300);

        if (!skipHistoryBack) {
            try { history.back(); } catch(e) {}
        }
    },

    showToast: function(message) {
        var existing = document.querySelector('.moonfin-toast');
        if (existing) existing.remove();

        var toast = document.createElement('div');
        toast.className = 'moonfin-toast';
        toast.textContent = message;
        document.body.appendChild(toast);

        requestAnimationFrame(function() {
            toast.classList.add('visible');
        });

        if (this._toastTimeout) clearTimeout(this._toastTimeout);
        this._toastTimeout = setTimeout(function() {
            toast.classList.remove('visible');
            setTimeout(function() { toast.remove(); }, 300);
        }, 2000);
    },

    saveSetting: function(name, value) {
        var current = Storage.getAll();
        current[name] = value;
        Storage.saveAll(current);
        console.log('[Moonfin] Setting saved:', name, '=', value);
    },

    createToggleCard: function(id, title, description, checked) {
        return '<div class="moonfin-toggle-card">' +
            '<label class="moonfin-toggle-label">' +
                '<input type="checkbox" id="moonfin-' + id + '" name="' + id + '"' + (checked ? ' checked' : '') + '>' +
                '<div class="moonfin-toggle-info">' +
                    '<div class="moonfin-toggle-title">' + title + '</div>' +
                    (description ? '<div class="moonfin-toggle-desc">' + description + '</div>' : '') +
                '</div>' +
            '</label>' +
        '</div>';
    },

    createSelectCard: function(id, title, description, options, currentValue) {
        var optionsHtml = '';
        for (var i = 0; i < options.length; i++) {
            var opt = options[i];
            optionsHtml += '<option value="' + opt.value + '"' + (String(currentValue) === String(opt.value) ? ' selected' : '') + '>' + opt.label + '</option>';
        }

        return '<div class="moonfin-select-card">' +
            '<div class="moonfin-select-info">' +
                '<div class="moonfin-toggle-title">' + title + '</div>' +
                (description ? '<div class="moonfin-toggle-desc">' + description + '</div>' : '') +
            '</div>' +
            '<select id="moonfin-' + id + '" name="' + id + '" class="moonfin-panel-select">' +
                optionsHtml +
            '</select>' +
        '</div>';
    },

    createRangeCard: function(id, title, description, min, max, step, currentValue, suffix) {
        return '<div class="moonfin-select-card">' +
            '<div class="moonfin-select-info">' +
                '<div class="moonfin-toggle-title">' + title + ' <span class="moonfin-range-value" data-for="' + id + '">' + currentValue + (suffix || '') + '</span></div>' +
                (description ? '<div class="moonfin-toggle-desc">' + description + '</div>' : '') +
            '</div>' +
            '<input type="range" id="moonfin-' + id + '" name="' + id + '" min="' + min + '" max="' + max + '" step="' + step + '" value="' + currentValue + '" class="moonfin-panel-range">' +
        '</div>';
    },

    createSection: function(icon, title, contentHtml, openByDefault) {
        return '<details class="moonfin-panel-section"' + (openByDefault ? ' open' : '') + '>' +
            '<summary class="moonfin-panel-summary">' + (icon ? icon + ' ' : '') + title + '</summary>' +
            '<div class="moonfin-panel-section-content">' +
                contentHtml +
            '</div>' +
        '</details>';
    },

    createDialog: function() {
        var existing = document.querySelector('.moonfin-settings-dialog');
        if (existing) existing.remove();

        var settings = Storage.getAll();
        var self = this;

        this.dialog = document.createElement('div');
        this.dialog.className = 'moonfin-settings-dialog';

        var uiContent =
            this.createToggleCard('navbarEnabled', 'Navigation Bar', 'Show the custom navigation bar with quick access buttons', settings.navbarEnabled) +
            this.createSelectCard('navbarPosition', 'Navbar Position', 'Show the navigation bar at the top or as a left sidebar', [
                { value: 'top', label: 'Top' },
                { value: 'left', label: 'Left (Sidebar)' }
            ], settings.navbarPosition) +
            this.createToggleCard('mediaBarEnabled', 'Media Bar', 'Show the featured media carousel on the home page', settings.mediaBarEnabled) +
            this.createToggleCard('detailsPageEnabled', 'Details Page', 'Use the custom Moonfin details page instead of the default Jellyfin one', settings.detailsPageEnabled);

        var mediaBarContent =
            this.createSelectCard('mediaBarContentType', 'Content Type', 'What type of content to show in the media bar', [
                { value: 'both', label: 'Movies & TV Shows' },
                { value: 'movies', label: 'Movies Only' },
                { value: 'tv', label: 'TV Shows Only' }
            ], settings.mediaBarContentType) +

            this.createSelectCard('mediaBarItemCount', 'Number of Items', 'How many items to display', [
                { value: '5', label: '5' },
                { value: '10', label: '10' },
                { value: '15', label: '15' },
                { value: '20', label: '20' }
            ], settings.mediaBarItemCount) +

            this.createSelectCard('mediaBarIntervalMs', 'Auto-advance Interval', 'Time between automatic slide changes', [
                { value: '5000', label: '5 seconds' },
                { value: '7000', label: '7 seconds' },
                { value: '10000', label: '10 seconds' },
                { value: '15000', label: '15 seconds' },
                { value: '20000', label: '20 seconds' }
            ], settings.mediaBarIntervalMs);

        var colorOptions = [];
        var colorKeys = Object.keys(Storage.colorOptions);
        for (var i = 0; i < colorKeys.length; i++) {
            colorOptions.push({ value: colorKeys[i], label: Storage.colorOptions[colorKeys[i]].name });
        }

        var overlayContent =
            this.createSelectCard('mediaBarOverlayColor', 'Overlay Color', 'Color of the gradient overlay on media bar items', colorOptions, settings.mediaBarOverlayColor) +
            '<div class="moonfin-color-preview" id="moonfin-color-preview" style="background:' + Storage.getColorHex(settings.mediaBarOverlayColor) + '"></div>' +
            this.createRangeCard('mediaBarOverlayOpacity', 'Overlay Opacity', 'Transparency of the gradient overlay', 0, 100, 5, settings.mediaBarOverlayOpacity, '%');

        var toolbarContent =
            this.createToggleCard('showShuffleButton', 'Shuffle Button', 'Show random content button in the toolbar', settings.showShuffleButton) +
            this.createSelectCard('shuffleContentType', 'Shuffle Content Type', 'What type of content to shuffle', [
                { value: 'both', label: 'Movies & TV Shows' },
                { value: 'movies', label: 'Movies Only' },
                { value: 'tv', label: 'TV Shows Only' }
            ], settings.shuffleContentType) +
            this.createToggleCard('showGenresButton', 'Genres Button', 'Show genres dropdown in the toolbar', settings.showGenresButton) +
            this.createToggleCard('showFavoritesButton', 'Favorites Button', 'Show favorites button in the toolbar', settings.showFavoritesButton) +
            this.createToggleCard('showCastButton', 'Cast Button', 'Show Chromecast button in the toolbar', settings.showCastButton) +
            this.createToggleCard('showSyncPlayButton', 'SyncPlay Button', 'Show SyncPlay button in the toolbar', settings.showSyncPlayButton) +
            this.createToggleCard('showLibrariesInToolbar', 'Library Shortcuts', 'Show library quick links in the toolbar', settings.showLibrariesInToolbar);

        var seasonalOptions = [];
        var seasonKeys = Object.keys(Storage.seasonalOptions);
        for (var j = 0; j < seasonKeys.length; j++) {
            seasonalOptions.push({ value: seasonKeys[j], label: Storage.seasonalOptions[seasonKeys[j]].name });
        }

        var displayContent =
            this.createToggleCard('showClock', 'Clock', 'Show a clock in the navigation bar', settings.showClock) +
            this.createToggleCard('use24HourClock', '24-Hour Format', 'Use 24-hour time format instead of 12-hour', settings.use24HourClock) +
            this.createSelectCard('seasonalSurprise', 'Seasonal Effect', 'Add a seasonal visual effect to the interface', seasonalOptions, settings.seasonalSurprise);

        var jellyseerrContent =
            '<div class="moonfin-jellyseerr-status-group">' +
                '<div class="moonfin-jellyseerr-sso-status">' +
                    '<span class="moonfin-jellyseerr-sso-indicator"></span>' +
                    '<span class="moonfin-jellyseerr-sso-text">Checking...</span>' +
                '</div>' +
            '</div>' +
            '<div class="moonfin-jellyseerr-login-group" style="display:none">' +
                '<div class="moonfin-jellyseerr-auth-type-group" style="margin-bottom:12px">' +
                    '<div class="moonfin-segmented-control">' +
                        '<button type="button" class="moonfin-segmented-btn moonfin-segmented-btn-active" data-auth-type="jellyfin">Jellyfin Account</button>' +
                        '<button type="button" class="moonfin-segmented-btn" data-auth-type="local">Local Account</button>' +
                    '</div>' +
                '</div>' +
                '<p class="moonfin-toggle-desc moonfin-jellyseerr-login-desc" style="margin:0 0 12px 0">Enter your Jellyfin credentials to sign in to Jellyseerr. Your session is stored on the server so all devices stay signed in.</p>' +
                '<div class="moonfin-jellyseerr-login-error" style="display:none"></div>' +
                '<div style="margin-bottom:8px">' +
                    '<label class="moonfin-input-label moonfin-jellyseerr-username-label">Username</label>' +
                    '<input type="text" id="jellyseerr-settings-username" autocomplete="username" class="moonfin-panel-input">' +
                '</div>' +
                '<div style="margin-bottom:12px">' +
                    '<label class="moonfin-input-label">Password</label>' +
                    '<input type="password" id="jellyseerr-settings-password" autocomplete="current-password" class="moonfin-panel-input">' +
                '</div>' +
                '<button class="moonfin-jellyseerr-settings-login-btn moonfin-panel-btn moonfin-panel-btn-primary">Sign In</button>' +
            '</div>' +
            '<div class="moonfin-jellyseerr-signedIn-group" style="display:none">' +
                '<button class="moonfin-jellyseerr-settings-logout-btn moonfin-panel-btn moonfin-panel-btn-danger">Sign Out of Jellyseerr</button>' +
            '</div>';

        var mdblistSources = [
            { key: 'imdb',           label: 'IMDb' },
            { key: 'tmdb',           label: 'TMDb' },
            { key: 'trakt',          label: 'Trakt' },
            { key: 'tomatoes',       label: 'Rotten Tomatoes (Critics)' },
            { key: 'popcorn',        label: 'Rotten Tomatoes (Audience)' },
            { key: 'metacritic',     label: 'Metacritic' },
            { key: 'metacriticuser', label: 'Metacritic User' },
            { key: 'letterboxd',     label: 'Letterboxd' },
            { key: 'rogerebert',     label: 'Roger Ebert' },
            { key: 'myanimelist',    label: 'MyAnimeList' },
            { key: 'anilist',        label: 'AniList' }
        ];
        var selectedSources = settings.mdblistRatingSources || ['imdb', 'tmdb', 'tomatoes', 'metacritic'];
        var serverUrl = (window.ApiClient && window.ApiClient.serverAddress ? window.ApiClient.serverAddress() : '') || '';
        var sourceIconFiles = {
            imdb: 'imdb.svg', tmdb: 'tmdb.svg', trakt: 'trakt.svg',
            tomatoes: 'rt-fresh.svg', popcorn: 'rt-audience-up.svg',
            metacritic: 'metacritic.svg', metacriticuser: 'metacritic-user.svg',
            letterboxd: 'letterboxd.svg', rogerebert: 'rogerebert.svg',
            myanimelist: 'mal.svg', anilist: 'anilist.svg'
        };

        // Build ordered list: enabled sources first (in saved order), then disabled
        var orderedSources = [];
        for (var oi = 0; oi < selectedSources.length; oi++) {
            for (var oj = 0; oj < mdblistSources.length; oj++) {
                if (mdblistSources[oj].key === selectedSources[oi]) {
                    orderedSources.push({ key: mdblistSources[oj].key, label: mdblistSources[oj].label, enabled: true });
                    break;
                }
            }
        }
        for (var uk = 0; uk < mdblistSources.length; uk++) {
            if (selectedSources.indexOf(mdblistSources[uk].key) === -1) {
                orderedSources.push({ key: mdblistSources[uk].key, label: mdblistSources[uk].label, enabled: false });
            }
        }

        var sourceItems = '';
        for (var si = 0; si < orderedSources.length; si++) {
            var src = orderedSources[si];
            var iconUrl = serverUrl + '/Moonfin/Assets/' + (sourceIconFiles[src.key] || 'imdb.svg');
            sourceItems += '<div class="moonfin-sortable-item' + (src.enabled ? ' moonfin-sortable-item-active' : '') + '" draggable="true" data-source="' + src.key + '">' +
                '<span class="moonfin-sortable-handle">' +
                    '<svg viewBox="0 0 24 24" width="14" height="14"><path fill="currentColor" d="M3 15h18v-2H3v2zm0 4h18v-2H3v2zm0-8h18V9H3v2zm0-6v2h18V5H3z"/></svg>' +
                '</span>' +
                '<img class="moonfin-sortable-icon" src="' + iconUrl + '" alt="' + src.label + '">' +
                '<span class="moonfin-sortable-label">' + src.label + '</span>' +
                '<button type="button" class="moonfin-sortable-toggle" title="' + (src.enabled ? 'Disable' : 'Enable') + '">' +
                    '<svg viewBox="0 0 24 24" width="16" height="16"><path fill="currentColor" d="' + (src.enabled ? 'M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z' : 'M19 13H5v-2h14v2z') + '"/></svg>' +
                '</button>' +
            '</div>';
        }

        var tmdbContent =
            this.createToggleCard('tmdbEpisodeRatingsEnabled', 'Enable Episode Ratings', 'Show TMDB ratings for individual TV episodes on the details page', settings.tmdbEpisodeRatingsEnabled) +
            '<div class="moonfin-tmdb-config" style="' + (settings.tmdbEpisodeRatingsEnabled ? '' : 'display:none') + '">' +
                (Storage.syncState.tmdbAvailable ?
                    '<div style="background-color: rgba(0, 180, 0, 0.1); border-left: 4px solid #00b400; border-radius: 4px; padding: 0.8em 1em; margin-bottom: 12px; font-size: 13px; color: rgba(255,255,255,0.8);">' +
                        'Your server admin has provided a server-wide TMDB API key. You can leave the field below blank to use it, or enter your own key.' +
                    '</div>' : '') +
                '<div style="margin-bottom:12px">' +
                    '<label class="moonfin-input-label">TMDB API Key</label>' +
                    '<input type="password" id="moonfin-tmdbApiKey" class="moonfin-panel-input" placeholder="' + (Storage.syncState.tmdbAvailable ? 'Using server key (optional override)' : 'Enter your TMDB API key or v4 token') + '" value="' + (settings.tmdbApiKey || '') + '">' +
                    '<div class="moonfin-toggle-desc" style="margin-top:4px">Get a free API key at <a href="https://www.themoviedb.org/settings/api" target="_blank" rel="noopener" style="color:#00a4dc">themoviedb.org/settings/api</a></div>' +
                '</div>' +
            '</div>';

        var mdblistContent =
            this.createToggleCard('mdblistEnabled', 'Enable MDBList Ratings', 'Show ratings from MDBList (IMDb, Rotten Tomatoes, Metacritic, etc.) on media bar and item details', settings.mdblistEnabled) +
            '<div class="moonfin-mdblist-config" style="' + (settings.mdblistEnabled ? '' : 'display:none') + '">' +
                (Storage.syncState.mdblistAvailable ?
                    '<div style="background-color: rgba(0, 180, 0, 0.1); border-left: 4px solid #00b400; border-radius: 4px; padding: 0.8em 1em; margin-bottom: 12px; font-size: 13px; color: rgba(255,255,255,0.8);">' +
                        'Your server admin has provided a server-wide MDBList API key. You can leave the field below blank to use it, or enter your own key.' +
                    '</div>' : '') +
                '<div style="margin-bottom:12px">' +
                    '<label class="moonfin-input-label">MDBList API Key</label>' +
                    '<input type="password" id="moonfin-mdblistApiKey" class="moonfin-panel-input" placeholder="' + (Storage.syncState.mdblistAvailable ? 'Using server key (optional override)' : 'Enter your mdblist.com API key') + '" value="' + (settings.mdblistApiKey || '') + '">' +
                    '<div class="moonfin-toggle-desc" style="margin-top:4px">Get your free API key at <a href="https://mdblist.com/preferences/" target="_blank" rel="noopener" style="color:#00a4dc">mdblist.com/preferences</a></div>' +
                '</div>' +
                '<div style="margin-bottom:8px">' +
                    '<label class="moonfin-input-label">Rating Sources</label>' +
                    '<p class="moonfin-toggle-desc" style="margin:0 0 8px 0">Drag to reorder. Click the icon on the right to enable or disable a source.</p>' +
                    '<div class="moonfin-sortable-list" id="moonfin-sources-sortable">' + sourceItems + '</div>' +
                '</div>' +
            '</div>';

        this.dialog.innerHTML =
            '<div class="moonfin-settings-overlay"></div>' +
            '<div class="moonfin-settings-panel">' +
                '<div class="moonfin-settings-header">' +
                    '<div class="moonfin-settings-header-left">' +
                        '<h2>Moonfin</h2>' +
                        '<span class="moonfin-settings-subtitle">Settings</span>' +
                    '</div>' +
                    '<button class="moonfin-settings-close" title="Close">' +
                        '<svg viewBox="0 0 24 24" width="20" height="20"><path fill="currentColor" d="M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z"/></svg>' +
                    '</button>' +
                '</div>' +
                '<div class="moonfin-settings-content">' +
                    this.createSection('', 'Moonfin UI', uiContent, true) +
                    this.createSection('', 'Media Bar', mediaBarContent) +
                    this.createSection('', 'Overlay Appearance', overlayContent) +
                    this.createSection('', 'Toolbar Buttons', toolbarContent) +
                    this.createSection('', 'Display', displayContent) +
                    this.createSection('', 'TMDB Episode Ratings', tmdbContent) +
                    this.createSection('', 'MDBList Ratings', mdblistContent) +
                    '<div class="moonfin-settings-jellyseerr-wrapper" style="display:none">' +
                        this.createSection('', 'Jellyseerr', jellyseerrContent) +
                    '</div>' +
                '</div>' +
                '<div class="moonfin-settings-footer">' +
                    '<div class="moonfin-sync-status" id="moonfinSyncStatus">' +
                        '<span class="moonfin-sync-indicator"></span>' +
                        '<span class="moonfin-sync-text">Checking sync...</span>' +
                    '</div>' +
                    '<div class="moonfin-settings-footer-buttons">' +
                        '<button class="moonfin-panel-btn moonfin-panel-btn-ghost moonfin-settings-reset">Reset</button>' +
                        '<button class="moonfin-panel-btn moonfin-panel-btn-ghost moonfin-settings-sync">Sync</button>' +
                        '<button class="moonfin-panel-btn moonfin-panel-btn-close moonfin-settings-close-btn">Close</button>' +
                    '</div>' +
                '</div>' +
            '</div>';

        document.body.appendChild(this.dialog);
        this.setupEventListeners();
        this.updateSyncStatus();
        this.updateJellyseerrSsoSection();
    },

    updateJellyseerrSsoSection: function() {
        var self = this;
        var wrapper = this.dialog ? this.dialog.querySelector('.moonfin-settings-jellyseerr-wrapper') : null;
        if (!wrapper) return Promise.resolve();

        // Always fetch fresh config to catch admin changes
        return Jellyseerr.fetchConfig().then(function() {
            if (!Jellyseerr.config || !Jellyseerr.config.enabled || !Jellyseerr.config.url) {
                console.log('[Moonfin] Jellyseerr not configured, hiding section. Config:', Jellyseerr.config);
                wrapper.style.display = 'none';
                return;
            }

            wrapper.style.display = '';

            var indicator = wrapper.querySelector('.moonfin-jellyseerr-sso-indicator');
            var text = wrapper.querySelector('.moonfin-jellyseerr-sso-text');
            var loginGroup = wrapper.querySelector('.moonfin-jellyseerr-login-group');
            var signedInGroup = wrapper.querySelector('.moonfin-jellyseerr-signedIn-group');

            return Jellyseerr.checkSsoStatus().then(function() {
                if (Jellyseerr.ssoStatus && Jellyseerr.ssoStatus.authenticated) {
                    indicator.className = 'moonfin-jellyseerr-sso-indicator connected';
                    var displayName = Jellyseerr.ssoStatus.displayName || 'Unknown';
                    text.textContent = 'Signed in as ' + displayName;
                    loginGroup.style.display = 'none';
                    signedInGroup.style.display = '';
                } else {
                    indicator.className = 'moonfin-jellyseerr-sso-indicator disconnected';
                    text.textContent = 'Not signed in';
                    loginGroup.style.display = '';
                    signedInGroup.style.display = 'none';

                    var api = API.getApiClient();
                    if (api && api._currentUser) {
                        var usernameInput = wrapper.querySelector('#jellyseerr-settings-username');
                        if (usernameInput && !usernameInput.value) {
                            usernameInput.value = api._currentUser.Name || '';
                        }
                    }
                }
            });
        });
    },

    updateSyncStatus: function() {
        var self = this;
        var statusEl = this.dialog ? this.dialog.querySelector('#moonfinSyncStatus') : null;
        if (!statusEl) return Promise.resolve();

        var indicator = statusEl.querySelector('.moonfin-sync-indicator');
        var text = statusEl.querySelector('.moonfin-sync-text');

        var syncStatus = Storage.getSyncStatus();

        if (syncStatus.syncing) {
            indicator.className = 'moonfin-sync-indicator syncing';
            text.textContent = 'Syncing...';
            return Promise.resolve();
        }

        // Always re-ping when the panel opens to get fresh status
        indicator.className = 'moonfin-sync-indicator checking';
        text.textContent = 'Checking server...';
        return Storage.pingServer().then(function() {
            var freshStatus = Storage.getSyncStatus();
            if (freshStatus.available) {
                indicator.className = 'moonfin-sync-indicator connected';
                if (freshStatus.lastSync) {
                    var ago = Math.round((Date.now() - freshStatus.lastSync) / 1000);
                    text.textContent = 'Synced ' + (ago < 60 ? ago + 's' : Math.round(ago / 60) + 'm') + ' ago';
                } else {
                    text.textContent = 'Server sync available';
                }
            } else {
                indicator.className = 'moonfin-sync-indicator disconnected';
                text.textContent = freshStatus.error || 'Server sync unavailable';
            }
        });
    },

    setupEventListeners: function() {
        var self = this;

        this.dialog.querySelector('.moonfin-settings-close').addEventListener('click', function() {
            self.hide();
        });

        this.dialog.querySelector('.moonfin-settings-close-btn').addEventListener('click', function() {
            self.hide();
        });

        this.dialog.querySelector('.moonfin-settings-overlay').addEventListener('click', function() {
            self.hide();
        });

        this.dialog.querySelector('.moonfin-settings-reset').addEventListener('click', function() {
            if (confirm('Reset all Moonfin settings to defaults?')) {
                Storage.reset();
                self.showToast('Settings reset to defaults');
                self.hide();
                setTimeout(function() { self.show(); }, 350);
            }
        });

        this.dialog.querySelector('.moonfin-settings-sync').addEventListener('click', function() {
            var syncBtn = self.dialog.querySelector('.moonfin-settings-sync');
            syncBtn.disabled = true;
            syncBtn.textContent = 'Syncing...';

            Storage.sync(true).then(function() {
                return self.updateSyncStatus();
            }).then(function() {
                syncBtn.disabled = false;
                syncBtn.textContent = 'Sync';
                self.showToast('Settings synced from server');
                self.hide();
                setTimeout(function() { self.show(); }, 350);
            });
        });

        var checkboxes = this.dialog.querySelectorAll('input[type="checkbox"][name]');
        for (var i = 0; i < checkboxes.length; i++) {
            (function(cb) {
                cb.addEventListener('change', function() {
                    self.saveSetting(cb.name, cb.checked);
                    self.showToast(cb.checked ? 'Enabled' : 'Disabled');

                    if (cb.name === 'mdblistEnabled') {
                        var configDiv = self.dialog.querySelector('.moonfin-mdblist-config');
                        if (configDiv) {
                            configDiv.style.display = cb.checked ? '' : 'none';
                        }
                    }

                    if (cb.name === 'tmdbEpisodeRatingsEnabled') {
                        var tmdbConfigDiv = self.dialog.querySelector('.moonfin-tmdb-config');
                        if (tmdbConfigDiv) {
                            tmdbConfigDiv.style.display = cb.checked ? '' : 'none';
                        }
                    }
                });
            })(checkboxes[i]);
        }

        var selects = this.dialog.querySelectorAll('select');
        for (var j = 0; j < selects.length; j++) {
            (function(sel) {
                sel.addEventListener('change', function() {
                    var val = sel.value;
                    var numVal = parseInt(val, 10);
                    self.saveSetting(sel.name, isNaN(numVal) ? val : numVal);
                    self.showToast('Setting updated');
                });
            })(selects[j]);
        }

        var ranges = this.dialog.querySelectorAll('input[type="range"]');
        for (var k = 0; k < ranges.length; k++) {
            (function(range) {
                range.addEventListener('input', function() {
                    var valueSpan = self.dialog.querySelector('.moonfin-range-value[data-for="' + range.name + '"]');
                    if (valueSpan) {
                        valueSpan.textContent = range.value + '%';
                    }
                });
                range.addEventListener('change', function() {
                    self.saveSetting(range.name, parseInt(range.value, 10));
                    self.showToast('Setting updated');
                });
            })(ranges[k]);
        }

        var colorSelect = this.dialog.querySelector('select[name="mediaBarOverlayColor"]');
        if (colorSelect) {
            colorSelect.addEventListener('change', function() {
                var preview = self.dialog.querySelector('#moonfin-color-preview');
                if (preview) {
                    preview.style.background = Storage.getColorHex(colorSelect.value);
                }
            });
        }

        this.dialog.addEventListener('keydown', function(e) {
            if (e.key === 'Escape') {
                self.hide();
            }
        });

        // MDBList API key - save on input with debounce + on blur
        var mdblistApiKeyInput = this.dialog.querySelector('#moonfin-mdblistApiKey');
        if (mdblistApiKeyInput) {
            var mdblistKeyTimer = null;
            mdblistApiKeyInput.addEventListener('input', function() {
                if (mdblistKeyTimer) clearTimeout(mdblistKeyTimer);
                mdblistKeyTimer = setTimeout(function() {
                    self.saveSetting('mdblistApiKey', mdblistApiKeyInput.value.trim());
                    self.showToast('API key saved');
                }, 800);
            });
            mdblistApiKeyInput.addEventListener('blur', function() {
                if (mdblistKeyTimer) clearTimeout(mdblistKeyTimer);
                self.saveSetting('mdblistApiKey', mdblistApiKeyInput.value.trim());
            });
        }

        // TMDB API key - save on input with debounce + on blur
        var tmdbApiKeyInput = this.dialog.querySelector('#moonfin-tmdbApiKey');
        if (tmdbApiKeyInput) {
            var tmdbKeyTimer = null;
            tmdbApiKeyInput.addEventListener('input', function() {
                if (tmdbKeyTimer) clearTimeout(tmdbKeyTimer);
                tmdbKeyTimer = setTimeout(function() {
                    self.saveSetting('tmdbApiKey', tmdbApiKeyInput.value.trim());
                    self.showToast('TMDB API key saved');
                }, 800);
            });
            tmdbApiKeyInput.addEventListener('blur', function() {
                if (tmdbKeyTimer) clearTimeout(tmdbKeyTimer);
                self.saveSetting('tmdbApiKey', tmdbApiKeyInput.value.trim());
            });
        }

        // --- Sortable rating sources ---
        var sortableList = this.dialog.querySelector('#moonfin-sources-sortable');
        if (sortableList) {
            var dragItem = null;
            var dragPlaceholder = document.createElement('div');
            dragPlaceholder.className = 'moonfin-sortable-placeholder';

            var saveSortableState = function() {
                var items = sortableList.querySelectorAll('.moonfin-sortable-item');
                var enabled = [];
                for (var i = 0; i < items.length; i++) {
                    if (items[i].classList.contains('moonfin-sortable-item-active')) {
                        enabled.push(items[i].getAttribute('data-source'));
                    }
                }
                self.saveSetting('mdblistRatingSources', enabled);
                self.showToast('Rating sources updated');
            };

            sortableList.addEventListener('dragstart', function(e) {
                var item = e.target.closest('.moonfin-sortable-item');
                if (!item) return;
                dragItem = item;
                item.classList.add('moonfin-sortable-dragging');
                e.dataTransfer.effectAllowed = 'move';
                e.dataTransfer.setData('text/plain', '');
            });

            sortableList.addEventListener('dragend', function() {
                if (dragItem) {
                    dragItem.classList.remove('moonfin-sortable-dragging');
                    dragItem = null;
                }
                if (dragPlaceholder.parentNode) {
                    dragPlaceholder.parentNode.removeChild(dragPlaceholder);
                }
            });

            sortableList.addEventListener('dragover', function(e) {
                e.preventDefault();
                e.dataTransfer.dropEffect = 'move';
                var target = e.target.closest('.moonfin-sortable-item');
                if (!target || target === dragItem) return;

                var rect = target.getBoundingClientRect();
                var midY = rect.top + rect.height / 2;
                if (e.clientY < midY) {
                    sortableList.insertBefore(dragPlaceholder, target);
                } else {
                    sortableList.insertBefore(dragPlaceholder, target.nextSibling);
                }
            });

            sortableList.addEventListener('drop', function(e) {
                e.preventDefault();
                if (!dragItem) return;
                if (dragPlaceholder.parentNode) {
                    sortableList.insertBefore(dragItem, dragPlaceholder);
                    dragPlaceholder.parentNode.removeChild(dragPlaceholder);
                }
                saveSortableState();
            });

            // Toggle enable/disable
            sortableList.addEventListener('click', function(e) {
                var toggleBtn = e.target.closest('.moonfin-sortable-toggle');
                if (!toggleBtn) return;
                var item = toggleBtn.closest('.moonfin-sortable-item');
                if (!item) return;
                var isActive = item.classList.toggle('moonfin-sortable-item-active');
                var svg = toggleBtn.querySelector('path');
                if (svg) {
                    svg.setAttribute('d', isActive
                        ? 'M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z'
                        : 'M19 13H5v-2h14v2z');
                }
                toggleBtn.title = isActive ? 'Disable' : 'Enable';
                saveSortableState();
            });

            // Touch drag support for mobile / TV
            (function() {
                var touchItem = null;
                var touchClone = null;
                var touchOffsetY = 0;

                sortableList.addEventListener('touchstart', function(e) {
                    var handle = e.target.closest('.moonfin-sortable-handle');
                    if (!handle) return;
                    var item = handle.closest('.moonfin-sortable-item');
                    if (!item) return;
                    touchItem = item;
                    var rect = item.getBoundingClientRect();
                    touchOffsetY = e.touches[0].clientY - rect.top;
                    touchClone = item.cloneNode(true);
                    touchClone.className = 'moonfin-sortable-item moonfin-sortable-touch-clone';
                    touchClone.style.width = rect.width + 'px';
                    touchClone.style.top = rect.top + 'px';
                    touchClone.style.left = rect.left + 'px';
                    document.body.appendChild(touchClone);
                    item.classList.add('moonfin-sortable-dragging');
                }, { passive: true });

                sortableList.addEventListener('touchmove', function(e) {
                    if (!touchItem || !touchClone) return;
                    e.preventDefault();
                    var y = e.touches[0].clientY;
                    touchClone.style.top = (y - touchOffsetY) + 'px';

                    var items = sortableList.querySelectorAll('.moonfin-sortable-item:not(.moonfin-sortable-dragging)');
                    for (var i = 0; i < items.length; i++) {
                        var rect = items[i].getBoundingClientRect();
                        var midY = rect.top + rect.height / 2;
                        if (y < midY) {
                            sortableList.insertBefore(touchItem, items[i]);
                            return;
                        }
                    }
                    sortableList.appendChild(touchItem);
                }, { passive: false });

                sortableList.addEventListener('touchend', function() {
                    if (touchItem) {
                        touchItem.classList.remove('moonfin-sortable-dragging');
                        touchItem = null;
                    }
                    if (touchClone && touchClone.parentNode) {
                        touchClone.parentNode.removeChild(touchClone);
                        touchClone = null;
                    }
                    saveSortableState();
                }, { passive: true });
            })();
        }

        var loginBtn = this.dialog.querySelector('.moonfin-jellyseerr-settings-login-btn');
        if (loginBtn) {
            loginBtn.addEventListener('click', function() {
                self.handleJellyseerrLogin();
            });
        }

        var passwordInput = this.dialog.querySelector('#jellyseerr-settings-password');
        if (passwordInput) {
            passwordInput.addEventListener('keydown', function(e) {
                if (e.key === 'Enter') {
                    self.handleJellyseerrLogin();
                }
            });
        }

        var authTypeBtns = this.dialog.querySelectorAll('.moonfin-segmented-btn[data-auth-type]');
        for (var ati = 0; ati < authTypeBtns.length; ati++) {
            authTypeBtns[ati].addEventListener('click', function() {
                var wrapper = self.dialog.querySelector('.moonfin-settings-jellyseerr-wrapper');
                if (!wrapper) return;
                var btns = wrapper.querySelectorAll('.moonfin-segmented-btn[data-auth-type]');
                for (var j = 0; j < btns.length; j++) btns[j].classList.remove('moonfin-segmented-btn-active');
                this.classList.add('moonfin-segmented-btn-active');
                var isLocal = this.getAttribute('data-auth-type') === 'local';
                var desc = wrapper.querySelector('.moonfin-jellyseerr-login-desc');
                var usernameLabel = wrapper.querySelector('.moonfin-jellyseerr-username-label');
                if (desc) desc.textContent = isLocal
                    ? 'Enter your local Jellyseerr account credentials. Your session is stored on the server so all devices stay signed in.'
                    : 'Enter your Jellyfin credentials to sign in to Jellyseerr. Your session is stored on the server so all devices stay signed in.';
                if (usernameLabel) usernameLabel.textContent = isLocal ? 'Email' : 'Username';
            });
        }

        var logoutBtn = this.dialog.querySelector('.moonfin-jellyseerr-settings-logout-btn');
        if (logoutBtn) {
            logoutBtn.addEventListener('click', function() {
                if (confirm('Sign out of Jellyseerr? You will need to sign in again to use it.')) {
                    Jellyseerr.ssoLogout().then(function() {
                        self.updateJellyseerrSsoSection();
                        self.showToast('Signed out of Jellyseerr');
                    });
                }
            });
        }
    },

    handleJellyseerrLogin: function() {
        var self = this;
        var wrapper = this.dialog ? this.dialog.querySelector('.moonfin-settings-jellyseerr-wrapper') : null;
        if (!wrapper) return;

        var username = wrapper.querySelector('#jellyseerr-settings-username');
        var password = wrapper.querySelector('#jellyseerr-settings-password');
        var errorEl = wrapper.querySelector('.moonfin-jellyseerr-login-error');
        var submitBtn = wrapper.querySelector('.moonfin-jellyseerr-settings-login-btn');

        var usernameVal = username ? username.value : '';
        var passwordVal = password ? password.value : '';

        if (!usernameVal || !passwordVal) {
            errorEl.textContent = 'Please enter your username and password.';
            errorEl.style.display = 'block';
            return;
        }

        submitBtn.disabled = true;
        submitBtn.textContent = 'Signing in...';
        errorEl.style.display = 'none';

        var activeAuthBtn = wrapper.querySelector('.moonfin-segmented-btn-active[data-auth-type]');
        var authType = activeAuthBtn ? activeAuthBtn.getAttribute('data-auth-type') : 'jellyfin';

        Jellyseerr.ssoLogin(usernameVal, passwordVal, authType).then(function(result) {
            if (result.success) {
                self.updateJellyseerrSsoSection();
                self.showToast('Signed in to Jellyseerr');
            } else {
                errorEl.textContent = result.error || 'Authentication failed';
                errorEl.style.display = 'block';
                submitBtn.disabled = false;
                submitBtn.textContent = 'Sign In';
            }
        });
    }
};
