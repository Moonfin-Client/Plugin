const Jellyseerr = {
    container: null,
    iframe: null,
    isOpen: false,
    config: null,
    ssoStatus: null,

    icons: {
        jellyseerr: '<svg class="moonfin-jellyseerr-icon" viewBox="0 0 96 96" fill="currentColor" xmlns="http://www.w3.org/2000/svg"><path fill-opacity="0.13" d="M96.1,48c0,26.31 -21.18,47.71 -47.48,48C22.31,96.28 0.68,75.33 0.11,49.03C-0.45,22.73 20.26,0.87 46.56,0.03c26.3,-0.85 48.37,19.63 49.5,45.92"/><path fill-opacity="0.4" d="M42.87,45.59h-2.49c-3.33,12.42 -4.89,30.36 -4.17,43.88c0.79,14.88 4.85,29.2 6.2,29.2s-0.71,-9.11 0.21,-29.17c0.62,-13.38 4.41,-25.95 4.7,-43.91h-4.46z"/><path fill-opacity="0.4" d="M64.09,45.86h2.49c3.33,12.42 4.89,30.36 4.17,43.88c-0.79,14.88 -4.85,29.2 -6.2,29.2s0.71,-9.11 -0.21,-29.17c-0.62,-13.38 -4.41,-25.95 -4.7,-43.91h4.46z"/><path fill-opacity="0.53" d="M38.05,70.69l-5.06,-1.13s-1.17,7.43 -1.61,11.15c-0.71,6.02 -1.57,14.34 -1.23,20.71c0.37,7.01 2.29,13.76 2.92,13.76s-0.34,-4.29 0.1,-13.75c0.29,-6.3 1.33,-13.87 2.58,-20.72c0.62,-3.38 2.42,-10.02 2.42,-10.02z"/><path fill-opacity="0.53" d="M59.41,70.16h1.55c2.08,7.76 2.47,18.96 2.02,27.4c-0.49,9.29 -3.03,18.23 -3.87,18.23s0.45,-5.69 -0.13,-18.21c-0.39,-8.35 -2.16,-16.2 -2.35,-27.41h2.78z"/><path fill-opacity="0.67" d="M35.18,39.95l-5.67,-2.02s-2.08,13.26 -2.87,19.92c-1.26,10.75 -3.75,25.61 -3.14,36.99c0.67,12.53 4.09,24.58 5.22,24.58s-0.6,-7.67 0.18,-24.56c0.52,-11.26 3.97,-21.94 5.14,-37.01c0.47,-5.99 1.37,-17.9 1.37,-17.9z"/><path fill-opacity="0.67" d="M53.91,45.86l-5.11,0.87s0.68,9.93 0.68,15.58c0,9.16 0.36,18.42 0.33,28.03c-0.03,11.05 1.81,29.55 2.77,29.55s4.06,-23.82 4.72,-38.06c0.44,-9.5 -0.97,-17.84 -1.22,-23.52c-0.22,-5.06 -0.93,-11.88 -0.93,-11.88z"/><path d="M82.09,48.88c0,12.9 -2.19,13.68 -5.78,19.15c-2.58,3.92 2.64,6.96 0.55,8.04c-2.5,1.29 -1.71,-1.05 -6.67,-2.38c-2.15,-0.57 -6.84,0.06 -8.74,0.43c-1.88,0.36 -7.61,-2.83 -9.14,-3.24c-2.27,-0.61 -7.84,2.35 -11.23,2.35s-6.94,-2.96 -11.46,-1.75c-5.36,1.44 -11.83,4.94 -12.81,3.79c-1.88,-2.19 4.1,-3.86 1.88,-7.76c-1.4,-2.47 -6.27,-8.98 -6.41,-15.56c-0.45,-21.16 17.07,-39.03 35.84,-39.03s33.95,16.28 33.95,34.49"/><path fill-rule="evenodd" d="M46.95,19.63c-10.25,0 -24.58,10.61 -24.58,20.86c0,1.14 -0.92,2.06 -2.06,2.06s-2.06,-0.92 -2.06,-2.06c0,-12.52 16.17,-24.98 28.7,-24.98c1.14,0 2.06,0.92 2.06,2.06s-0.92,2.06 -2.06,2.06z"/><path fill-opacity="0.87" d="M62.12,58.41c-1.09,1.78 -2.57,3.21 -4.32,4.19c-0.75,0.41 -1.54,0.74 -2.36,0.98c-2.45,1.1 -5.2,1.69 -7.99,1.75c-9.53,0.17 -17.44,-5.92 -17.75,-13.65c-0.15,-3.79 2.11,-7.72 3.86,-10.75c1.48,-2.56 4.03,-6.97 7.39,-8.73c6.85,-3.6 16.08,0.21 20.7,8.55c1.34,2.42 2.19,5.07 2.48,7.71c0.21,0.86 0.33,1.74 0.34,2.62c0.03,2.29 -0.63,4.55 -1.91,6.58c-0.13,0.26 -0.27,0.51 -0.42,0.75z"/><path d="M47.07,39.46c5.94,0 10.75,4.81 10.75,10.75s-4.81,10.75 -10.75,10.75s-10.75,-4.81 -10.75,-10.75c0,-1.1 0.16,-2.16 0.47,-3.17c0.84,1.87 2.72,3.17 4.9,3.17c2.97,0 5.37,-2.41 5.37,-5.37c0,-2.18 -1.3,-4.06 -3.17,-4.9c1,-0.31 2.06,-0.47 3.17,-0.47z"/></svg>',
        seerr: '<svg class="moonfin-jellyseerr-icon" viewBox="0 0 96 96" fill="currentColor" xmlns="http://www.w3.org/2000/svg"><path fill-rule="evenodd" clip-rule="evenodd" d="M48 96C74.5097 96 96 74.5097 96 48C96 21.4903 74.5097 0 48 0C21.4903 0 0 21.4903 0 48C0 74.5097 21.4903 96 48 96Z" fill-opacity="0.2"/><circle cx="52" cy="52" r="28" fill-opacity="0.3"/><path fill-rule="evenodd" clip-rule="evenodd" d="M80.0001 52C80.0001 67.464 67.4641 80 52.0001 80C36.5361 80 24.0001 67.464 24.0001 52C24.0001 49.1303 24.4318 46.3615 25.2338 43.7548C27.4288 48.6165 32.3194 52 38.0001 52C45.7321 52 52.0001 45.732 52.0001 38C52.0001 32.3192 48.6166 27.4287 43.755 25.2337C46.3616 24.4317 49.1304 24 52.0001 24C67.4641 24 80.0001 36.536 80.0001 52Z"/><path fill-rule="evenodd" clip-rule="evenodd" d="M48 12C28.1177 12 12 28.1177 12 48C12 50.2091 10.2091 52 8 52C5.79086 52 4 50.2091 4 48C4 23.6995 23.6995 4 48 4C50.2091 4 52 5.79086 52 8C52 10.2091 50.2091 12 48 12Z" fill-opacity="0.5"/><path opacity="0.25" fill-rule="evenodd" clip-rule="evenodd" d="M80.0002 52C80.0002 67.464 67.4642 80 52.0002 80C36.864 80 24.5329 67.9897 24.017 52.9791C24.0057 53.318 24 53.6583 24 54C24 70.5685 37.4315 84 54 84C70.5685 84 84 70.5685 84 54C84 37.4315 70.5685 24 54 24C53.6597 24 53.3207 24.0057 52.9831 24.0169C67.9919 24.5347 80.0002 36.865 80.0002 52Z"/></svg>'
    },

    getIcon(variant) {
        return this.icons[variant] || this.icons.jellyseerr;
    },

    getProxyUrl() {
        var serverUrl = window.ApiClient?.serverAddress?.() || '';
        var token = window.ApiClient?.accessToken?.();
        if (!serverUrl || !token) return null;
        return serverUrl + '/Moonfin/Jellyseerr/Web/?api_key=' + encodeURIComponent(token);
    },

    getIframeUrl() {
        if (this.config?.directUrl) {
            return this.config.directUrl;
        }
        return this.getProxyUrl() || this.config?.url;
    },

    async init() {
        await this.fetchConfig();
        
        if (this.config?.enabled && this.config?.url) {
            console.log('[Moonfin] ' + (this.config.displayName || 'Jellyseerr') + ' enabled:', this.config.url, '(variant:', this.config.variant + ')');
            await this.checkSsoStatus();
            window.dispatchEvent(new CustomEvent('moonfin-jellyseerr-config', { 
                detail: this.config 
            }));
        }
    },

    async fetchConfig() {
        try {
            const serverUrl = window.ApiClient?.serverAddress?.() || '';
            const token = window.ApiClient?.accessToken?.();
            
            if (!serverUrl || !token) {
                console.log('[Moonfin] Cannot fetch Jellyseerr config - not authenticated');
                return;
            }

            const deviceInfo = Device.getInfo();
            const params = new URLSearchParams({
                deviceType: deviceInfo.type,
                isMobile: deviceInfo.isMobile,
                hasTouch: deviceInfo.hasTouch
            });

            var response = await fetch(serverUrl + '/Moonfin/Jellyseerr/Config?' + params, {
                method: 'GET',
                headers: {
                    'Content-Type': 'application/json',
                    'Authorization': 'MediaBrowser Token="' + token + '"'
                }
            });

            if (response.ok) {
                this.config = API.toCamelCase(await response.json());
            }
        } catch (e) {
            console.error('[Moonfin] Failed to fetch Jellyseerr config:', e);
        }
    },

    async checkSsoStatus() {
        try {
            var serverUrl = window.ApiClient?.serverAddress?.() || '';
            var token = window.ApiClient?.accessToken?.();
            
            if (!serverUrl || !token) return;

            var response = await fetch(serverUrl + '/Moonfin/Jellyseerr/Status', {
                method: 'GET',
                headers: {
                    'Content-Type': 'application/json',
                    'Authorization': 'MediaBrowser Token="' + token + '"'
                }
            });

            if (response.ok) {
                this.ssoStatus = API.toCamelCase(await response.json());
                console.log('[Moonfin] Jellyseerr SSO status:', this.ssoStatus.authenticated ? 'authenticated' : 'not authenticated');
            }
        } catch (e) {
            console.error('[Moonfin] Failed to check Jellyseerr SSO status:', e);
        }
    },

    async ssoLogin(username, password, authType) {
        try {
            var serverUrl = window.ApiClient?.serverAddress?.() || '';
            var token = window.ApiClient?.accessToken?.();
            
            if (!serverUrl || !token) {
                return { success: false, error: 'Not authenticated with Jellyfin' };
            }

            var response = await fetch(serverUrl + '/Moonfin/Jellyseerr/Login', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'Authorization': 'MediaBrowser Token="' + token + '"'
                },
                body: JSON.stringify({ username: username, password: password, authType: authType || 'jellyfin' })
            });

            var result = API.toCamelCase(await response.json());
            
            if (response.ok && result.success) {
                this.ssoStatus = {
                    enabled: true,
                    authenticated: true,
                    url: this.config?.url,
                    jellyseerrUserId: result.jellyseerrUserId,
                    displayName: result.displayName,
                    avatar: result.avatar,
                    permissions: result.permissions
                };
                console.log('[Moonfin] Jellyseerr SSO login successful:', result.displayName);
                return { success: true };
            }
            
            return { success: false, error: result.error || 'Authentication failed' };
        } catch (e) {
            console.error('[Moonfin] Jellyseerr SSO login error:', e);
            return { success: false, error: 'Connection error' };
        }
    },

    async ssoLogout() {
        try {
            var serverUrl = window.ApiClient?.serverAddress?.() || '';
            var token = window.ApiClient?.accessToken?.();
            
            if (!serverUrl || !token) return;

            await fetch(serverUrl + '/Moonfin/Jellyseerr/Logout', {
                method: 'DELETE',
                headers: {
                    'Authorization': 'MediaBrowser Token="' + token + '"'
                }
            });

            this.ssoStatus = { enabled: true, authenticated: false, url: this.config?.url };
            console.log('[Moonfin] Jellyseerr SSO logged out');
        } catch (e) {
            console.error('[Moonfin] Jellyseerr SSO logout error:', e);
        }
    },

    async ssoApiCall(method, path, body) {
        var serverUrl = window.ApiClient?.serverAddress?.() || '';
        var token = window.ApiClient?.accessToken?.();
        
        if (!serverUrl || !token) {
            throw new Error('Not authenticated with Jellyfin');
        }

        var options = {
            method: method,
            headers: {
                'Content-Type': 'application/json',
                'Authorization': 'MediaBrowser Token="' + token + '"'
            }
        };

        if (body && (method === 'POST' || method === 'PUT')) {
            options.body = JSON.stringify(body);
        }

        var response = await fetch(serverUrl + '/Moonfin/Jellyseerr/Api/' + path, options);
        
        if (response.status === 401) {
            // Session expired - clear status
            this.ssoStatus = { enabled: true, authenticated: false, url: this.config?.url };
            throw new Error('SESSION_EXPIRED');
        }

        return response;
    },

    open() {
        if (!this.config?.enabled || !this.config?.url) {
            console.warn('[Moonfin] Jellyseerr not configured');
            return;
        }

        if (this.isOpen) return;

        // Check SSO status - direct user to Settings if not authenticated
        if (!this.ssoStatus?.authenticated) {
            this.showSignInPrompt();
            return;
        }

        this.createContainer();
        this.isOpen = true;

        history.pushState({ moonfinJellyseerr: true }, '');
        document.body.classList.add('moonfin-jellyseerr-open');

        requestAnimationFrame(function() {
            if (Jellyseerr.container) {
                Jellyseerr.container.classList.add('open');
            }
        });
    },

    showSignInPrompt() {
        var existing = document.querySelector('.moonfin-jellyseerr-signin-prompt');
        if (existing) existing.remove();

        var prompt = document.createElement('div');
        prompt.className = 'moonfin-jellyseerr-signin-prompt';
        prompt.style.cssText = 'position:fixed; top:50%; left:50%; transform:translate(-50%,-50%); background:#1e1e2e; border:1px solid #555; border-radius:8px; padding:1.5em 2em; z-index:100001; text-align:center; color:#fff; box-shadow:0 4px 24px rgba(0,0,0,0.5);';
        prompt.innerHTML =
            '<p style="margin:0 0 1em 0; font-size:1em;">Sign in to Jellyseerr in <strong>Moonfin Settings</strong> first.</p>' +
            '<div style="display:flex; gap:0.5em; justify-content:center;">' +
                '<button class="moonfin-prompt-settings-btn" style="padding:0.5em 1.5em; border:none; border-radius:4px; background:#6366f1; color:#fff; cursor:pointer; font-size:0.9em;">Open Settings</button>' +
                '<button class="moonfin-prompt-close-btn" style="padding:0.5em 1.5em; border:none; border-radius:4px; background:#555; color:#fff; cursor:pointer; font-size:0.9em;">Close</button>' +
            '</div>';

        document.body.appendChild(prompt);

        prompt.querySelector('.moonfin-prompt-close-btn').addEventListener('click', function() {
            prompt.remove();
        });

        prompt.querySelector('.moonfin-prompt-settings-btn').addEventListener('click', function() {
            prompt.remove();
            Settings.show();
        });

        setTimeout(function() {
            if (prompt.parentNode) prompt.remove();
        }, 8000);
    },

    close(skipHistoryBack) {
        if (!this.isOpen) return;

        this.isOpen = false;
        this.container.classList.remove('open');
        document.body.classList.remove('moonfin-jellyseerr-open');

        setTimeout(() => {
            if (this.container) {
                this.container.remove();
                this.container = null;
                this.iframe = null;
            }
        }, 300);

        if (!skipHistoryBack) {
            try { history.back(); } catch(e) {}
        }
    },

    toggle() {
        if (this.isOpen) {
            this.close();
        } else {
            this.open();
        }
    },

    createContainer() {
        var existing = document.querySelector('.moonfin-jellyseerr-container');
        if (existing) {
            existing.remove();
        }

        this.container = document.createElement('div');
        this.container.className = 'moonfin-jellyseerr-container';
        
        var displayName = this.config?.displayName || 'Jellyseerr';
        var variant = this.config?.variant || 'jellyseerr';
        var ssoUser = this.ssoStatus?.displayName || '';
        var iframeSrc = this.getIframeUrl();
        var iconSvg = this.getIcon(variant);
        
        this.container.innerHTML = 
            '<div class="moonfin-jellyseerr-header">' +
                '<div class="moonfin-jellyseerr-title">' +
                    iconSvg +
                    '<span>' + displayName + '</span>' +
                    (ssoUser ? '<span class="moonfin-jellyseerr-sso-user"> &mdash; ' + ssoUser + '</span>' : '') +
                '</div>' +
                '<div class="moonfin-jellyseerr-actions">' +
                    '<button class="moonfin-jellyseerr-btn moonfin-jellyseerr-refresh" title="Refresh">' +
                        '<svg viewBox="0 0 24 24" width="20" height="20">' +
                            '<path fill="currentColor" d="M17.65 6.35C16.2 4.9 14.21 4 12 4c-4.42 0-7.99 3.58-7.99 8s3.57 8 7.99 8c3.73 0 6.84-2.55 7.73-6h-2.08c-.82 2.33-3.04 4-5.65 4-3.31 0-6-2.69-6-6s2.69-6 6-6c1.66 0 3.14.69 4.22 1.78L13 11h7V4l-2.35 2.35z"/>' +
                        '</svg>' +
                    '</button>' +
                    '<button class="moonfin-jellyseerr-btn moonfin-jellyseerr-external" title="Open in new tab">' +
                        '<svg viewBox="0 0 24 24" width="20" height="20">' +
                            '<path fill="currentColor" d="M19 19H5V5h7V3H5c-1.11 0-2 .9-2 2v14c0 1.1.89 2 2 2h14c1.1 0 2-.9 2-2v-7h-2v7zM14 3v2h3.59l-9.83 9.83 1.41 1.41L19 6.41V10h2V3h-7z"/>' +
                        '</svg>' +
                    '</button>' +
                    '<button class="moonfin-jellyseerr-btn moonfin-jellyseerr-signout" title="Sign out of ' + displayName + '">' +
                        '<svg viewBox="0 0 24 24" width="20" height="20">' +
                            '<path fill="currentColor" d="M17 7l-1.41 1.41L18.17 11H8v2h10.17l-2.58 2.58L17 17l5-5zM4 5h8V3H4c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h8v-2H4V5z"/>' +
                        '</svg>' +
                    '</button>' +
                    '<button class="moonfin-jellyseerr-btn moonfin-jellyseerr-close" title="Close">' +
                        '<svg viewBox="0 0 24 24" width="20" height="20">' +
                            '<path fill="currentColor" d="M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z"/>' +
                        '</svg>' +
                    '</button>' +
                '</div>' +
            '</div>' +
            '<div class="moonfin-jellyseerr-loading">' +
                '<div class="moonfin-jellyseerr-spinner"></div>' +
                '<span>Loading ' + displayName + '...</span>' +
            '</div>' +
            '<iframe ' +
                'class="moonfin-jellyseerr-iframe" ' +
                'src="' + iframeSrc + '" ' +
                'allow="fullscreen" ' +
                'sandbox="allow-same-origin allow-scripts allow-popups allow-forms allow-popups-to-escape-sandbox"' +
            '></iframe>';

        document.body.appendChild(this.container);
        
        this.iframe = this.container.querySelector('.moonfin-jellyseerr-iframe');
        
        this.setupEventListeners();
    },

    setupEventListeners() {
        var self = this;

        this.container.querySelector('.moonfin-jellyseerr-close')?.addEventListener('click', function() {
            self.close();
        });

        this.container.querySelector('.moonfin-jellyseerr-refresh')?.addEventListener('click', function() {
            self.refresh();
        });

        this.container.querySelector('.moonfin-jellyseerr-external')?.addEventListener('click', function() {
            window.open(self.config.url, '_blank');
        });

        this.container.querySelector('.moonfin-jellyseerr-signout')?.addEventListener('click', function() {
            if (confirm('Sign out of Jellyseerr? You will need to sign in again to use it.')) {
                self.close();
                self.ssoLogout();
            }
        });

        this.iframe?.addEventListener('load', function() {
            self.container.classList.add('loaded');
        });

        this.iframe?.addEventListener('error', function() {
            self.showError('Failed to load. The site may block embedding.');
        });

        this._escHandler = function(e) {
            if (e.key === 'Escape' && self.isOpen) {
                self.close();
            }
        };
        document.addEventListener('keydown', this._escHandler);
    },

    refresh() {
        if (this.iframe) {
            this.container.classList.remove('loaded');
            this.iframe.src = this.getIframeUrl();
        }
    },

    showError(message) {
        const loading = this.container?.querySelector('.moonfin-jellyseerr-loading');
        if (loading) {
            loading.innerHTML = `
                <svg viewBox="0 0 24 24" width="48" height="48" style="color: #f44336;">
                    <path fill="currentColor" d="M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-2h2v2zm0-4h-2V7h2v6z"/>
                </svg>
                <span style="color: #f44336;">${message}</span>
                <button class="moonfin-jellyseerr-btn" onclick="window.open('${this.config.url}', '_blank')">
                    Open in New Tab
                </button>
            `;
            loading.style.display = 'flex';
        }
    },

    destroy() {
        this.close();
        if (this._escHandler) {
            document.removeEventListener('keydown', this._escHandler);
        }
    }
};
