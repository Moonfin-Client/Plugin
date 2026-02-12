const Jellyseerr = {
    container: null,
    iframe: null,
    isOpen: false,
    config: null,
    ssoStatus: null,

    getProxyUrl() {
        var serverUrl = window.ApiClient?.serverAddress?.() || '';
        var token = window.ApiClient?.accessToken?.();
        if (!serverUrl || !token) return null;
        return serverUrl + '/Moonfin/Jellyseerr/Web/?api_key=' + encodeURIComponent(token);
    },

    async init() {
        await this.fetchConfig();
        
        if (this.config?.enabled && this.config?.url) {
            console.log('[Moonfin] Jellyseerr enabled:', this.config.url);
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
        var ssoUser = this.ssoStatus?.displayName || '';
        var iframeSrc = this.getProxyUrl() || this.config.url;
        
        this.container.innerHTML = 
            '<div class="moonfin-jellyseerr-header">' +
                '<div class="moonfin-jellyseerr-title">' +
                    '<svg class="moonfin-jellyseerr-icon" viewBox="0 0 24 24" width="24" height="24">' +
                        '<path fill="currentColor" d="M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 15l-5-5 1.41-1.41L10 14.17l7.59-7.59L19 8l-9 9z"/>' +
                    '</svg>' +
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
                    '<button class="moonfin-jellyseerr-btn moonfin-jellyseerr-signout" title="Sign out of Jellyseerr">' +
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
            this.iframe.src = this.getProxyUrl() || this.config.url;
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
