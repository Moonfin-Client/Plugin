const Sidebar = {
    container: null,
    overlay: null,
    mobileTrigger: null,
    clockInterval: null,
    initialized: false,
    libraries: [],
    currentUser: null,
    librariesExpanded: false,

    isMobile: function() {
        return window.innerWidth <= 768;
    },

    async init() {
        if (this.initialized) return;

        console.log('[Moonfin] Initializing sidebar...');

        try {
            await this.waitForApi();
        } catch (e) {
            console.error('[Moonfin] Sidebar: Failed to initialize -', e.message);
            return;
        }

        this.createSidebar();

        await this.loadUserData();

        this.setupEventListeners();

        this.startClock();

        this.initialized = true;
        console.log('[Moonfin] Sidebar initialized');
    },

    waitForApi() {
        return new Promise(function(resolve, reject) {
            var attempts = 0;
            var maxAttempts = 100;

            var check = function() {
                var api = API.getApiClient();
                if (api && api._currentUser && api._currentUser.Id) {
                    resolve();
                } else if (attempts >= maxAttempts) {
                    reject(new Error('API timeout'));
                } else {
                    attempts++;
                    setTimeout(check, 100);
                }
            };
            check();
        });
    },

    createSidebar() {
        var existing = document.querySelector('.moonfin-sidebar');
        if (existing) existing.remove();

        var existingOverlay = document.querySelector('.moonfin-sidebar-overlay');
        if (existingOverlay) existingOverlay.remove();

        var existingTrigger = document.querySelector('.moonfin-sidebar-mobile-trigger');
        if (existingTrigger) existingTrigger.remove();

        var settings = Storage.getAll();

        this.container = document.createElement('nav');
        this.container.className = 'moonfin-sidebar';
        this.container.innerHTML = [
            '<div class="moonfin-sidebar-user">',
            '    <button class="moonfin-sidebar-user-btn" title="User Menu">',
            '        <div class="moonfin-sidebar-avatar">',
            '            <span class="moonfin-sidebar-initial">U</span>',
            '        </div>',
            '        <span class="moonfin-sidebar-username">User</span>',
            '    </button>',
            '</div>',
            '',
            '<div class="moonfin-sidebar-separator"></div>',
            '',
            '<div class="moonfin-sidebar-nav">',
            '',
            '    <button class="moonfin-sidebar-item moonfin-sidebar-home" data-action="home" title="Home">',
            '        <svg class="moonfin-sidebar-icon" viewBox="0 0 24 24"><path d="M10 20v-6h4v6h5v-8h3L12 3 2 12h3v8z"/></svg>',
            '        <span class="moonfin-sidebar-label">Home</span>',
            '    </button>',
            '',
            '    <button class="moonfin-sidebar-item moonfin-sidebar-search" data-action="search" title="Search">',
            '        <svg class="moonfin-sidebar-icon" viewBox="0 0 24 24"><path d="M15.5 14h-.79l-.28-.27C15.41 12.59 16 11.11 16 9.5 16 5.91 13.09 3 9.5 3S3 5.91 3 9.5 5.91 16 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z"/></svg>',
            '        <span class="moonfin-sidebar-label">Search</span>',
            '    </button>',
            '',
            '    <button class="moonfin-sidebar-item moonfin-sidebar-shuffle' + (!settings.showShuffleButton ? ' hidden' : '') + '" data-action="shuffle" title="Shuffle">',
            '        <svg class="moonfin-sidebar-icon" viewBox="0 0 24 24"><path d="M10.59 9.17L5.41 4 4 5.41l5.17 5.17 1.42-1.41zM14.5 4l2.04 2.04L4 18.59 5.41 20 17.96 7.46 20 9.5V4h-5.5zm.33 9.41l-1.41 1.41 3.13 3.13L14.5 20H20v-5.5l-2.04 2.04-3.13-3.13z"/></svg>',
            '        <span class="moonfin-sidebar-label">Shuffle</span>',
            '    </button>',
            '',
            '    <button class="moonfin-sidebar-item moonfin-sidebar-genres' + (!settings.showGenresButton ? ' hidden' : '') + '" data-action="genres" title="Genres">',
            '        <svg class="moonfin-sidebar-icon" viewBox="0 0 24 24"><path d="M8.11,19.45C5.94,18.65 4.22,16.78 3.71,14.35L2.05,6.54C1.81,5.46 2.5,4.4 3.58,4.17L13.35,2.1L13.38,2.09C14.45,1.88 15.5,2.57 15.72,3.63L16.07,5.3L20.42,6.23H20.45C21.5,6.47 22.18,7.53 21.96,8.59L20.3,16.41C19.5,20.18 15.78,22.6 12,21.79C10.42,21.46 9.08,20.61 8.11,19.45V19.45M20,8.18L10.23,6.1L8.57,13.92V13.95C8,16.63 9.73,19.27 12.42,19.84C15.11,20.41 17.77,18.69 18.34,16L20,8.18M16,16.5C15.37,17.57 14.11,18.16 12.83,17.89C11.56,17.62 10.65,16.57 10.5,15.34L16,16.5M8.47,5.17L4,6.13L5.66,13.94L5.67,13.97C5.82,14.68 6.12,15.32 6.53,15.87C6.43,15.1 6.45,14.3 6.62,13.5L7.05,11.5C6.6,11.42 6.21,11.17 6,10.81C6.06,10.2 6.56,9.66 7.25,9.5C7.33,9.5 7.4,9.5 7.5,9.5L8.28,5.69C8.32,5.5 8.38,5.33 8.47,5.17M15.03,12.23C15.35,11.7 16.03,11.42 16.72,11.57C17.41,11.71 17.91,12.24 18,12.86C17.67,13.38 17,13.66 16.3,13.5C15.61,13.37 15.11,12.84 15.03,12.23M10.15,11.19C10.47,10.66 11.14,10.38 11.83,10.53C12.5,10.67 13.03,11.21 13.11,11.82C12.78,12.34 12.11,12.63 11.42,12.5C10.73,12.33 10.23,11.8 10.15,11.19M11.97,4.43L13.93,4.85L13.77,4.05L11.97,4.43Z"/></svg>',
            '        <span class="moonfin-sidebar-label">Genres</span>',
            '    </button>',
            '',
            '    <button class="moonfin-sidebar-item moonfin-sidebar-favorites' + (!settings.showFavoritesButton ? ' hidden' : '') + '" data-action="favorites" title="Favorites">',
            '        <svg class="moonfin-sidebar-icon" viewBox="0 0 24 24"><path d="M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z"/></svg>',
            '        <span class="moonfin-sidebar-label">Favorites</span>',
            '    </button>',
            '',
            '    <button class="moonfin-sidebar-item moonfin-sidebar-jellyseerr hidden" data-action="jellyseerr" title="Jellyseerr">',
            '        <svg class="moonfin-sidebar-icon" viewBox="0 0 96 96" fill="currentColor" xmlns="http://www.w3.org/2000/svg"><path fill-opacity="0.13" d="M96.1,48c0,26.31 -21.18,47.71 -47.48,48C22.31,96.28 0.68,75.33 0.11,49.03C-0.45,22.73 20.26,0.87 46.56,0.03c26.3,-0.85 48.37,19.63 49.5,45.92"/><path fill-opacity="0.4" d="M42.87,45.59h-2.49c-3.33,12.42 -4.89,30.36 -4.17,43.88c0.79,14.88 4.85,29.2 6.2,29.2s-0.71,-9.11 0.21,-29.17c0.62,-13.38 4.41,-25.95 4.7,-43.91h-4.46z"/><path fill-opacity="0.4" d="M64.09,45.86h2.49c3.33,12.42 4.89,30.36 4.17,43.88c-0.79,14.88 -4.85,29.2 -6.2,29.2s0.71,-9.11 -0.21,-29.17c-0.62,-13.38 -4.41,-25.95 -4.7,-43.91h4.46z"/><path fill-opacity="0.53" d="M38.05,70.69l-5.06,-1.13s-1.17,7.43 -1.61,11.15c-0.71,6.02 -1.57,14.34 -1.23,20.71c0.37,7.01 2.29,13.76 2.92,13.76s-0.34,-4.29 0.1,-13.75c0.29,-6.3 1.33,-13.87 2.58,-20.72c0.62,-3.38 2.42,-10.02 2.42,-10.02z"/><path fill-opacity="0.53" d="M59.41,70.16h1.55c2.08,7.76 2.47,18.96 2.02,27.4c-0.49,9.29 -3.03,18.23 -3.87,18.23s0.45,-5.69 -0.13,-18.21c-0.39,-8.35 -2.16,-16.2 -2.35,-27.41h2.78z"/><path fill-opacity="0.67" d="M35.18,39.95l-5.67,-2.02s-2.08,13.26 -2.87,19.92c-1.26,10.75 -3.75,25.61 -3.14,36.99c0.67,12.53 4.09,24.58 5.22,24.58s-0.6,-7.67 0.18,-24.56c0.52,-11.26 3.97,-21.94 5.14,-37.01c0.47,-5.99 1.37,-17.9 1.37,-17.9z"/><path fill-opacity="0.67" d="M53.91,45.86l-5.11,0.87s0.68,9.93 0.68,15.58c0,9.16 0.36,18.42 0.33,28.03c-0.03,11.05 1.81,29.55 2.77,29.55s4.06,-23.82 4.72,-38.06c0.44,-9.5 -0.97,-17.84 -1.22,-23.52c-0.22,-5.06 -0.93,-11.88 -0.93,-11.88z"/><path d="M82.09,48.88c0,12.9 -2.19,13.68 -5.78,19.15c-2.58,3.92 2.64,6.96 0.55,8.04c-2.5,1.29 -1.71,-1.05 -6.67,-2.38c-2.15,-0.57 -6.84,0.06 -8.74,0.43c-1.88,0.36 -7.61,-2.83 -9.14,-3.24c-2.27,-0.61 -7.84,2.35 -11.23,2.35s-6.94,-2.96 -11.46,-1.75c-5.36,1.44 -11.83,4.94 -12.81,3.79c-1.88,-2.19 4.1,-3.86 1.88,-7.76c-1.4,-2.47 -6.27,-8.98 -6.41,-15.56c-0.45,-21.16 17.07,-39.03 35.84,-39.03s33.95,16.28 33.95,34.49"/><path fill-rule="evenodd" d="M46.95,19.63c-10.25,0 -24.58,10.61 -24.58,20.86c0,1.14 -0.92,2.06 -2.06,2.06s-2.06,-0.92 -2.06,-2.06c0,-12.52 16.17,-24.98 28.7,-24.98c1.14,0 2.06,0.92 2.06,2.06s-0.92,2.06 -2.06,2.06z"/><path fill-opacity="0.87" d="M62.12,58.41c-1.09,1.78 -2.57,3.21 -4.32,4.19c-0.75,0.41 -1.54,0.74 -2.36,0.98c-2.45,1.1 -5.2,1.69 -7.99,1.75c-9.53,0.17 -17.44,-5.92 -17.75,-13.65c-0.15,-3.79 2.11,-7.72 3.86,-10.75c1.48,-2.56 4.03,-6.97 7.39,-8.73c6.85,-3.6 16.08,0.21 20.7,8.55c1.34,2.42 2.19,5.07 2.48,7.71c0.21,0.86 0.33,1.74 0.34,2.62c0.03,2.29 -0.63,4.55 -1.91,6.58c-0.13,0.26 -0.27,0.51 -0.42,0.75z"/><path d="M47.07,39.46c5.94,0 10.75,4.81 10.75,10.75s-4.81,10.75 -10.75,10.75s-10.75,-4.81 -10.75,-10.75c0,-1.1 0.16,-2.16 0.47,-3.17c0.84,1.87 2.72,3.17 4.9,3.17c2.97,0 5.37,-2.41 5.37,-5.37c0,-2.18 -1.3,-4.06 -3.17,-4.9c1,-0.31 2.06,-0.47 3.17,-0.47z"/></svg>',
            '        <span class="moonfin-sidebar-label">Jellyseerr</span>',
            '    </button>',
            '',
            '    <button class="moonfin-sidebar-item moonfin-sidebar-cast' + (!settings.showCastButton ? ' hidden' : '') + '" data-action="cast" title="Cast">',
            '        <svg class="moonfin-sidebar-icon" viewBox="0 0 24 24"><path d="M1 18v3h3c0-1.66-1.34-3-3-3m0-4v2c2.76 0 5 2.24 5 5h2c0-3.87-3.13-7-7-7m0-4v2a9 9 0 0 1 9 9h2c0-6.08-4.93-11-11-11m20-7H3c-1.1 0-2 .9-2 2v3h2V5h18v14h-7v2h7c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2"/></svg>',
            '        <span class="moonfin-sidebar-label">Cast</span>',
            '    </button>',
            '',
            '    <button class="moonfin-sidebar-item moonfin-sidebar-syncplay' + (!settings.showSyncPlayButton ? ' hidden' : '') + '" data-action="syncplay" title="SyncPlay">',
            '        <svg class="moonfin-sidebar-icon" viewBox="0 -960 960 960"><path d="M0-240v-63q0-43 44-70t116-27q13 0 25 .5t23 2.5q-14 21-21 44t-7 48v65H0Zm240 0v-65q0-32 17.5-58.5T307-410q32-20 76.5-30t96.5-10q53 0 97.5 10t76.5 30q32 20 49 46.5t17 58.5v65H240Zm540 0v-65q0-26-6.5-49T754-397q11-2 22.5-2.5t23.5-.5q72 0 116 26.5t44 70.5v63H780Zm-455-80h311q-10-20-55.5-35T480-370q-55 0-100.5 15T325-320ZM160-440q-33 0-56.5-23.5T80-520q0-34 23.5-57t56.5-23q34 0 57 23t23 57q0 33-23 56.5T160-440Zm640 0q-33 0-56.5-23.5T720-520q0-34 23.5-57t56.5-23q34 0 57 23t23 57q0 33-23 56.5T800-440Zm-320-40q-50 0-85-35t-35-85q0-51 35-85.5t85-34.5q51 0 85.5 34.5T600-600q0 50-34.5 85T480-480Zm0-80q17 0 28.5-11.5T520-600q0-17-11.5-28.5T480-640q-17 0-28.5 11.5T440-600q0 17 11.5 28.5T480-560Zm1 240Zm-1-280Z"/></svg>',
            '        <span class="moonfin-sidebar-label">SyncPlay</span>',
            '    </button>',
            '',
            '    <div class="moonfin-sidebar-separator"></div>',
            '',
            '    <button class="moonfin-sidebar-item moonfin-sidebar-libraries-toggle' + (!settings.showLibrariesInToolbar ? ' hidden' : '') + '" data-action="libraries-toggle" title="Libraries">',
            '        <svg class="moonfin-sidebar-icon" viewBox="0 0 24 24"><path d="M4 6H2v14c0 1.1.9 2 2 2h14v-2H4V6zm16-4H8c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm-8 12.5v-9l6 4.5-6 4.5z"/></svg>',
            '        <span class="moonfin-sidebar-label">Libraries</span>',
            '        <svg class="moonfin-sidebar-chevron" viewBox="0 0 24 24"><path d="M7.41 8.59L12 13.17l4.59-4.58L18 10l-6 6-6-6z"/></svg>',
            '    </button>',
            '    <div class="moonfin-sidebar-libraries">',
            '    </div>',
            '',
            '</div>',
            '',
            '<div class="moonfin-sidebar-footer">',
            '    <button class="moonfin-sidebar-item moonfin-sidebar-settings" data-action="settings" title="Settings">',
            '        <svg class="moonfin-sidebar-icon" viewBox="0 0 24 24"><path d="M19.14 12.94c.04-.3.06-.61.06-.94 0-.32-.02-.64-.07-.94l2.03-1.58c.18-.14.23-.41.12-.61l-1.92-3.32c-.12-.22-.37-.29-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54c-.04-.24-.24-.41-.48-.41h-3.84c-.24 0-.43.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96c-.22-.08-.47 0-.59.22L2.74 8.87c-.12.21-.08.47.12.61l2.03 1.58c-.05.3-.09.63-.09.94s.02.64.07.94l-2.03 1.58c-.18.14-.23.41-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61l-2.01-1.58zM12 15.6c-1.98 0-3.6-1.62-3.6-3.6s1.62-3.6 3.6-3.6 3.6 1.62 3.6 3.6-1.62 3.6-3.6 3.6z"/></svg>',
            '        <span class="moonfin-sidebar-label">Settings</span>',
            '    </button>',
            '    <div class="moonfin-sidebar-clock' + (!settings.showClock ? ' hidden' : '') + '">',
            '        <span>--:--</span>',
            '    </div>',
            '</div>'
        ].join('\n');

        document.body.insertBefore(this.container, document.body.firstChild);

        this.overlay = document.createElement('div');
        this.overlay.className = 'moonfin-sidebar-overlay';
        document.body.appendChild(this.overlay);

        this.mobileTrigger = document.createElement('button');
        this.mobileTrigger.className = 'moonfin-sidebar-mobile-trigger';
        this.mobileTrigger.innerHTML = '<svg viewBox="0 0 24 24"><path d="M3 18h18v-2H3v2zm0-5h18v-2H3v2zm0-7v2h18V6H3z"/></svg>';
        this.mobileTrigger.title = 'Menu';
        document.body.appendChild(this.mobileTrigger);

        document.body.classList.add('moonfin-sidebar-active');
    },

    async loadUserData() {
        this.currentUser = await API.getCurrentUser();
        if (this.currentUser) {
            this.updateUserAvatar();
        }

        this.libraries = await API.getUserViews();
        this.updateLibraries();
    },

    updateUserAvatar() {
        var avatarContainer = this.container.querySelector('.moonfin-sidebar-avatar');
        var usernameEl = this.container.querySelector('.moonfin-sidebar-username');
        if (!avatarContainer || !this.currentUser) return;

        var avatarUrl = API.getUserAvatarUrl(this.currentUser);
        if (avatarUrl) {
            avatarContainer.innerHTML = '<img src="' + avatarUrl + '" alt="' + (this.currentUser.Name || '') + '">';
        } else {
            var initial = (this.currentUser.Name && this.currentUser.Name[0]) || 'U';
            avatarContainer.innerHTML = '<span class="moonfin-sidebar-initial">' + initial + '</span>';
        }

        if (usernameEl) {
            usernameEl.textContent = this.currentUser.Name || 'User';
        }
    },

    updateLibraries() {
        var librariesList = this.container.querySelector('.moonfin-sidebar-libraries');
        if (!librariesList) return;

        var filteredLibraries = this.libraries.filter(function(lib) {
            var type = lib.CollectionType ? lib.CollectionType.toLowerCase() : '';
            return type !== 'playlists' && type !== 'boxsets';
        });

        librariesList.innerHTML = filteredLibraries.map(function(lib) {
            var collectionType = lib.CollectionType || '';
            return '<button class="moonfin-sidebar-library-item" data-action="library" data-library-id="' + lib.Id + '" data-collection-type="' + collectionType + '" title="' + lib.Name + '">' +
                lib.Name +
            '</button>';
        }).join('');
    },

    toggleLibraries() {
        this.librariesExpanded = !this.librariesExpanded;

        var toggleBtn = this.container.querySelector('.moonfin-sidebar-libraries-toggle');
        var librariesList = this.container.querySelector('.moonfin-sidebar-libraries');

        if (toggleBtn) {
            toggleBtn.classList.toggle('libraries-expanded', this.librariesExpanded);
        }
        if (librariesList) {
            librariesList.classList.toggle('expanded', this.librariesExpanded);
        }
    },

    toggleMobile() {
        var isExpanded = this.container.classList.contains('expanded');
        this.container.classList.toggle('expanded', !isExpanded);
        this.overlay.classList.toggle('visible', !isExpanded);

        if (isExpanded) {
            this.librariesExpanded = false;
            var toggleBtn = this.container.querySelector('.moonfin-sidebar-libraries-toggle');
            var librariesList = this.container.querySelector('.moonfin-sidebar-libraries');
            if (toggleBtn) toggleBtn.classList.remove('libraries-expanded');
            if (librariesList) librariesList.classList.remove('expanded');
        }
    },

    closeMobile() {
        this.container.classList.remove('expanded');
        this.overlay.classList.remove('visible');
    },

    setupEventListeners() {
        var self = this;

        this.container.addEventListener('click', function(e) {
            var btn = e.target.closest('.moonfin-sidebar-item, .moonfin-sidebar-library-item');
            if (!btn) return;

            var action = btn.dataset.action;
            if (action === 'libraries-toggle') {
                self.toggleLibraries();
                return;
            }

            if (self.isMobile()) {
                self.closeMobile();
            }

            self.handleNavigation(action, btn);
        });

        var userBtn = this.container.querySelector('.moonfin-sidebar-user-btn');
        if (userBtn) {
            userBtn.addEventListener('click', function() {
                if (self.isMobile()) {
                    self.closeMobile();
                }
                if (Genres.isVisible) Genres.close();
                if (Details.isVisible) Details.hide(true);
                API.navigateTo('/mypreferencesmenu');
            });
        }

        if (this.mobileTrigger) {
            this.mobileTrigger.addEventListener('click', function() {
                self.toggleMobile();
            });
        }

        if (this.overlay) {
            this.overlay.addEventListener('click', function() {
                self.closeMobile();
            });
        }

        window.addEventListener('moonfin-settings-changed', function(e) {
            self.applySettings(e.detail);
        });

        window.addEventListener('viewshow', function() {
            self.updateActiveState();
        });

        window.addEventListener('moonfin-jellyseerr-config', function(e) {
            self.updateJellyseerrButton(e.detail);
        });
    },

    updateJellyseerrButton(config) {
        var btn = this.container ? this.container.querySelector('.moonfin-sidebar-jellyseerr') : null;
        if (!btn) return;

        if (config && config.enabled && config.url) {
            btn.classList.remove('hidden');
            var label = btn.querySelector('.moonfin-sidebar-label');
            if (label) {
                label.textContent = config.displayName || 'Jellyseerr';
            }
            btn.title = config.displayName || 'Jellyseerr';
        } else {
            btn.classList.add('hidden');
        }
    },

    async handleNavigation(action, btn) {
        if (Details.isVisible) {
            Details.hide(true);
        }

        if (action !== 'jellyseerr' && action !== 'settings' && Jellyseerr.isOpen) {
            Jellyseerr.close();
            this.updateJellyseerrButtonState();
        }

        if (action !== 'genres' && Genres.isVisible) {
            Genres.close();
        }

        if (action !== 'library' && Library.isVisible) {
            Library.close();
        }

        switch (action) {
            case 'home':
                API.navigateTo('/home');
                break;
            case 'search':
                API.navigateTo('/search');
                break;
            case 'shuffle':
                await this.handleShuffle();
                break;
            case 'genres':
                if (Genres.isVisible) {
                    Genres.close();
                } else {
                    Genres.show();
                }
                break;
            case 'favorites':
                API.navigateTo('/home?tab=1');
                break;
            case 'settings':
                Settings.show();
                break;
            case 'cast':
                this.showCastMenu(btn);
                break;
            case 'syncplay':
                this.showSyncPlayMenu(btn);
                break;
            case 'jellyseerr':
                Jellyseerr.toggle();
                this.updateJellyseerrButtonState();
                break;
            case 'library':
                var libraryId = btn.dataset.libraryId;
                var collectionType = btn.dataset.collectionType;
                var libraryName = btn.getAttribute('title');
                if (libraryId) {
                    var type = (collectionType || '').toLowerCase();
                    if (type === 'movies' || type === 'tvshows' || type === '') {
                        Library.show(libraryId, libraryName, collectionType);
                    } else {
                        API.navigateTo(this.getLibraryUrl(libraryId, collectionType));
                    }
                }
                break;
        }
    },

    updateJellyseerrButtonState() {
        var btn = this.container ? this.container.querySelector('.moonfin-sidebar-jellyseerr') : null;
        if (btn) {
            btn.classList.toggle('active', Jellyseerr.isOpen);
        }
    },

    getLibraryUrl: function(libraryId, collectionType) {
        var type = (collectionType || '').toLowerCase();
        switch (type) {
            case 'movies':
                return '/movies?topParentId=' + libraryId + '&collectionType=' + collectionType;
            case 'tvshows':
                return '/tv?topParentId=' + libraryId + '&collectionType=' + collectionType;
            case 'music':
                return '/music?topParentId=' + libraryId + '&collectionType=' + collectionType;
            case 'livetv':
                return '/livetv?collectionType=' + collectionType;
            case 'homevideos':
                return '/homevideos?topParentId=' + libraryId;
            case 'books':
                return '/list?parentId=' + libraryId;
            default:
                return '/list?parentId=' + libraryId;
        }
    },

    showCastMenu() {
        var nativeCastBtn = document.querySelector('.headerCastButton, .castButton');
        if (nativeCastBtn) {
            nativeCastBtn.click();
        }
    },

    showSyncPlayMenu() {
        var nativeSyncBtn = document.querySelector('.headerSyncButton, .syncButton');
        if (nativeSyncBtn) {
            nativeSyncBtn.click();
        }
    },

    async handleShuffle() {
        var settings = Storage.getAll();
        var items = await API.getRandomItems({
            contentType: settings.shuffleContentType,
            limit: 1
        });

        if (items.length > 0) {
            var item = items[0];
            if (typeof Details !== 'undefined' && Storage.get('detailsPageEnabled')) {
                Details.showDetails(item.Id, item.Type);
            } else {
                API.navigateToItem(item.Id);
            }
        }
    },

    updateActiveState() {
        if (!this.container) return;

        var path = window.location.pathname + window.location.search;

        this.container.querySelectorAll('.moonfin-sidebar-item').forEach(function(btn) {
            btn.classList.remove('active');
        });

        if (path.indexOf('/home') !== -1) {
            var homeBtn = this.container.querySelector('.moonfin-sidebar-home');
            if (homeBtn) homeBtn.classList.add('active');
        } else if (path.indexOf('/search') !== -1) {
            var searchBtn = this.container.querySelector('.moonfin-sidebar-search');
            if (searchBtn) searchBtn.classList.add('active');
        }

        var urlParams = new URLSearchParams(window.location.search);
        var parentId = urlParams.get('parentId');
        if (parentId) {
            var libraryBtn = this.container.querySelector('[data-library-id="' + parentId + '"]');
            if (libraryBtn) {
                libraryBtn.classList.add('active');
            }
        }
    },

    startClock() {
        var self = this;
        var updateClock = function() {
            var clockElement = self.container ? self.container.querySelector('.moonfin-sidebar-clock span') : null;
            if (!clockElement) return;

            var now = new Date();
            var settings = Storage.getAll();

            var hours = now.getHours();
            var minutes = now.getMinutes();
            var suffix = '';

            if (!settings.use24HourClock) {
                suffix = hours >= 12 ? ' PM' : ' AM';
                hours = hours % 12 || 12;
            }

            clockElement.textContent = hours + ':' + minutes.toString().padStart(2, '0') + suffix;
        };

        updateClock();
        this.clockInterval = setInterval(updateClock, 1000);
    },

    applySettings(settings) {
        if (!this.container) return;

        var shuffleBtn = this.container.querySelector('.moonfin-sidebar-shuffle');
        if (shuffleBtn) shuffleBtn.classList.toggle('hidden', !settings.showShuffleButton);

        var genresBtn = this.container.querySelector('.moonfin-sidebar-genres');
        if (genresBtn) genresBtn.classList.toggle('hidden', !settings.showGenresButton);

        var favoritesBtn = this.container.querySelector('.moonfin-sidebar-favorites');
        if (favoritesBtn) favoritesBtn.classList.toggle('hidden', !settings.showFavoritesButton);

        var librariesToggle = this.container.querySelector('.moonfin-sidebar-libraries-toggle');
        if (librariesToggle) librariesToggle.classList.toggle('hidden', !settings.showLibrariesInToolbar);

        var clock = this.container.querySelector('.moonfin-sidebar-clock');
        if (clock) clock.classList.toggle('hidden', !settings.showClock);
    },

    destroy() {
        if (this.clockInterval) {
            clearInterval(this.clockInterval);
            this.clockInterval = null;
        }
        if (this.container) {
            this.container.remove();
            this.container = null;
        }
        if (this.overlay) {
            this.overlay.remove();
            this.overlay = null;
        }
        if (this.mobileTrigger) {
            this.mobileTrigger.remove();
            this.mobileTrigger = null;
        }
        document.body.classList.remove('moonfin-sidebar-active');
        this.librariesExpanded = false;
        this.initialized = false;
    }
};
