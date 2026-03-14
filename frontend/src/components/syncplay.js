const SyncPlay = {
    container: null,
    isOpen: false,
    initialized: false,
    _group: null,
    _groups: [],
    _refreshInterval: null,
    _wsHandler: null,

    init() {
        if (this.initialized) return;
        this._setupWebSocketListener();
        this.initialized = true;
        console.log('[Moonfin] SyncPlay initialized');
    },

    _request(method, path, body) {
        var api = API.getApiClient();
        if (!api) return Promise.reject(new Error('No API client'));
        var opts = {
            type: method,
            url: api.getUrl('SyncPlay/' + path)
        };
        if (body !== undefined) {
            opts.data = JSON.stringify(body);
            opts.contentType = 'application/json';
        }
        if (method === 'GET') {
            opts.dataType = 'json';
        }
        return api.ajax(opts);
    },

    async listGroups() {
        try {
            var result = await this._request('GET', 'List');
            return Array.isArray(result) ? result : [];
        } catch (e) {
            console.error('[Moonfin] SyncPlay: Failed to list groups', e);
            return [];
        }
    },

    async createGroup(name) {
        try {
            return await this._request('POST', 'New', { GroupName: name });
        } catch (e) {
            console.error('[Moonfin] SyncPlay: Failed to create group', e);
            return null;
        }
    },

    async joinGroup(groupId) {
        try {
            await this._request('POST', 'Join', { GroupId: groupId });
            return true;
        } catch (e) {
            console.error('[Moonfin] SyncPlay: Failed to join group', e);
            return false;
        }
    },

    async leaveGroup() {
        try {
            await this._request('POST', 'Leave');
            this._group = null;
            return true;
        } catch (e) {
            console.error('[Moonfin] SyncPlay: Failed to leave group', e);
            return false;
        }
    },

    async getGroup(groupId) {
        try {
            return await this._request('GET', groupId);
        } catch (e) {
            return null;
        }
    },

    _setupWebSocketListener() {
        var self = this;
        var attempts = 0;
        var tryHook = function() {
            var api = API.getApiClient();
            if (api) {
                self._hookWebSocket(api);
            } else if (attempts < 50) {
                attempts++;
                setTimeout(tryHook, 200);
            }
        };
        tryHook();
    },

    _hookWebSocket(api) {
        var self = this;
        if (self._wsHandler) return;

        self._wsHandler = function(e, msgType, data) {
            if (msgType === 'SyncPlayGroupUpdate') {
                self._handleGroupUpdate(data);
            }
        };

        if (window.Events && typeof window.Events.on === 'function') {
            window.Events.on(api, 'message', self._wsHandler);
        } else {
            var checkEvents = function(retries) {
                if (window.Events && typeof window.Events.on === 'function') {
                    window.Events.on(api, 'message', self._wsHandler);
                } else if (retries > 0) {
                    setTimeout(function() { checkEvents(retries - 1); }, 500);
                }
            };
            checkEvents(20);
        }
    },

    _handleGroupUpdate(data) {
        if (!data) return;
        switch (data.Type) {
            case 'GroupJoined':
                this._group = data.Data;
                console.log('[Moonfin] SyncPlay: Joined group', this._group.GroupName);
                this._updateUI();
                break;
            case 'GroupLeft':
                console.log('[Moonfin] SyncPlay: Left group');
                this._group = null;
                this._updateUI();
                break;
            case 'UserJoined':
            case 'UserLeft':
                this._refreshCurrentGroup();
                break;
            case 'StateUpdate':
                if (this._group && data.Data) {
                    this._group.State = data.Data.State;
                    this._updateUI();
                }
                break;
            case 'PlayQueue':
                this._updateUI();
                break;
            case 'NotInGroup':
            case 'GroupDoesNotExist':
                this._group = null;
                this._updateUI();
                break;
            case 'LibraryAccessDenied':
                console.warn('[Moonfin] SyncPlay: Library access denied');
                break;
        }
    },

    async _refreshCurrentGroup() {
        if (!this._group || !this._group.GroupId) return;
        var updated = await this.getGroup(this._group.GroupId);
        if (updated) {
            this._group = updated;
            this._updateUI();
        }
    },

    toggle() {
        if (this.isOpen) {
            this.close();
        } else {
            this.show();
        }
    },

    async show() {
        if (this.isOpen) return;
        if (Device.isTV()) return;
        this.isOpen = true;

        this._groups = await this.listGroups();
        this._createPanel();

        var self = this;
        requestAnimationFrame(function() {
            requestAnimationFrame(function() {
                if (self.container) {
                    self.container.classList.add('open');
                }
            });
        });

        history.pushState({ moonfinSyncPlay: true }, '');
        if (typeof Plugin !== 'undefined') Plugin._overlayHistoryDepth++;

        this._refreshInterval = setInterval(function() {
            if (self.isOpen && !self._group) {
                self._refreshGroupList();
            }
        }, 5000);
    },

    close(skipHistoryBack) {
        if (!this.isOpen) return;
        this.isOpen = false;

        if (this._refreshInterval) {
            clearInterval(this._refreshInterval);
            this._refreshInterval = null;
        }

        if (this._onKeyDown) {
            document.removeEventListener('keydown', this._onKeyDown);
            this._onKeyDown = null;
        }

        var self = this;
        if (this.container) {
            this.container.classList.remove('open');
            setTimeout(function() {
                if (self.container) {
                    self.container.remove();
                    self.container = null;
                }
            }, 300);
        }

        if (!skipHistoryBack) {
            try { history.back(); } catch (e) {}
        }
    },

    _createPanel() {
        if (this.container) {
            this.container.remove();
        }

        this.container = document.createElement('div');
        this.container.className = 'moonfin-syncplay-overlay';

        this.container.innerHTML = [
            '<div class="moonfin-syncplay-panel">',
            '    <div class="moonfin-syncplay-header">',
            '        <h2 class="moonfin-syncplay-title">SyncPlay</h2>',
            '        <button class="moonfin-syncplay-close" title="Close">&times;</button>',
            '    </div>',
            '    <div class="moonfin-syncplay-content">',
            this._group ? this._renderGroupView() : this._renderLobbyView(),
            '    </div>',
            '</div>'
        ].join('\n');

        document.body.appendChild(this.container);

        var self = this;
        this.container.querySelector('.moonfin-syncplay-close').addEventListener('click', function() {
            self.close();
        });
        this.container.addEventListener('click', function(e) {
            if (e.target === self.container) {
                self.close();
            }
        });
        this._onKeyDown = function(e) {
            if (e.key === 'Escape' && self.isOpen) {
                self.close();
            }
        };
        document.addEventListener('keydown', this._onKeyDown);

        this._bindContentEvents();
    },

    _renderGroupCardHtml(g) {
        var participants = g.Participants ? g.Participants.length : 0;
        var stateLabel = g.State || 'Idle';
        return '<button class="moonfin-syncplay-group-card" data-group-id="' + g.GroupId + '">' +
            '    <div class="moonfin-syncplay-group-info">' +
            '        <span class="moonfin-syncplay-group-name">' + this._escapeHtml(g.GroupName) + '</span>' +
            '        <span class="moonfin-syncplay-group-meta">' + participants + ' member' + (participants !== 1 ? 's' : '') + ' &middot; ' + stateLabel + '</span>' +
            '    </div>' +
            '    <span class="moonfin-syncplay-join-label">Join</span>' +
            '</button>';
    },

    _renderLobbyView() {
        var lines = [
            '<div class="moonfin-syncplay-lobby">',
            '    <div class="moonfin-syncplay-create">',
            '        <input type="text" class="moonfin-syncplay-input" placeholder="Group name..." maxlength="64">',
            '        <button class="moonfin-syncplay-btn moonfin-syncplay-create-btn">Create Group</button>',
            '    </div>',
            '    <div class="moonfin-syncplay-divider"><span>or join an existing group</span></div>',
            '    <div class="moonfin-syncplay-groups">'
        ];

        if (this._groups.length === 0) {
            lines.push('        <div class="moonfin-syncplay-empty">No active groups found</div>');
        } else {
            for (var i = 0; i < this._groups.length; i++) {
                lines.push(this._renderGroupCardHtml(this._groups[i]));
            }
        }

        lines.push('    </div>');
        lines.push('</div>');
        return lines.join('\n');
    },

    _renderGroupView() {
        var g = this._group;
        var participants = g.Participants || [];
        var stateLabel = g.State || 'Idle';

        var lines = [
            '<div class="moonfin-syncplay-group-view">',
            '    <div class="moonfin-syncplay-group-header">',
            '        <h3 class="moonfin-syncplay-group-title">' + this._escapeHtml(g.GroupName) + '</h3>',
            '        <span class="moonfin-syncplay-state moonfin-syncplay-state-' + (g.State || 'Idle').toLowerCase() + '">' + stateLabel + '</span>',
            '    </div>',
            '    <div class="moonfin-syncplay-members">',
            '        <h4 class="moonfin-syncplay-members-title">Members (' + participants.length + ')</h4>',
            '        <ul class="moonfin-syncplay-members-list">'
        ];

        for (var i = 0; i < participants.length; i++) {
            lines.push('            <li class="moonfin-syncplay-member">' + this._escapeHtml(participants[i]) + '</li>');
        }

        lines.push('        </ul>');
        lines.push('    </div>');
        lines.push('    <div class="moonfin-syncplay-controls">');
        lines.push('        <button class="moonfin-syncplay-btn moonfin-syncplay-leave-btn">Leave Group</button>');
        lines.push('    </div>');
        lines.push('</div>');
        return lines.join('\n');
    },

    _bindContentEvents() {
        var self = this;

        var createBtn = this.container.querySelector('.moonfin-syncplay-create-btn');
        var input = this.container.querySelector('.moonfin-syncplay-input');
        if (createBtn && input) {
            createBtn.addEventListener('click', function() {
                var name = input.value.trim();
                if (name) self._handleCreateGroup(name);
            });
            input.addEventListener('keydown', function(e) {
                if (e.key === 'Enter') {
                    var name = input.value.trim();
                    if (name) self._handleCreateGroup(name);
                }
            });
        }

        this.container.querySelectorAll('.moonfin-syncplay-group-card').forEach(function(card) {
            card.addEventListener('click', function() {
                var groupId = card.dataset.groupId;
                if (groupId) self._handleJoinGroup(groupId);
            });
        });

        var leaveBtn = this.container.querySelector('.moonfin-syncplay-leave-btn');
        if (leaveBtn) {
            leaveBtn.addEventListener('click', function() {
                self._handleLeaveGroup();
            });
        }
    },

    async _handleCreateGroup(name) {
        var createBtn = this.container ? this.container.querySelector('.moonfin-syncplay-create-btn') : null;
        if (createBtn) createBtn.disabled = true;

        var result = await this.createGroup(name);
        if (result) {
            this._group = result;
            this._updateUI();
        } else {
            if (createBtn) createBtn.disabled = false;
        }
    },

    async _handleJoinGroup(groupId) {
        var card = this.container ? this.container.querySelector('[data-group-id="' + groupId + '"]') : null;
        if (card) card.classList.add('joining');

        var success = await this.joinGroup(groupId);
        if (success) {
            var groupInfo = await this.getGroup(groupId);
            if (groupInfo) {
                this._group = groupInfo;
                this._updateUI();
            }
        } else {
            if (card) card.classList.remove('joining');
        }
    },

    async _handleLeaveGroup() {
        var leaveBtn = this.container ? this.container.querySelector('.moonfin-syncplay-leave-btn') : null;
        if (leaveBtn) leaveBtn.disabled = true;

        await this.leaveGroup();
        this._updateUI();
    },

    async _refreshGroupList() {
        this._groups = await this.listGroups();
        if (!this.isOpen || !this.container || this._group) return;

        var groupsContainer = this.container.querySelector('.moonfin-syncplay-groups');
        if (!groupsContainer) return;

        if (this._groups.length === 0) {
            groupsContainer.innerHTML = '<div class="moonfin-syncplay-empty">No active groups found</div>';
        } else {
            var html = '';
            for (var i = 0; i < this._groups.length; i++) {
                html += this._renderGroupCardHtml(this._groups[i]);
            }
            groupsContainer.innerHTML = html;
        }

        var self = this;
        groupsContainer.querySelectorAll('.moonfin-syncplay-group-card').forEach(function(card) {
            card.addEventListener('click', function() {
                var groupId = card.dataset.groupId;
                if (groupId) self._handleJoinGroup(groupId);
            });
        });
    },

    _updateUI() {
        if (!this.isOpen || !this.container) return;

        var content = this.container.querySelector('.moonfin-syncplay-content');
        if (content) {
            content.innerHTML = this._group ? this._renderGroupView() : this._renderLobbyView();
            this._bindContentEvents();
        }

        this._updateNavButton();
    },

    _updateNavButton() {
        var inGroup = !!this._group;
        var navBtn = document.querySelector('.moonfin-nav-syncplay');
        if (navBtn) navBtn.classList.toggle('active', inGroup);
        var sideBtn = document.querySelector('.moonfin-sidebar-syncplay');
        if (sideBtn) sideBtn.classList.toggle('active', inGroup);
    },

    _escapeHtml(str) {
        if (!str) return '';
        var div = document.createElement('div');
        div.textContent = str;
        return div.innerHTML;
    },

    destroy() {
        if (this._refreshInterval) {
            clearInterval(this._refreshInterval);
            this._refreshInterval = null;
        }
        if (this._onKeyDown) {
            document.removeEventListener('keydown', this._onKeyDown);
            this._onKeyDown = null;
        }
        if (this._wsHandler) {
            var api = API.getApiClient();
            if (api && window.Events && typeof window.Events.off === 'function') {
                window.Events.off(api, 'message', this._wsHandler);
            }
            this._wsHandler = null;
        }
        if (this.container) {
            this.container.remove();
            this.container = null;
        }
        this._group = null;
        this._groups = [];
        this.isOpen = false;
        this.initialized = false;
    }
};
