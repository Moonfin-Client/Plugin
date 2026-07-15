define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select', 'emby-scroller'], function (BaseView, loading) {
    'use strict';

    var PluginUniqueId = '9a1b2c3d-4e5f-6789-abcd-ef0123456789';

    var RATING_SOURCES = [
        { id: 'tomatoes', label: 'Rotten Tomatoes' },
        { id: 'tomatoes_audience', label: 'RT Audience Score' },
        { id: 'imdb', label: 'IMDb' },
        { id: 'tmdb', label: 'TMDB' },
        { id: 'metacritic', label: 'Metacritic' },
        { id: 'metacriticUser', label: 'Metacritic User' },
        { id: 'stars', label: 'Stars' },
        { id: 'trakt', label: 'Trakt' },
        { id: 'letterboxd', label: 'Letterboxd' },
        { id: 'myAnimeList', label: 'MyAnimeList' },
        { id: 'aniList', label: 'AniList' }
    ];

    var HOME_ROW_DEFINITIONS = [
        { id: 'resume', label: 'Continue Watching' },
        { id: 'nextup', label: 'Next Up' },
        { id: 'latestmedia', label: 'Recently Added Media' },
        { id: 'collections', label: 'Collections' },
        { id: 'smalllibrarytiles', label: 'My Media' },
        { id: 'recentlyreleased', label: 'Recently Released' }
    ];

    function esc(s) {
        var d = document.createElement('div');
        d.textContent = s;
        return d.innerHTML;
    }

    // Inline SVG icons for the Active Downloads cards. Emby's dashboard doesn't load the Material
    // Icons font, so these use SVG paths rather than font ligatures.
    var SYNC_ICONS = {
        person: 'M12 12c2.21 0 4-1.79 4-4s-1.79-4-4-4-4 1.79-4 4 1.79 4 4 4zm0 2c-2.67 0-8 1.34-8 4v2h16v-2c0-2.66-5.33-4-8-4z',
        memory: 'M15 9H9v6h6V9zm-2 4h-2v-2h2v2zm8-2V9h-2V7c0-1.1-.9-2-2-2h-2V3h-2v2h-2V3H9v2H7c-1.1 0-2 .9-2 2v2H3v2h2v2H3v2h2v2c0 1.1.9 2 2 2h2v2h2v-2h2v2h2v-2h2c1.1 0 2-.9 2-2v-2h2v-2h-2v-2h2zm-4 6H7V7h10v10z',
        speed: 'M20.38 8.57l-1.23 1.85a8 8 0 0 1-.22 7.58H5.07A8 8 0 0 1 15.58 6.85l1.85-1.23A10 10 0 0 0 3.35 19a2 2 0 0 0 1.72 1h13.85a2 2 0 0 0 1.74-1 10 10 0 0 0-.27-10.44zm-9.79 6.84a2 2 0 0 0 2.83 0l5.66-8.49-8.49 5.66a2 2 0 0 0 0 2.83z',
        schedule: 'M11.99 2C6.47 2 2 6.48 2 12s4.47 10 9.99 10C17.52 22 22 17.52 22 12S17.52 2 11.99 2zM12 20c-4.42 0-8-3.58-8-8s3.58-8 8-8 8 3.58 8 8-3.58 8-8 8zm.5-13H11v6l5.25 3.15.75-1.23-4.5-2.67z',
        play: 'M10 16.5l6-4.5-6-4.5v9zM12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 18c-4.41 0-8-3.59-8-8s3.59-8 8-8 8 3.59 8 8-3.59 8-8 8z',
        network: 'M15.9 5c-.17 0-.32.09-.41.23l-.07.15-5.18 11.65c-.16.29-.26.61-.26.96 0 1.11.9 2.01 2.01 2.01.96 0 1.77-.68 1.96-1.59l.01-.03L16.4 5.5c0-.28-.22-.5-.5-.5zM1 9l2 2c2.88-2.88 6.79-4.08 10.53-3.62l1.19-2.68C9.89 3.84 4.74 5.27 1 9zm20 2l2-2c-1.64-1.64-3.55-2.82-5.59-3.57l-.53 2.82c1.5.62 2.9 1.53 4.12 2.75zm-4 4l2-2c-.8-.8-1.7-1.42-2.66-1.89l-.55 2.92c.42.27.83.59 1.21.97zM5 13l2 2c1.13-1.13 2.56-1.79 4.03-2l1.28-2.88c-2.63-.08-5.3.87-7.31 2.88z'
    };

    function syncSvg(key, size, color) {
        var path = SYNC_ICONS[key] || '';
        return '<svg viewBox="0 0 24 24" aria-hidden="true" style="width:' + size + ';height:' + size +
            ';fill:' + (color || 'currentColor') + ';flex-shrink:0;vertical-align:middle;"><path d="' + path + '"/></svg>';
    }

    function formatTime(totalSeconds) {
        if (isNaN(totalSeconds) || !isFinite(totalSeconds) || totalSeconds < 0) return '--:--:--';
        var h = Math.floor(totalSeconds / 3600);
        var m = Math.floor((totalSeconds % 3600) / 60);
        var s = Math.floor(totalSeconds % 60);
        return [h, m > 9 ? m : '0' + m, s > 9 ? s : '0' + s].filter(function (val, i) { return val > 0 || i > 0; }).join(':');
    }

    function syncMetric(iconKey, label, value) {
        return '<div style="display:flex; align-items:center; gap:10px;">' +
            syncSvg(iconKey, '1.4em', 'rgba(255,255,255,0.7)') +
            '<div><div style="color:rgba(255,255,255,0.5);font-size:0.85em;text-transform:uppercase;letter-spacing:1px;">' + label + '</div>' +
            '<div style="font-weight:500;">' + value + '</div></div></div>';
    }

    // Emby's getPluginConfiguration/updatePluginConfiguration serializer uses C# property names
    // (PascalCase) and does NOT honor [JsonPropertyName("camelCase")] on the nested
    // DefaultUserSettings (MoonfinSettingsProfile). The config page reads/writes those defaults in
    // camelCase, so without this every default came back undefined on reload and round-tripped with
    // duplicate Pascal+camel keys on save. Recursively lower-case the first letter of every object
    // key so the defaults are consistently camelCase. No-op if Emby ever does emit camelCase.
    function camelKeysDeep(value) {
        if (Array.isArray(value)) {
            return value.map(camelKeysDeep);
        }
        if (value && typeof value === 'object') {
            var out = {};
            Object.keys(value).forEach(function (k) {
                var camel = k ? k.charAt(0).toLowerCase() + k.slice(1) : k;
                out[camel] = camelKeysDeep(value[k]);
            });
            return out;
        }
        return value;
    }

    function moonfinAuthHeaders() {
        var token = ApiClient.accessToken ? ApiClient.accessToken() : '';
        var headers = { 'Content-Type': 'application/json' };
        if (token) {
            headers['X-Emby-Token'] = token;
        }
        return headers;
    }

    function parseJsonResponse(response) {
        return response.text().then(function (text) {
            var payload = {};
            try {
                payload = text ? JSON.parse(text) : {};
            } catch (e) {
                payload = {};
            }
            if (!response.ok) {
                var error = new Error(payload.error || payload.Error || ('Request failed (' + response.status + ')'));
                error.payload = payload;
                throw error;
            }
            return payload;
        });
    }

    function setSelectValue(view, selector, value, dynamicLabelPrefix) {
        var select = view.querySelector(selector);
        if (!select) return;
        if (value == null || value === '') {
            select.value = '';
            return;
        }
        var normalized = String(value);
        var hasOption = false;
        for (var i = 0; i < select.options.length; i++) {
            if (select.options[i].value === normalized) { hasOption = true; break; }
        }
        if (!hasOption) {
            var option = document.createElement('option');
            option.value = normalized;
            option.textContent = (dynamicLabelPrefix || 'Current value') + ': ' + normalized;
            option.setAttribute('data-dynamic-option', 'true');
            select.appendChild(option);
        }
        select.value = normalized;
    }

    function setNullableBoolSelect(view, selector, value) {
        var select = view.querySelector(selector);
        if (!select) return;
        select.value = value === true ? 'true' : (value === false ? 'false' : '');
    }

    function getNullableBoolSelect(view, selector) {
        var select = view.querySelector(selector);
        if (!select) return null;
        if (select.value === 'true') return true;
        if (select.value === 'false') return false;
        return null;
    }

    function setNullableIntInput(view, selector, value) {
        var input = view.querySelector(selector);
        if (!input) return;
        input.value = value == null ? '' : String(value);
    }

    function getNullableIntInput(view, selector) {
        var input = view.querySelector(selector);
        if (!input) return null;
        var raw = (input.value || '').trim();
        if (raw === '') return null;
        var parsed = parseInt(raw, 10);
        return isNaN(parsed) ? null : parsed;
    }

    // ── Tabbed navigation ───────────────────────────────────────────────────

    function buildDefaultSettingsSubTabs(section) {
        if (!section || section.dataset.subTabsInit === 'true') return;
        var headings = Array.prototype.slice.call(section.querySelectorAll('h4'));
        if (!headings.length) return;
        section.dataset.subTabsInit = 'true';

        var tabBar = document.createElement('div');
        tabBar.className = 'moonfinSubTabs';
        tabBar.setAttribute('role', 'tablist');

        var entries = [];

        function selectSubTab(active) {
            entries.forEach(function (entry, i) {
                var on = i === active;
                entry.button.classList.toggle('is-active', on);
                entry.button.setAttribute('aria-selected', on ? 'true' : 'false');
                entry.panel.classList.toggle('is-active', on);
            });
        }

        headings.forEach(function (heading, index) {
            if (!heading || !heading.parentNode) return;

            var title = (heading.textContent || '').trim();

            var panel = document.createElement('div');
            panel.className = 'moonfinSubPanel';
            panel.setAttribute('role', 'tabpanel');
            panel.setAttribute('data-subpanel', String(index));

            var node = heading.nextSibling;
            while (node && !(node.nodeType === 1 && node.tagName && node.tagName.toUpperCase() === 'H4')) {
                var nextNode = node.nextSibling;
                panel.appendChild(node);
                node = nextNode;
            }

            heading.parentNode.insertBefore(panel, heading);
            heading.remove();

            var button = document.createElement('button');
            button.type = 'button';
            button.className = 'moonfinSubTab';
            button.setAttribute('role', 'tab');
            button.setAttribute('data-subtab', String(index));
            button.textContent = title;
            button.addEventListener('click', function () {
                selectSubTab(index);
            });

            tabBar.appendChild(button);
            entries.push({ button: button, panel: panel });
        });

        if (entries.length) {
            section.insertBefore(tabBar, entries[0].panel);
            selectSubTab(0);
        }
    }

    function initializeAdminTabs(view) {
        if (!view || view.dataset.tabsInitialized === 'true') return;
        view.dataset.tabsInitialized = 'true';

        var brandLogo = view.querySelector('#MoonfinBrandLogo');
        if (brandLogo && !brandLogo.getAttribute('src')) {
            // The asset endpoint needs a token here (an img tag sends none on its own), so build the
            // URL with the current api_key the way Emby serves all of its protected images. Hide the
            // image if it still fails so the text brand shows on its own instead of a broken icon.
            brandLogo.onerror = function () { brandLogo.style.display = 'none'; };
            if (typeof ApiClient !== 'undefined' && ApiClient.getUrl) {
                brandLogo.src = ApiClient.getUrl('Moonfin/Assets/icon.png', { api_key: ApiClient.accessToken() });
            } else {
                brandLogo.style.display = 'none';
            }
        }

        var navItems = Array.prototype.slice.call(view.querySelectorAll('.moonfinNavItem'));
        var panels = Array.prototype.slice.call(view.querySelectorAll('.moonfinTabPanel'));
        if (!navItems.length || !panels.length) return;

        function selectTab(tabId) {
            navItems.forEach(function (item) {
                item.classList.toggle('is-active', item.getAttribute('data-tab') === tabId);
            });
            panels.forEach(function (panel) {
                panel.classList.toggle('is-active', panel.getAttribute('data-tab') === tabId);
            });
            try { window.localStorage.setItem('moonfinAdminActiveTab', tabId); } catch (e) {}

            if (tabId === 'syncs') { startSyncsPolling(); } else { stopSyncsPolling(); }
        }

        // Active Downloads tab: polls the transcodes endpoint every few seconds while the tab is
        // visible so admins can watch client download progress.
        var syncsTimer = null;

        function stopSyncsPolling() {
            if (syncsTimer) { clearInterval(syncsTimer); syncsTimer = null; }
        }

        function startSyncsPolling() {
            loadActiveSyncs();
            if (!syncsTimer) { syncsTimer = setInterval(loadActiveSyncs, 3000); }
        }

        function loadActiveSyncs() {
            var container = view.querySelector('#MoonfinActiveSyncsList');
            var navItem = view.querySelector('.moonfinNavItem[data-tab="syncs"]');
            if (!container || !navItem) { stopSyncsPolling(); return; }
            if (!navItem.classList.contains('is-active')) { return; }

            ApiClient.getJSON(ApiClient.getUrl('Moonfin/Transcodes/Active')).then(function (jobs) {
                if (!jobs || jobs.length === 0) {
                    container.innerHTML = '<div style="padding:16px; text-align:center; color:rgba(255,255,255,0.5);">No active downloads</div>';
                    return;
                }

                var now = new Date().getTime();
                var html = '';
                jobs.forEach(function (job) {
                    var pct = job.CompletionPercentage ? job.CompletionPercentage.toFixed(1) : '0.0';
                    var fps = job.Framerate ? job.Framerate.toFixed(1) : '0.0';

                    var sessionText = 'Unknown session';
                    if (job.UserName || job.DeviceName) {
                        sessionText = esc(job.UserName || 'Unknown user');
                        if (job.DeviceName) { sessionText += ' on ' + esc(job.DeviceName); }
                        if (job.Client) { sessionText += ' · ' + esc(job.Client); }
                    }
                    var sessionBadge = '<span style="background:rgba(255,255,255,0.1); color:#fff; padding:4px 10px; border-radius:12px; font-size:0.85em; margin-right:8px; font-weight:600; display:inline-flex; align-items:center; gap:6px;">' + syncSvg('person', '1.2em') + sessionText + '</span>';
                    var hwBadge = job.IsHardwareAccelerated ? '<span style="background:rgba(82,181,75,0.2); color:#52b54b; padding:4px 10px; border-radius:12px; font-size:0.85em; margin-right:8px; font-weight:600; display:inline-flex; align-items:center; gap:6px;">' + syncSvg('memory', '1.2em') + 'Hardware accelerated</span>' : '';

                    var posText = '--:-- / --:--';
                    if (job.PositionTicks && job.RuntimeTicks) {
                        posText = formatTime(job.PositionTicks / 10000000) + ' / ' + formatTime(job.RuntimeTicks / 10000000);
                    }

                    var speedMult = job.Framerate ? job.Framerate / 24.0 : null;

                    var etaText = '--:--';
                    if (speedMult && speedMult > 0 && job.PositionTicks && job.RuntimeTicks) {
                        var remainingSecs = ((job.RuntimeTicks - job.PositionTicks) / 10000000) / speedMult;
                        etaText = formatTime(remainingSecs);
                    }

                    var speedMultText = speedMult ? '(' + speedMult.toFixed(1) + 'x)' : '';
                    var bitrateText = job.BitRate ? (job.BitRate / 1000000).toFixed(1) + ' Mbps' : 'Unknown';

                    html += '<div style="background:rgba(0,0,0,0.2); border:1px solid rgba(255,255,255,0.05); border-radius:10px; padding:16px; margin-bottom:12px; position:relative; overflow:hidden;">';
                    html += '<div style="font-weight:600; font-size:1.15em; color:#fff; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; max-width:85%; margin-bottom:12px;">' + esc(job.MediaSource || 'Unknown Media') + '</div>';
                    html += '<div style="margin-bottom:16px; display:flex; flex-wrap:wrap; gap:8px;">' + sessionBadge + hwBadge + '</div>';
                    html += '<div style="background:rgba(255,255,255,0.1); border-radius:6px; height:14px; width:100%; overflow:hidden; position:relative;">';
                    html += '<div style="background:#00a4dc; height:100%; width:' + pct + '%; box-shadow:0 0 10px rgba(0,164,220,0.6); transition:width 3s linear;"></div>';
                    html += '<div style="position:absolute; right:8px; top:0; font-size:0.75em; line-height:14px; color:#fff; font-weight:bold; text-shadow:1px 1px 2px #000;">' + pct + '%</div></div>';
                    html += '<div style="display:grid; grid-template-columns:1fr 1fr; gap:16px; font-size:0.95em; margin-top:12px;">';
                    html += syncMetric('speed', 'Speed', fps + ' fps ' + speedMultText);
                    html += syncMetric('schedule', 'ETA', etaText + ' remaining');
                    html += syncMetric('play', 'Position', posText);
                    html += syncMetric('network', 'Bitrate', bitrateText);
                    html += '</div>';
                    html += '<div style="display:flex; justify-content:flex-end; margin-top:12px;">';
                    html += '<button type="button" class="btnCancelSync" data-jobid="' + esc(job.Id) + '" style="background:rgba(211,47,47,0.1); border:1px solid #d32f2f; color:#ff5252; border-radius:6px; padding:8px 20px; cursor:pointer; font-weight:bold;">Cancel</button>';
                    html += '</div></div>';
                });
                container.innerHTML = html;

                var cancelBtns = container.querySelectorAll('.btnCancelSync');
                for (var i = 0; i < cancelBtns.length; i++) {
                    cancelBtns[i].addEventListener('click', function () {
                        var jid = this.getAttribute('data-jobid');
                        if (confirm('Cancel this download?\n\nThis sends a stop to the session. A background download with no controllable client may keep running until it finishes.')) {
                            ApiClient.ajax({ type: 'DELETE', url: ApiClient.getUrl('Moonfin/Transcodes/Active/' + jid) })
                                .then(function () { loadActiveSyncs(); }, function () { loadActiveSyncs(); });
                        }
                    });
                }
            }, function () {
                container.innerHTML = '<div style="padding:16px; text-align:center; color:#ff5252;">Could not load active downloads. The server may be restarting.</div>';
            });
        }

        navItems.forEach(function (item) {
            item.addEventListener('click', function () {
                selectTab(item.getAttribute('data-tab'));
            });
        });

        var defaultsPanel = view.querySelector('.moonfinTabPanel[data-tab="defaults"]');
        if (defaultsPanel) {
            var defaultsSection = defaultsPanel.querySelector('.verticalSection');
            if (defaultsSection) {
                buildDefaultSettingsSubTabs(defaultsSection);
            }
        }

        var saved = null;
        try { saved = window.localStorage.getItem('moonfinAdminActiveTab'); } catch (e) {}
        var validSaved = saved && panels.some(function (p) { return p.getAttribute('data-tab') === saved; });
        selectTab(validSaved ? saved : 'general');

        initializeSettingsSearch(view);
    }

    function initializeSettingsSearch(view) {
        var input = view.querySelector('#MoonfinSettingsSearch');
        if (!input || input.dataset.bound === 'true') return;
        input.dataset.bound = 'true';

        input.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') {
                e.preventDefault();
            }
        });

        var fields = Array.prototype.slice.call(
            view.querySelectorAll('.moonfinTabPanel .inputContainer, .moonfinTabPanel .checkboxContainer, .moonfinTabPanel .selectContainer')
        );

        input.addEventListener('input', function () {
            var query = (input.value || '').trim().toLowerCase();
            if (!query) {
                view.classList.remove('moonfin-searching');
                fields.forEach(function (field) { field.classList.remove('moonfin-hidden-by-search'); });
                return;
            }

            view.classList.add('moonfin-searching');
            fields.forEach(function (field) {
                var text = (field.textContent || '').toLowerCase();
                field.classList.toggle('moonfin-hidden-by-search', text.indexOf(query) === -1);
            });
        });
    }

    // ── Pickers ─────────────────────────────────────────────────────────────

    function loadGameLibraryPicker(view, selectedIds) {
        var picker = view.querySelector('#GameLibraryPicker');
        if (!picker) return;
        picker.innerHTML = '<div style="padding:8px;color:rgba(128,128,128,0.5);font-size:0.9em;">Loading...</div>';
        var userId = ApiClient.getCurrentUserId();
        ApiClient.getUserViews(userId).then(function (result) {
            var items = result.Items || [];
            if (items.length === 0) {
                picker.innerHTML = '<div style="padding:8px;color:rgba(128,128,128,0.5);font-size:0.9em;">No libraries found.</div>';
                return;
            }
            var html = '';
            for (var i = 0; i < items.length; i++) {
                var item = items[i];
                var isChecked = selectedIds.indexOf(item.Id) !== -1;
                html += '<label style="display:flex;align-items:center;gap:8px;padding:6px 8px;border-radius:4px;cursor:pointer;' + (isChecked ? 'background:rgba(82,181,75,0.15);' : '') + '">' +
                    '<input type="checkbox" class="gameLibraryCb" data-id="' + item.Id + '"' + (isChecked ? ' checked' : '') + ' style="width:16px;height:16px;">' +
                    '<div style="flex:1;min-width:0;"><div style="font-size:0.9em;color:rgba(128,128,128,0.9);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">' + esc(item.Name || 'Untitled') + '</div>' +
                    '<div style="font-size:0.75em;color:rgba(128,128,128,0.4);">' + esc(item.CollectionType || 'mixed') + '</div></div></label>';
            }
            picker.innerHTML = html;
        });
    }

    function loadAdminCollectionPicker(view, selectedIds) {
        var picker = view.querySelector('#DefaultCollectionPicker');
        if (!picker) return;
        picker.innerHTML = '<div style="padding:8px;color:rgba(128,128,128,0.5);font-size:0.9em;">Loading...</div>';
        var userId = ApiClient.getCurrentUserId();
        ApiClient.getItems(userId, {
            userId: userId,
            includeItemTypes: 'BoxSet,Playlist',
            sortBy: 'SortName',
            sortOrder: 'Ascending',
            recursive: true,
            fields: 'PrimaryImageAspectRatio',
            imageTypeLimit: 1,
            enableImageTypes: 'Primary'
        }).then(function (result) {
            var items = result.Items || [];
            if (items.length === 0) {
                picker.innerHTML = '<div style="padding:8px;color:rgba(128,128,128,0.5);font-size:0.9em;">No collections or playlists found.</div>';
                return;
            }
            var html = '';
            var serverUrl = ApiClient.serverAddress ? ApiClient.serverAddress() : '';
            for (var i = 0; i < items.length; i++) {
                var item = items[i];
                var isChecked = selectedIds.indexOf(item.Id) !== -1;
                var typeBadge = item.Type === 'BoxSet' ? 'Collection' : 'Playlist';
                var posterUrl = '';
                if (item.ImageTags && item.ImageTags.Primary) {
                    posterUrl = serverUrl + '/Items/' + item.Id + '/Images/Primary?maxWidth=40&quality=80&tag=' + item.ImageTags.Primary;
                }
                html += '<label style="display:flex;align-items:center;gap:8px;padding:6px 8px;border-radius:4px;cursor:pointer;' + (isChecked ? 'background:rgba(0,164,220,0.1);' : '') + '">' +
                    '<input type="checkbox" class="adminCollectionCb" data-id="' + item.Id + '"' + (isChecked ? ' checked' : '') + ' style="accent-color:#00a4dc;width:16px;height:16px;">' +
                    (posterUrl ? '<img src="' + posterUrl + '" style="width:32px;height:32px;border-radius:3px;object-fit:cover;">' : '<div style="width:32px;height:32px;border-radius:3px;background:rgba(128,128,128,0.08);"></div>') +
                    '<div style="flex:1;min-width:0;"><div style="font-size:0.9em;color:rgba(128,128,128,0.9);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">' + esc(item.Name || 'Untitled') + '</div>' +
                    '<div style="font-size:0.78em;color:rgba(128,128,128,0.4);">' + typeBadge + '</div></div></label>';
            }
            picker.innerHTML = html;
        });
    }

    function loadAdminLibraryPicker(view, selectedIds) {
        var picker = view.querySelector('#DefaultLibraryPicker');
        if (!picker) return;
        picker.innerHTML = '<div style="padding:8px;color:rgba(128,128,128,0.5);font-size:0.9em;">Loading...</div>';
        var userId = ApiClient.getCurrentUserId();
        ApiClient.getUserViews(userId).then(function (result) {
            var items = (result.Items || []).filter(function (item) {
                var ct = item.CollectionType;
                return ct === 'movies' || ct === 'tvshows' || ct === 'mixed';
            });
            if (items.length === 0) {
                picker.innerHTML = '<div style="padding:8px;color:rgba(128,128,128,0.5);font-size:0.9em;">No media libraries found.</div>';
                return;
            }
            var html = '';
            for (var i = 0; i < items.length; i++) {
                var item = items[i];
                var isChecked = selectedIds.indexOf(item.Id) !== -1;
                var typeLabel = item.CollectionType === 'movies' ? 'Movies' : item.CollectionType === 'tvshows' ? 'Shows' : 'Mixed';
                html += '<label style="display:flex;align-items:center;gap:8px;padding:6px 8px;border-radius:4px;cursor:pointer;' + (isChecked ? 'background:rgba(0,164,220,0.1);' : '') + '">' +
                    '<input type="checkbox" class="adminLibraryCb" data-id="' + item.Id + '"' + (isChecked ? ' checked' : '') + ' style="accent-color:#00a4dc;width:16px;height:16px;">' +
                    '<div style="flex:1;min-width:0;"><div style="font-size:0.9em;color:rgba(128,128,128,0.9);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">' + esc(item.Name || 'Untitled') + '</div>' +
                    '<div style="font-size:0.75em;color:rgba(128,128,128,0.4);">' + typeLabel + '</div></div></label>';
            }
            picker.innerHTML = html;
        });
    }

    function loadAdminGenrePicker(view, selectedIds) {
        var picker = view.querySelector('#DefaultGenrePicker');
        if (!picker) return;
        picker.innerHTML = '<div style="padding:8px;color:rgba(128,128,128,0.5);font-size:0.9em;">Loading...</div>';
        var serverUrl = ApiClient.serverAddress ? ApiClient.serverAddress() : '';
        fetch(serverUrl + '/Moonfin/Genres', { method: 'GET', headers: moonfinAuthHeaders() })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                var genres = data.Items || data.items || [];
                if (genres.length === 0) {
                    picker.innerHTML = '<div style="padding:8px;color:rgba(128,128,128,0.5);font-size:0.9em;">No genres found.</div>';
                    return;
                }
                var html = '';
                for (var i = 0; i < genres.length; i++) {
                    var g = genres[i];
                    var gId = g.id || g.Id;
                    var gName = g.name || g.Name;
                    var isChecked = selectedIds.indexOf(gId) !== -1;
                    html += '<label style="display:flex;align-items:center;gap:8px;padding:6px 8px;border-radius:4px;cursor:pointer;' + (isChecked ? 'background:rgba(0,164,220,0.1);' : '') + '">' +
                        '<input type="checkbox" class="adminGenreCb" data-id="' + esc(gId) + '"' + (isChecked ? ' checked' : '') + ' style="accent-color:#00a4dc;width:16px;height:16px;">' +
                        '<div style="flex:1;min-width:0;"><div style="font-size:0.9em;color:rgba(128,128,128,0.9);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">' + esc(gName) + '</div></div></label>';
                }
                picker.innerHTML = html;
            })
            .catch(function () {
                picker.innerHTML = '<div style="padding:8px;color:rgba(128,128,128,0.5);font-size:0.9em;">Failed to load genres.</div>';
            });
    }

    function loadRatingSourcesPicker(view, selectedIds) {
        var container = view.querySelector('#DefaultMdblistRatingSourcesList');
        if (!container) return;
        var selectedSet = {};
        var ordered = [];
        if (selectedIds && selectedIds.length > 0) {
            selectedIds.forEach(function (id) {
                if (id === 'rtAudience') id = 'tomatoes_audience';
                var src = RATING_SOURCES.find(function (s) { return s.id === id; });
                if (src) { ordered.push({ id: src.id, label: src.label, checked: true }); selectedSet[id] = true; }
            });
        }
        RATING_SOURCES.forEach(function (src) {
            if (!selectedSet[src.id]) ordered.push({ id: src.id, label: src.label, checked: false });
        });
        container.innerHTML = '';
        ordered.forEach(function (item) {
            var row = document.createElement('div');
            row.className = 'mdblistRatingItem';
            row.dataset.id = item.id;
            row.style.cssText = 'display:flex;align-items:center;gap:8px;padding:5px 8px;border-radius:4px;margin-bottom:2px;background:rgba(128,128,128,0.03);';
            row.innerHTML =
                '<input type="checkbox"' + (item.checked ? ' checked' : '') + ' style="accent-color:#00a4dc;width:16px;height:16px;flex-shrink:0;">' +
                '<span style="flex:1;font-size:0.9em;color:rgba(128,128,128,0.9);">' + esc(item.label) + '</span>' +
                '<button type="button" class="ratingMoveBtn" data-dir="up" style="background:none;border:1px solid rgba(128,128,128,0.2);border-radius:3px;color:rgba(128,128,128,0.7);padding:1px 6px;cursor:pointer;font-size:0.85em;">&#x2191;</button>' +
                '<button type="button" class="ratingMoveBtn" data-dir="down" style="background:none;border:1px solid rgba(128,128,128,0.2);border-radius:3px;color:rgba(128,128,128,0.7);padding:1px 6px;cursor:pointer;font-size:0.85em;">&#x2193;</button>';
            container.appendChild(row);
        });
        if (!container.dataset.hasListener) {
            container.dataset.hasListener = 'true';
            container.addEventListener('click', function (e) {
                var btn = e.target.closest('.ratingMoveBtn');
                if (!btn) return;
                var row = btn.closest('.mdblistRatingItem');
                if (!row) return;
                if (btn.dataset.dir === 'up' && row.previousElementSibling) {
                    container.insertBefore(row, row.previousElementSibling);
                } else if (btn.dataset.dir === 'down' && row.nextElementSibling) {
                    container.insertBefore(row.nextElementSibling, row);
                }
            });
        }
    }

    function getRatingSourcesValue(view) {
        var items = view.querySelectorAll('#DefaultMdblistRatingSourcesList .mdblistRatingItem');
        var result = [];
        items.forEach(function (item) {
            var cb = item.querySelector('input[type=checkbox]');
            if (cb && cb.checked) result.push(item.dataset.id);
        });
        return result.length > 0 ? result : null;
    }

    // ── Home layout builder ─────────────────────────────────────────────────

    var HOME_SECTION_DEFINITIONS = [
        { type: 'smalllibrarytiles', label: 'My Media' },
        { type: 'resume', label: 'Continue Watching' },
        { type: 'nextup', label: 'Next Up' },
        { type: 'latestmedia', label: 'Recently Added Media' },
        { type: 'recentlyreleased', label: 'Recently Released' },
        { type: 'livetv', label: 'Live TV' },
        { type: 'librarybuttons', label: 'Library Buttons' },
        { type: 'resumeaudio', label: 'Resume Audio' },
        { type: 'resumebook', label: 'Resume Books' },
        { type: 'activerecordings', label: 'Active Recordings' },
        { type: 'collections', label: 'Collections' },
        { type: 'favoritemovies', label: 'Favorite Movies' },
        { type: 'favoriteseries', label: 'Favorite Series' },
        { type: 'favoriteepisodes', label: 'Favorite Episodes' },
        { type: 'favoritepeople', label: 'Favorite People' },
        { type: 'favoriteartists', label: 'Favorite Artists' },
        { type: 'favoritemusicvideos', label: 'Favorite Music Videos' },
        { type: 'favoritealbums', label: 'Favorite Albums' },
        { type: 'favoritesongs', label: 'Favorite Songs' },
        { type: 'genres', label: 'Genres' },
        { type: 'playlists', label: 'Playlists' },
        { type: 'seerr_recent_requests', label: 'Seerr Recent Requests' },
        { type: 'seerr_recently_added', label: 'Seerr Recently Added' },
        { type: 'seerr_popular_movies', label: 'Seerr Popular Movies' },
        { type: 'seerr_upcoming_movies', label: 'Seerr Upcoming Movies' },
        { type: 'seerr_popular_series', label: 'Seerr Popular Series' },
        { type: 'seerr_upcoming_series', label: 'Seerr Upcoming Series' },
        { type: 'seerr_trending', label: 'Seerr Trending' },
        { type: 'seerr_movie_genres', label: 'Seerr Movie Genres' },
        { type: 'seerr_studios', label: 'Seerr Studios' },
        { type: 'seerr_series_genres', label: 'Seerr Series Genres' },
        { type: 'seerr_networks', label: 'Seerr Networks' }
    ];

    var HOME_LAYOUT_TABS = [
        { id: 'builtin', label: 'Basics' },
        { id: 'collections', label: 'Collections' },
        { id: 'playlists', label: 'Playlists' },
        { id: 'genres', label: 'Genres' },
        { id: 'seerr', label: 'Seerr' }
    ];

    var HOME_LAYOUT_SEERR_TYPES = {
        seerr_recent_requests: true,
        seerr_recently_added: true,
        seerr_popular_movies: true,
        seerr_upcoming_movies: true,
        seerr_popular_series: true,
        seerr_upcoming_series: true,
        seerr_trending: true,
        seerr_movie_genres: true,
        seerr_studios: true,
        seerr_series_genres: true,
        seerr_networks: true
    };

    function isSeerrHomeSectionType(type) {
        return !!HOME_LAYOUT_SEERR_TYPES[type];
    }

    function getHomeLayoutState(view) {
        if (!view.__moonfinHomeLayout) {
            view.__moonfinHomeLayout = {
                sections: [],
                selectedIndex: null,
                activeTab: 'builtin',
                search: '',
                available: { builtin: [], collections: [], playlists: [], genres: [], seerr: [] }
            };
        }
        return view.__moonfinHomeLayout;
    }

    function homeSectionDefinition(type) {
        return HOME_SECTION_DEFINITIONS.find(function (row) { return row.type === type; }) || null;
    }

    function homeSectionLabel(section) {
        if (!section) return 'Unknown row';
        if (section.kind === 'pluginDynamic') {
            return section.pluginDisplayText || section.pluginSection || 'Dynamic row';
        }
        var definition = homeSectionDefinition(section.type);
        return definition ? definition.label : (section.type || 'Unknown row');
    }

    function homeSectionMeta(section) {
        if (!section) return 'Basics';
        if (section.kind !== 'pluginDynamic') {
            return isSeerrHomeSectionType(section.type) ? 'Seerr' : 'Basics';
        }
        if (section.pluginSource === 'collections') return 'Collection';
        if (section.pluginSource === 'playlists') return 'Playlist';
        if (section.pluginSource === 'genres') return 'Genre';
        if (section.pluginSource === 'hss') return 'HSS';
        return 'Dynamic';
    }

    function homeSectionBadgeClass(section) {
        if (!section || section.kind !== 'pluginDynamic') {
            if (section && isSeerrHomeSectionType(section.type)) return 'homeLayoutBadge-seerr';
            return 'homeLayoutBadge-builtin';
        }
        if (section.pluginSource === 'collections') return 'homeLayoutBadge-collections';
        if (section.pluginSource === 'playlists') return 'homeLayoutBadge-playlists';
        if (section.pluginSource === 'genres') return 'homeLayoutBadge-genres';
        return 'homeLayoutBadge-dynamic';
    }

    function homeSectionKey(section) {
        if (!section) return '';
        if (section.kind === 'pluginDynamic') {
            return [
                'pluginDynamic',
                section.pluginSource || '',
                section.pluginSection || '',
                section.pluginAdditionalData || ''
            ].join(':');
        }
        return 'builtin:' + (section.type || '');
    }

    function candidateKey(candidate) {
        return candidate ? candidate.key : '';
    }

    function currentHomeLayoutKeys(state) {
        var keys = {};
        state.sections.forEach(function (section) {
            keys[homeSectionKey(section)] = true;
        });
        return keys;
    }

    function cloneHomeSection(section) {
        return JSON.parse(JSON.stringify(section));
    }

    function renumberHomeLayoutSections(state) {
        state.sections.forEach(function (section, index) {
            section.enabled = true;
            section.order = index;
        });
    }

    function legacyOrderToHomeSections(savedIds) {
        if (!savedIds || savedIds.length === 0) return [];
        return savedIds.reduce(function (result, id) {
            var definition = homeSectionDefinition(id);
            if (!definition) return result;
            result.push({
                kind: 'builtin',
                type: definition.type,
                enabled: true,
                order: result.length
            });
            return result;
        }, []);
    }

    function normalizeHomeSections(savedSections, legacyOrder) {
        var source = Array.isArray(savedSections) && savedSections.length > 0
            ? savedSections
            : legacyOrderToHomeSections(legacyOrder);
        var seen = {};
        var ordered = [];

        source.slice().sort(function (a, b) {
            return (a.order == null ? 0 : a.order) - (b.order == null ? 0 : b.order);
        }).forEach(function (section) {
            if (!section || section.enabled === false) return;
            var normalized;
            if (section.kind === 'pluginDynamic') {
                normalized = {
                    kind: 'pluginDynamic',
                    type: 'none',
                    enabled: true,
                    order: ordered.length,
                    serverId: section.serverId || '',
                    pluginSource: section.pluginSource || 'hss',
                    pluginSection: section.pluginSection || '',
                    pluginAdditionalData: section.pluginAdditionalData || '',
                    pluginDisplayText: section.pluginDisplayText || section.pluginSection || 'Dynamic row'
                };
            } else {
                var definition = homeSectionDefinition(section.type);
                if (!definition) return;
                normalized = {
                    kind: 'builtin',
                    type: definition.type,
                    enabled: true,
                    order: ordered.length
                };
            }

            var key = homeSectionKey(normalized);
            if (seen[key]) return;
            seen[key] = true;
            ordered.push(normalized);
        });

        return ordered;
    }

    function createBuiltinCandidate(definition) {
        return {
            key: 'builtin:' + definition.type,
            tab: 'builtin',
            label: definition.label,
            meta: isSeerrHomeSectionType(definition.type) ? 'Seerr' : 'Basics',
            badgeClass: isSeerrHomeSectionType(definition.type) ? 'homeLayoutBadge-seerr' : 'homeLayoutBadge-builtin',
            section: {
                kind: 'builtin',
                type: definition.type,
                enabled: true,
                order: 0
            }
        };
    }

    function createDynamicCandidate(tab, source, sectionName, id, label, meta, serverId) {
        return {
            key: ['pluginDynamic', source, sectionName, id || ''].join(':'),
            tab: tab,
            label: label || 'Untitled',
            meta: meta,
            badgeClass: 'homeLayoutBadge-' + (source || 'dynamic'),
            section: {
                kind: 'pluginDynamic',
                type: 'none',
                enabled: true,
                order: 0,
                serverId: serverId || '',
                pluginSource: source,
                pluginSection: sectionName,
                pluginAdditionalData: id || '',
                pluginDisplayText: label || 'Untitled'
            }
        };
    }

    function initializeHomeLayoutAvailableRows(state) {
        state.available.builtin = HOME_SECTION_DEFINITIONS
            .filter(function (definition) { return !isSeerrHomeSectionType(definition.type); })
            .map(createBuiltinCandidate);
        state.available.seerr = HOME_SECTION_DEFINITIONS
            .filter(function (definition) { return isSeerrHomeSectionType(definition.type); })
            .map(createBuiltinCandidate);
    }

    function getHomeLayoutInsertIndex(state) {
        var count = state.sections.length;
        var selected = state.selectedIndex;
        if (selected == null || selected < 0 || selected >= count) return count;
        return selected + 1;
    }

    function addHomeLayoutCandidate(view, candidate) {
        if (!candidate) return;
        var state = getHomeLayoutState(view);
        var keys = currentHomeLayoutKeys(state);
        if (keys[candidateKey(candidate)]) return;
        var section = cloneHomeSection(candidate.section);
        var insertIndex = getHomeLayoutInsertIndex(state);
        state.sections.splice(insertIndex, 0, section);
        state.selectedIndex = insertIndex;
        renumberHomeLayoutSections(state);
        renderHomeSectionsEditor(view);
        scrollSelectedHomeLayoutRowIntoView(view);
    }

    function removeHomeLayoutSection(view, index) {
        var state = getHomeLayoutState(view);
        if (index < 0 || index >= state.sections.length) return;
        state.sections.splice(index, 1);
        if (state.sections.length === 0) {
            state.selectedIndex = null;
        } else if (state.selectedIndex === index) {
            state.selectedIndex = Math.min(index, state.sections.length - 1);
        } else if (state.selectedIndex > index) {
            state.selectedIndex -= 1;
        }
        renumberHomeLayoutSections(state);
        renderHomeSectionsEditor(view);
    }

    function moveHomeLayoutSection(view, index, direction) {
        var state = getHomeLayoutState(view);
        var target = index + direction;
        if (index < 0 || target < 0 || index >= state.sections.length || target >= state.sections.length) return;
        var section = state.sections[index];
        state.sections[index] = state.sections[target];
        state.sections[target] = section;
        state.selectedIndex = target;
        renumberHomeLayoutSections(state);
        renderHomeSectionsEditor(view);
    }

    function renderHomeLayoutTabs(view) {
        var state = getHomeLayoutState(view);
        var tabs = view.querySelector('#DefaultHomeAvailableTabs');
        if (!tabs) return;
        tabs.innerHTML = '';
        HOME_LAYOUT_TABS.forEach(function (tab) {
            var button = document.createElement('button');
            button.type = 'button';
            button.className = 'homeLayoutTabButton' + (state.activeTab === tab.id ? ' is-active' : '');
            button.dataset.tab = tab.id;
            button.textContent = tab.label;
            tabs.appendChild(button);
        });
    }

    function renderHomeLayoutRows(view) {
        var state = getHomeLayoutState(view);
        var container = view.querySelector('#DefaultHomeRowOrder');
        if (!container) return;
        container.innerHTML = '';

        if (state.sections.length === 0) {
            container.innerHTML = '<div class="homeLayoutEmpty">No default rows configured. Add rows from the catalog.</div>';
            return;
        }

        state.sections.forEach(function (section, index) {
            var row = document.createElement('div');
            row.className = 'homeLayoutRow' + (state.selectedIndex === index ? ' is-selected' : '');
            row.dataset.index = String(index);
            row.innerHTML =
                '<div class="homeLayoutRowText">' +
                '<div class="homeLayoutRowTitle">' + esc(homeSectionLabel(section)) + '</div>' +
                '<span class="homeLayoutBadge ' + homeSectionBadgeClass(section) + '">' + esc(homeSectionMeta(section)) + '</span>' +
                '</div>' +
                '<button type="button" class="homeLayoutIconButton" data-action="up" title="Move up"' + (index === 0 ? ' disabled' : '') + '>&#x2191;</button>' +
                '<button type="button" class="homeLayoutIconButton" data-action="down" title="Move down"' + (index === state.sections.length - 1 ? ' disabled' : '') + '>&#x2193;</button>' +
                '<button type="button" class="homeLayoutIconButton" data-action="remove" title="Remove">&#x2715;</button>';
            container.appendChild(row);
        });
    }

    function renderHomeAvailableRows(view) {
        var state = getHomeLayoutState(view);
        var container = view.querySelector('#DefaultHomeAvailableRows');
        if (!container) return;
        var tab = state.activeTab;
        var search = (state.search || '').toLowerCase();
        var keys = currentHomeLayoutKeys(state);
        var candidates = (state.available[tab] || []).filter(function (candidate) {
            if (!search) return true;
            return (candidate.label || '').toLowerCase().indexOf(search) !== -1
                || (candidate.meta || '').toLowerCase().indexOf(search) !== -1;
        });

        container.innerHTML = '';
        if (candidates.length === 0) {
            container.innerHTML = '<div class="homeLayoutEmpty">No rows found.</div>';
            return;
        }

        candidates.forEach(function (candidate) {
            var added = !!keys[candidateKey(candidate)];
            var row = document.createElement('div');
            row.className = 'homeAvailableRow' + (added ? ' is-added' : '');
            row.dataset.key = candidate.key;
            row.innerHTML =
                '<div class="homeAvailableRowText">' +
                '<div class="homeAvailableRowTitle">' + esc(candidate.label) + '</div>' +
                '</div>' +
                '<button type="button" class="homeLayoutAddButton" data-action="add"' + (added ? ' disabled' : '') + '>Add</button>';
            container.appendChild(row);
        });
    }

    function scrollSelectedHomeLayoutRowIntoView(view) {
        window.setTimeout(function () {
            var container = view.querySelector('#DefaultHomeRowOrder');
            if (!container) return;
            var selected = container.querySelector('.homeLayoutRow.is-selected');
            if (!selected) return;
            selected.scrollIntoView({ block: 'nearest', inline: 'nearest' });
        }, 0);
    }

    function renderHomeSectionsEditor(view) {
        renderHomeLayoutRows(view);
        renderHomeLayoutTabs(view);
        renderHomeAvailableRows(view);
    }

    function findHomeLayoutCandidate(view, key) {
        var state = getHomeLayoutState(view);
        var candidates = state.available[state.activeTab] || [];
        for (var i = 0; i < candidates.length; i++) {
            if (candidates[i].key === key) return candidates[i];
        }
        return null;
    }

    function loadHomeSectionsEditor(view, savedSections, legacyOrder) {
        var state = getHomeLayoutState(view);
        initializeHomeLayoutAvailableRows(state);
        state.sections = normalizeHomeSections(savedSections, legacyOrder);
        state.selectedIndex = state.sections.length > 0 ? state.sections.length - 1 : null;
        renumberHomeLayoutSections(state);
        bindHomeLayoutEditorEvents(view);
        renderHomeSectionsEditor(view);
        loadHomeLayoutDynamicRows(view);
    }

    function bindHomeLayoutEditorEvents(view) {
        var state = getHomeLayoutState(view);
        var layout = view.querySelector('#DefaultHomeRowOrder');
        var tabs = view.querySelector('#DefaultHomeAvailableTabs');
        var available = view.querySelector('#DefaultHomeAvailableRows');
        var search = view.querySelector('#DefaultHomeAvailableSearch');
        if (layout && !layout.dataset.bound) {
            layout.dataset.bound = 'true';
            layout.addEventListener('click', function (event) {
                var row = event.target.closest('.homeLayoutRow');
                if (!row) return;
                var index = parseInt(row.dataset.index, 10);
                var actionButton = event.target.closest('[data-action]');
                if (isNaN(index)) return;
                if (!actionButton) {
                    state.selectedIndex = index;
                    renderHomeSectionsEditor(view);
                    return;
                }
                var action = actionButton.dataset.action;
                if (action === 'up') moveHomeLayoutSection(view, index, -1);
                if (action === 'down') moveHomeLayoutSection(view, index, 1);
                if (action === 'remove') removeHomeLayoutSection(view, index);
            });
        }

        if (tabs && !tabs.dataset.bound) {
            tabs.dataset.bound = 'true';
            tabs.addEventListener('click', function (event) {
                var button = event.target.closest('.homeLayoutTabButton');
                if (!button) return;
                state.activeTab = button.dataset.tab || 'builtin';
                state.search = '';
                if (search) search.value = '';
                renderHomeSectionsEditor(view);
            });
        }

        if (available && !available.dataset.bound) {
            available.dataset.bound = 'true';
            available.addEventListener('click', function (event) {
                var button = event.target.closest('[data-action="add"]');
                if (!button || button.disabled) return;
                var row = button.closest('.homeAvailableRow');
                if (!row) return;
                addHomeLayoutCandidate(view, findHomeLayoutCandidate(view, row.dataset.key));
            });
        }

        if (search && !search.dataset.bound) {
            search.dataset.bound = 'true';
            search.addEventListener('input', function () {
                state.search = search.value || '';
                renderHomeAvailableRows(view);
            });
        }
    }

    function loadHomeLayoutDynamicRows(view) {
        loadHomeLayoutCollections(view);
        loadHomeLayoutPlaylists(view);
        loadHomeLayoutGenres(view);
    }

    function setHomeLayoutAvailable(view, tab, candidates) {
        var state = getHomeLayoutState(view);
        state.available[tab] = candidates;
        if (state.activeTab === tab) {
            renderHomeAvailableRows(view);
        }
    }

    function loadHomeLayoutCollections(view) {
        var userId = ApiClient.getCurrentUserId();
        var serverId = ApiClient.serverAddress ? ApiClient.serverAddress() : '';
        ApiClient.getItems(userId, {
            userId: userId,
            includeItemTypes: 'BoxSet',
            sortBy: 'SortName',
            sortOrder: 'Ascending',
            recursive: true,
            fields: 'PrimaryImageAspectRatio',
            imageTypeLimit: 1,
            enableImageTypes: 'Primary'
        }).then(function (result) {
            var items = result.Items || [];
            setHomeLayoutAvailable(view, 'collections', items.map(function (item) {
                return createDynamicCandidate('collections', 'collections', 'collection', item.Id, item.Name || 'Untitled', 'Collection row', serverId);
            }));
        }).catch(function () {
            setHomeLayoutAvailable(view, 'collections', []);
        });
    }

    function loadHomeLayoutPlaylists(view) {
        var userId = ApiClient.getCurrentUserId();
        var serverId = ApiClient.serverAddress ? ApiClient.serverAddress() : '';
        ApiClient.getItems(userId, {
            userId: userId,
            includeItemTypes: 'Playlist',
            sortBy: 'SortName',
            sortOrder: 'Ascending',
            recursive: true,
            fields: 'PrimaryImageAspectRatio',
            imageTypeLimit: 1,
            enableImageTypes: 'Primary'
        }).then(function (result) {
            var items = result.Items || [];
            setHomeLayoutAvailable(view, 'playlists', items.map(function (item) {
                return createDynamicCandidate('playlists', 'playlists', 'playlist', item.Id, item.Name || 'Untitled', 'Playlist row', serverId);
            }));
        }).catch(function () {
            setHomeLayoutAvailable(view, 'playlists', []);
        });
    }

    function mapGenreCandidates(items, serverId) {
        return (items || []).map(function (item) {
            var id = item.id || item.Id;
            var name = item.name || item.Name || 'Untitled';
            return createDynamicCandidate('genres', 'genres', 'genre', id, name, 'Genre row', serverId);
        }).filter(function (candidate) { return !!candidate.section.pluginAdditionalData; });
    }

    function loadEmbyGenresFallback(serverId) {
        var userId = ApiClient.getCurrentUserId();
        var query = '?userId=' + encodeURIComponent(userId)
            + '&sortBy=SortName&sortOrder=Ascending&recursive=true'
            + '&fields=ItemCounts&includeItemTypes=Movie,Series';
        return fetch(serverId + '/Genres' + query, { method: 'GET', headers: moonfinAuthHeaders() })
            .then(function (response) { return response.json(); })
            .then(function (data) { return data.Items || data.items || []; });
    }

    function loadHomeLayoutGenres(view) {
        var serverId = ApiClient.serverAddress ? ApiClient.serverAddress() : '';
        fetch(serverId + '/Moonfin/Genres', { method: 'GET', headers: moonfinAuthHeaders() })
            .then(function (response) { return response.json(); })
            .then(function (data) {
                var items = data.Items || data.items || [];
                if (items.length > 0) {
                    setHomeLayoutAvailable(view, 'genres', mapGenreCandidates(items, serverId));
                    return null;
                }
                return loadEmbyGenresFallback(serverId).then(function (fallbackItems) {
                    setHomeLayoutAvailable(view, 'genres', mapGenreCandidates(fallbackItems, serverId));
                });
            }).catch(function () {
                loadEmbyGenresFallback(serverId)
                    .then(function (items) {
                        setHomeLayoutAvailable(view, 'genres', mapGenreCandidates(items, serverId));
                    })
                    .catch(function () {
                        setHomeLayoutAvailable(view, 'genres', []);
                    });
            });
    }

    function getHomeSectionsValue(view) {
        var state = getHomeLayoutState(view);
        if (state.sections.length === 0) return null;
        renumberHomeLayoutSections(state);
        return state.sections.map(function (section) {
            var result = {
                type: section.kind === 'pluginDynamic' ? 'none' : section.type,
                enabled: true,
                order: section.order
            };
            if (section.kind === 'pluginDynamic') {
                result.kind = 'pluginDynamic';
                result.pluginSource = section.pluginSource || 'hss';
                if (section.serverId) result.serverId = section.serverId;
                if (section.pluginSection) result.pluginSection = section.pluginSection;
                if (section.pluginAdditionalData) result.pluginAdditionalData = section.pluginAdditionalData;
                if (section.pluginDisplayText) result.pluginDisplayText = section.pluginDisplayText;
            } else {
                result.kind = 'builtin';
            }
            return result;
        });
    }

    function getHomeRowOrderValue(view, homeSections) {
        var sections = homeSections || getHomeSectionsValue(view) || [];
        var result = [];
        sections.forEach(function (section) {
            if ((section.kind === 'builtin' || !section.kind) && section.enabled && section.type && section.type !== 'none') {
                result.push(section.type);
            }
        });
        return result.length > 0 ? result : null;
    }

    // ── Game cores ──────────────────────────────────────────────────────────

    function refreshCoresStatus(view, state) {
        var statusEl = view.querySelector('#GameCoresStatus');
        var btn = view.querySelector('#GameCoresInstallBtn');
        if (!statusEl || !btn) return;
        var serverUrl = ApiClient.serverAddress ? ApiClient.serverAddress() : '';
        fetch(serverUrl + '/Moonfin/Games/Cores/Status', { headers: moonfinAuthHeaders() })
            .then(function (r) { return r.json(); })
            .then(function (s) {
                if (s.downloading) {
                    statusEl.textContent = 'Downloading cores... (' + (s.filesInstalled || 0) + ' files so far)';
                    btn.disabled = true;
                    if (!state.timer) state.timer = setInterval(function () { refreshCoresStatus(view, state); }, 4000);
                    return;
                }
                if (state.timer) { clearInterval(state.timer); state.timer = null; }
                btn.disabled = false;
                if (s.installed) { statusEl.textContent = 'Installed on server (offline ready).'; btn.textContent = 'Re-download cores'; }
                else if (s.state === 'failed') { statusEl.textContent = 'Download failed: ' + (s.error || 'unknown error'); }
                else { statusEl.textContent = 'Using EmulatorJS CDN (no local cores).'; }
            })
            .catch(function () { statusEl.textContent = ''; });
    }

    function initGameCores(view, state) {
        var btn = view.querySelector('#GameCoresInstallBtn');
        if (!btn) return;
        if (btn.dataset.wired !== '1') {
            btn.dataset.wired = '1';
            btn.addEventListener('click', function () {
                var serverUrl = ApiClient.serverAddress ? ApiClient.serverAddress() : '';
                var statusEl = view.querySelector('#GameCoresStatus');
                if (statusEl) statusEl.textContent = 'Starting download...';
                btn.disabled = true;
                fetch(serverUrl + '/Moonfin/Games/Cores/Install', { method: 'POST', headers: moonfinAuthHeaders() })
                    .then(function () { refreshCoresStatus(view, state); })
                    .catch(function () { if (statusEl) statusEl.textContent = 'Failed to start download.'; btn.disabled = false; });
            });

            var uploadBtn = view.querySelector('#GameCoresUploadBtn');
            if (uploadBtn) {
                uploadBtn.addEventListener('click', function () {
                    var fileInput = view.querySelector('#GameCoresFile');
                    var statusEl = view.querySelector('#GameCoresStatus');
                    var file = fileInput && fileInput.files ? fileInput.files[0] : null;
                    if (!file) { if (statusEl) statusEl.textContent = 'Choose a .zip file first.'; return; }
                    var serverUrl = ApiClient.serverAddress ? ApiClient.serverAddress() : '';
                    var token = ApiClient.accessToken ? ApiClient.accessToken() : '';
                    var headers = token ? { 'X-Emby-Token': token, 'Content-Type': 'application/octet-stream' } : { 'Content-Type': 'application/octet-stream' };
                    if (statusEl) statusEl.textContent = 'Uploading cores zip... (this can take a while)';
                    uploadBtn.disabled = true;
                    fetch(serverUrl + '/Moonfin/Games/Cores/Upload', { method: 'POST', headers: headers, body: file })
                        .then(function () { refreshCoresStatus(view, state); })
                        .catch(function () { if (statusEl) statusEl.textContent = 'Upload failed.'; })
                        .finally(function () { uploadBtn.disabled = false; });
                });
            }
        }
        refreshCoresStatus(view, state);
    }

    // ── Uploaded themes ─────────────────────────────────────────────────────

    function formatThemeSize(bytes) {
        var value = Number(bytes || 0);
        if (!isFinite(value) || value < 1024) return Math.max(0, Math.round(value)) + ' B';
        if (value < (1024 * 1024)) return (value / 1024).toFixed(1) + ' KB';
        return (value / (1024 * 1024)).toFixed(2) + ' MB';
    }

    function formatThemeUploadedAt(value) {
        if (!value) return 'Unknown date';
        var date = new Date(value);
        if (isNaN(date.getTime())) return 'Unknown date';
        return date.toLocaleString();
    }

    function setThemeUploadResult(view, message, color) {
        var result = view.querySelector('#AdminThemeUploadResult');
        if (!result) return;
        if (!message) { result.style.display = 'none'; result.textContent = ''; return; }
        result.style.display = '';
        result.style.color = color || 'rgba(128,128,128,0.85)';
        result.textContent = message;
    }

    function setSelectedThemeFileLabel(view, file) {
        var label = view.querySelector('#AdminThemeChosenFile');
        if (!label) return;
        label.textContent = file ? (file.name + ' (' + formatThemeSize(file.size) + ')') : 'No file selected';
    }

    function getAdminThemesFromPayload(payload) {
        if (!payload) return [];
        if (Array.isArray(payload.items)) return payload.items;
        if (Array.isArray(payload.Items)) return payload.Items;
        return [];
    }

    function renderAdminThemesList(view, items) {
        var container = view.querySelector('#AdminThemesList');
        if (!container) return;
        if (!items || items.length === 0) {
            container.innerHTML = '<div style="padding:8px;color:rgba(128,128,128,0.55);font-size:0.9em;">No uploaded themes yet.</div>';
            return;
        }
        var html = '';
        items.forEach(function (item) {
            var id = item.id || item.Id || '';
            var displayName = item.displayName || item.DisplayName || id;
            var sizeBytes = item.sizeBytes != null ? item.sizeBytes : item.SizeBytes;
            var uploadedAt = item.uploadedAtUtc || item.UploadedAtUtc;
            var checksum = item.checksumSha256 || item.ChecksumSha256 || '';
            html += '<div style="display:flex;align-items:flex-start;gap:10px;padding:8px;border:1px solid rgba(128,128,128,0.08);border-radius:4px;margin-bottom:6px;background:rgba(128,128,128,0.02);">' +
                '<div style="flex:1;min-width:0;">' +
                '<div style="display:flex;align-items:center;gap:8px;flex-wrap:wrap;">' +
                '<strong style="font-size:0.95em;color:rgba(128,128,128,0.95);">' + esc(displayName) + '</strong></div>' +
                '<div style="font-size:0.8em;color:rgba(128,128,128,0.65);margin-top:4px;">ID: <code>' + esc(id) + '</code></div>' +
                '<div style="font-size:0.78em;color:rgba(128,128,128,0.52);margin-top:3px;">' +
                'Uploaded: ' + esc(formatThemeUploadedAt(uploadedAt)) +
                ' &bull; Size: ' + esc(formatThemeSize(sizeBytes)) +
                (checksum ? ' &bull; SHA256: ' + esc(checksum.slice(0, 12)) : '') +
                '</div></div>' +
                '<button type="button" class="adminThemeDeleteBtn" data-theme-id="' + esc(id) + '" title="Delete theme" style="background:none;border:1px solid rgba(239,68,68,0.6);color:#ef4444;border-radius:4px;padding:2px 8px;cursor:pointer;line-height:1.1;">&#x2715;</button>' +
                '</div>';
        });
        container.innerHTML = html;
    }

    function loadAdminThemesList(view) {
        var container = view.querySelector('#AdminThemesList');
        if (!container) return;
        var serverUrl = ApiClient.serverAddress ? ApiClient.serverAddress() : '';
        container.innerHTML = '<div style="padding:8px;color:rgba(128,128,128,0.55);font-size:0.9em;">Loading uploaded themes...</div>';
        fetch(serverUrl + '/Moonfin/Admin/Themes', { method: 'GET', headers: moonfinAuthHeaders() })
            .then(parseJsonResponse)
            .then(function (payload) { renderAdminThemesList(view, getAdminThemesFromPayload(payload)); })
            .catch(function (error) {
                container.innerHTML = '<div style="padding:8px;color:#d9534f;font-size:0.9em;">' +
                    esc((error && error.message) ? error.message : 'Failed to load uploaded themes.') + '</div>';
            });
    }

    function clearSelectedThemeFile(view) {
        var fileInput = view.querySelector('#AdminThemeFileInput');
        var uploadButton = view.querySelector('#AdminThemeUploadBtn');
        if (fileInput) fileInput.value = '';
        if (uploadButton) uploadButton.disabled = true;
        setSelectedThemeFileLabel(view, null);
    }

    function uploadSelectedThemeFile(view) {
        var fileInput = view.querySelector('#AdminThemeFileInput');
        var uploadButton = view.querySelector('#AdminThemeUploadBtn');
        var file = fileInput && fileInput.files ? fileInput.files[0] : null;
        if (!file) { setThemeUploadResult(view, 'Select a JSON file first.', '#d9534f'); return; }
        uploadButton.disabled = true;
        setThemeUploadResult(view, '', '');
        file.text()
            .then(function (raw) {
                var payload;
                try { payload = JSON.parse(raw); } catch (error) { throw new Error('Invalid JSON file.'); }
                if (!payload || typeof payload !== 'object' || Array.isArray(payload)) {
                    throw new Error('Theme file must contain a JSON object.');
                }
                var serverUrl = ApiClient.serverAddress ? ApiClient.serverAddress() : '';
                return fetch(serverUrl + '/Moonfin/Admin/Themes', {
                    method: 'POST',
                    headers: moonfinAuthHeaders(),
                    body: JSON.stringify(payload)
                });
            })
            .then(parseJsonResponse)
            .then(function (payload) {
                var item = payload.item || payload.Item || {};
                var displayName = item.displayName || item.DisplayName || 'Theme';
                setThemeUploadResult(view, 'Uploaded "' + displayName + '" successfully.', '#52b54b');
                clearSelectedThemeFile(view);
                loadAdminThemesList(view);
            })
            .catch(function (error) {
                var message = (error && error.message) ? error.message : 'Upload failed.';
                if (error && error.payload && Array.isArray(error.payload.errors) && error.payload.errors.length > 0) {
                    message = error.payload.errors.join(' | ');
                }
                setThemeUploadResult(view, message, '#d9534f');
            })
            .finally(function () {
                if (uploadButton && !(fileInput && fileInput.files && fileInput.files.length)) {
                    uploadButton.disabled = true;
                }
            });
    }

    function deleteUploadedTheme(view, themeId) {
        if (!themeId) return;
        if (!window.confirm('Delete uploaded theme "' + themeId + '" from the plugin?')) return;
        var serverUrl = ApiClient.serverAddress ? ApiClient.serverAddress() : '';
        setThemeUploadResult(view, '', '');
        fetch(serverUrl + '/Moonfin/Admin/Themes/' + encodeURIComponent(themeId), {
            method: 'DELETE',
            headers: moonfinAuthHeaders()
        })
            .then(parseJsonResponse)
            .then(function () { setThemeUploadResult(view, 'Deleted "' + themeId + '".', '#52b54b'); loadAdminThemesList(view); })
            .catch(function (error) { setThemeUploadResult(view, (error && error.message) ? error.message : 'Delete failed.', '#d9534f'); });
    }

    // ── Load / save ─────────────────────────────────────────────────────────

    function loadConfig(view) {
        loading.show();
        ApiClient.getPluginConfiguration(PluginUniqueId).then(function (config) {
            view.querySelector('#EnableSettingsSync').checked = config.EnableSettingsSync;
            view.querySelector('#SeerrEnabled').checked = config.SeerrEnabled;
            view.querySelector('#SeerrUrl').value = config.SeerrUrl || '';
            view.querySelector('#SeerrDisplayName').value = config.SeerrDisplayName || '';
            view.querySelector('#MdblistApiKey').value = config.MdblistApiKey || '';
            view.querySelector('#TmdbApiKey').value = config.TmdbApiKey || '';
            view.querySelector('#ImdbListsEnabled').checked = config.ImdbListsEnabled !== false;
            view.querySelector('#StudioLogosEnabled').checked = config.StudioLogosEnabled !== false;
            view.querySelector('#FcmServiceAccountJson').value = config.FcmServiceAccountJson || '';
            view.querySelector('#FcmServiceAccountPath').value = config.FcmServiceAccountPath || '';
            view.querySelector('#WebDefaultServerUrl').value = config.WebDefaultServerUrl || '';
            view.querySelector('#WebForcedServerUrl').value = config.WebForcedServerUrl || '';
            view.querySelector('#WebEnableWebRtcScan').checked = config.WebEnableWebRtcScan !== false;

            view.querySelector('#GamesEnabled').checked = config.GamesEnabled === true;
            loadGameLibraryPicker(view, config.GameLibraryIds || []);
            initGameCores(view, view.__moonfinState);
            view.querySelector('#GamesCoreZipUrl').value = config.GamesCoreZipUrl || '';

            var defaults = camelKeysDeep(config.DefaultUserSettings) || {};
            setSelectValue(view, '#DefaultVisualTheme', defaults.visualTheme, 'Configured theme');
            setSelectValue(view, '#DefaultDetailScreenStyle', defaults.detailScreenStyle, 'Configured style');
            setNullableBoolSelect(view, '#DefaultDetailExpandedTabs', defaults.detailExpandedTabs);
            setSelectValue(view, '#DefaultFocusColor', defaults.focusColor, 'Configured color');
            setSelectValue(view, '#DefaultWatchedIndicator', defaults.watchedIndicator, 'Configured mode');
            setNullableBoolSelect(view, '#DefaultCardFocusExpansion', defaults.cardFocusExpansion);
            setSelectValue(view, '#DefaultScreensaverMode', defaults.screensaverMode, 'Configured mode');

            setSelectValue(view, '#DefaultNavbarPosition', defaults.navbarPosition, 'Configured position');
            setSelectValue(view, '#DefaultNavbarColor', defaults.navbarColor, 'Configured color');
            setNullableIntInput(view, '#DefaultNavbarOpacity', defaults.navbarOpacity);

            setSelectValue(view, '#DefaultMediaBarSourceType', defaults.mediaBarSourceType, 'Configured source');
            setSelectValue(view, '#DefaultMediaBarMode', defaults.mediaBarMode, 'Configured mode');
            setNullableBoolSelect(view, '#DefaultMediaBarTrailerAudio', defaults.mediaBarTrailerAudio);

            setSelectValue(view, '#DefaultHomeRowsStyle', defaults.homeRowsStyle, 'Configured style');
            setNullableBoolSelect(view, '#DefaultFullScreenRows', defaults.fullScreenRows);
            setNullableBoolSelect(view, '#DefaultUseDetailedSubHeadings', defaults.useDetailedSubHeadings);
            setSelectValue(view, '#DefaultHomeImageTypeContinueWatching', defaults.homeImageTypeContinueWatching, 'Configured image type');
            setSelectValue(view, '#DefaultPosterSize', defaults.posterSize, 'Configured size');
            setNullableBoolSelect(view, '#DefaultDisplayFavoritesRows', defaults.displayFavoritesRows);
            setNullableBoolSelect(view, '#DefaultDisplayCollectionsRows', defaults.displayCollectionsRows);
            setNullableBoolSelect(view, '#DefaultDisplayGenresRows', defaults.displayGenresRows);
            setNullableBoolSelect(view, '#DefaultDisplayPlaylistsRows', defaults.displayPlaylistsRows);
            setNullableBoolSelect(view, '#DefaultDisplayAudioRows', defaults.displayAudioRows);
            setSelectValue(view, '#DefaultFavoritesRowSortBy', defaults.favoritesRowSortBy, 'Configured sort');
            setSelectValue(view, '#DefaultCollectionsRowSortBy', defaults.collectionsRowSortBy, 'Configured sort');
            setSelectValue(view, '#DefaultGenresRowSortBy', defaults.genresRowSortBy, 'Configured sort');
            setSelectValue(view, '#DefaultGenresRowItemFilter', defaults.genresRowItemFilter, 'Configured filter');
            setNullableBoolSelect(view, '#DefaultHomeImageUseSeriesImage', defaults.homeImageUseSeriesImage);
            loadHomeSectionsEditor(view, defaults.homeSections || null, defaults.homeRowOrder || null);
            view.querySelector('#DefaultMergeContinueWatchingNextUp').checked = !!defaults.mergeContinueWatchingNextUp;

            view.querySelector('#DefaultShowShuffleButton').checked = !!defaults.showShuffleButton;
            view.querySelector('#DefaultShowGenresButton').checked = !!defaults.showGenresButton;
            view.querySelector('#DefaultShowFavoritesButton').checked = !!defaults.showFavoritesButton;
            view.querySelector('#DefaultShowCastButton').checked = !!defaults.showCastButton;
            view.querySelector('#DefaultShowSyncPlayButton').checked = !!defaults.showSyncPlayButton;
            view.querySelector('#DefaultShowLibrariesInToolbar').checked = !!defaults.showLibrariesInToolbar;

            setNullableBoolSelect(view, '#DefaultMediaBarTrailerPreview', defaults.mediaBarTrailerPreview);
            setNullableBoolSelect(view, '#DefaultEpisodePreviewEnabled', defaults.episodePreviewEnabled);
            setNullableBoolSelect(view, '#DefaultPreviewAudioEnabled', defaults.previewAudioEnabled);

            view.querySelector('#DefaultMdblistEnabled').checked = !!defaults.mdblistEnabled;
            view.querySelector('#DefaultTmdbEpisodeRatingsEnabled').checked = !!defaults.tmdbEpisodeRatingsEnabled;
            setNullableBoolSelect(view, '#DefaultMdblistShowRatingBadges', defaults.mdblistShowRatingBadges);
            loadRatingSourcesPicker(view, defaults.mdblistRatingSources || null);
            setNullableBoolSelect(view, '#DefaultSeerrBlockNsfw', defaults.seerrBlockNsfw);

            var sourceSelect = view.querySelector('#DefaultMediaBarSourceType');
            var collectionPickerSection = view.querySelector('#DefaultCollectionPickerSection');
            var libraryPickerSection = view.querySelector('#DefaultLibraryPickerSection');
            function togglePickerVisibility() {
                var isCollection = sourceSelect.value === 'collection';
                var isLibrary = sourceSelect.value === 'library';
                collectionPickerSection.style.display = isCollection ? '' : 'none';
                libraryPickerSection.style.display = isLibrary ? '' : 'none';
                if (isCollection) loadAdminCollectionPicker(view, defaults.mediaBarCollectionIds || []);
                if (isLibrary) loadAdminLibraryPicker(view, defaults.mediaBarLibraryIds || []);
            }
            if (!sourceSelect.dataset.bound) {
                sourceSelect.dataset.bound = 'true';
                sourceSelect.addEventListener('change', togglePickerVisibility);
            }
            togglePickerVisibility();
            loadAdminGenrePicker(view, defaults.mediaBarExcludedGenres || []);

            loading.hide();
        });
    }

    function saveConfig(view) {
        loading.show();
        return ApiClient.getPluginConfiguration(PluginUniqueId).then(function (config) {
            config.EnableSettingsSync = view.querySelector('#EnableSettingsSync').checked;
            config.SeerrEnabled = view.querySelector('#SeerrEnabled').checked;
            config.SeerrUrl = view.querySelector('#SeerrUrl').value || null;
            config.SeerrDisplayName = view.querySelector('#SeerrDisplayName').value || null;
            config.MdblistApiKey = view.querySelector('#MdblistApiKey').value || null;
            config.TmdbApiKey = view.querySelector('#TmdbApiKey').value || null;
            config.ImdbListsEnabled = view.querySelector('#ImdbListsEnabled').checked;
            config.StudioLogosEnabled = view.querySelector('#StudioLogosEnabled').checked;
            config.FcmServiceAccountJson = view.querySelector('#FcmServiceAccountJson').value || null;
            config.FcmServiceAccountPath = view.querySelector('#FcmServiceAccountPath').value || null;
            config.WebDefaultServerUrl = view.querySelector('#WebDefaultServerUrl').value || null;
            config.WebForcedServerUrl = view.querySelector('#WebForcedServerUrl').value || null;
            config.WebEnableWebRtcScan = view.querySelector('#WebEnableWebRtcScan').checked;

            config.GamesEnabled = view.querySelector('#GamesEnabled').checked;
            config.GameLibraryIds = Array.prototype.slice.call(view.querySelectorAll('.gameLibraryCb:checked'))
                .map(function (cb) { return cb.getAttribute('data-id'); });
            config.GamesCoreZipUrl = view.querySelector('#GamesCoreZipUrl').value || null;

            var d = camelKeysDeep(config.DefaultUserSettings) || {};
            d.visualTheme = view.querySelector('#DefaultVisualTheme').value || null;
            d.detailScreenStyle = view.querySelector('#DefaultDetailScreenStyle').value || null;
            d.detailExpandedTabs = getNullableBoolSelect(view, '#DefaultDetailExpandedTabs');
            d.focusColor = view.querySelector('#DefaultFocusColor').value || null;
            d.watchedIndicator = view.querySelector('#DefaultWatchedIndicator').value || null;
            d.cardFocusExpansion = getNullableBoolSelect(view, '#DefaultCardFocusExpansion');
            d.screensaverMode = view.querySelector('#DefaultScreensaverMode').value || null;

            d.navbarPosition = view.querySelector('#DefaultNavbarPosition').value || null;
            d.navbarColor = view.querySelector('#DefaultNavbarColor').value || null;
            d.navbarOpacity = getNullableIntInput(view, '#DefaultNavbarOpacity');

            d.mediaBarSourceType = view.querySelector('#DefaultMediaBarSourceType').value || null;
            d.mediaBarMode = view.querySelector('#DefaultMediaBarMode').value || null;
            d.mediaBarTrailerAudio = getNullableBoolSelect(view, '#DefaultMediaBarTrailerAudio');

            var collectionIds = Array.prototype.slice.call(view.querySelectorAll('.adminCollectionCb:checked')).map(function (cb) { return cb.dataset.id; });
            d.mediaBarCollectionIds = collectionIds.length > 0 ? collectionIds : null;
            var libraryIds = Array.prototype.slice.call(view.querySelectorAll('.adminLibraryCb:checked')).map(function (cb) { return cb.dataset.id; });
            d.mediaBarLibraryIds = libraryIds.length > 0 ? libraryIds : null;
            var genreIds = Array.prototype.slice.call(view.querySelectorAll('.adminGenreCb:checked')).map(function (cb) { return cb.dataset.id; });
            d.mediaBarExcludedGenres = genreIds.length > 0 ? genreIds : null;

            d.homeRowsStyle = view.querySelector('#DefaultHomeRowsStyle').value || null;
            d.fullScreenRows = getNullableBoolSelect(view, '#DefaultFullScreenRows');
            d.useDetailedSubHeadings = getNullableBoolSelect(view, '#DefaultUseDetailedSubHeadings');
            d.homeImageTypeContinueWatching = view.querySelector('#DefaultHomeImageTypeContinueWatching').value || null;
            d.posterSize = view.querySelector('#DefaultPosterSize').value || null;
            d.displayFavoritesRows = getNullableBoolSelect(view, '#DefaultDisplayFavoritesRows');
            d.displayCollectionsRows = getNullableBoolSelect(view, '#DefaultDisplayCollectionsRows');
            d.displayGenresRows = getNullableBoolSelect(view, '#DefaultDisplayGenresRows');
            d.displayPlaylistsRows = getNullableBoolSelect(view, '#DefaultDisplayPlaylistsRows');
            d.displayAudioRows = getNullableBoolSelect(view, '#DefaultDisplayAudioRows');
            d.favoritesRowSortBy = view.querySelector('#DefaultFavoritesRowSortBy').value || null;
            d.collectionsRowSortBy = view.querySelector('#DefaultCollectionsRowSortBy').value || null;
            d.genresRowSortBy = view.querySelector('#DefaultGenresRowSortBy').value || null;
            d.genresRowItemFilter = view.querySelector('#DefaultGenresRowItemFilter').value || null;
            d.homeImageUseSeriesImage = getNullableBoolSelect(view, '#DefaultHomeImageUseSeriesImage');
            var homeSections = getHomeSectionsValue(view);
            d.homeSections = homeSections;
            d.homeRowOrder = getHomeRowOrderValue(view, homeSections);
            d.mergeContinueWatchingNextUp = view.querySelector('#DefaultMergeContinueWatchingNextUp').checked;

            d.showShuffleButton = view.querySelector('#DefaultShowShuffleButton').checked;
            d.showGenresButton = view.querySelector('#DefaultShowGenresButton').checked;
            d.showFavoritesButton = view.querySelector('#DefaultShowFavoritesButton').checked;
            d.showCastButton = view.querySelector('#DefaultShowCastButton').checked;
            d.showSyncPlayButton = view.querySelector('#DefaultShowSyncPlayButton').checked;
            d.showLibrariesInToolbar = view.querySelector('#DefaultShowLibrariesInToolbar').checked;

            d.mediaBarTrailerPreview = getNullableBoolSelect(view, '#DefaultMediaBarTrailerPreview');
            d.episodePreviewEnabled = getNullableBoolSelect(view, '#DefaultEpisodePreviewEnabled');
            d.previewAudioEnabled = getNullableBoolSelect(view, '#DefaultPreviewAudioEnabled');

            d.mdblistEnabled = view.querySelector('#DefaultMdblistEnabled').checked;
            d.tmdbEpisodeRatingsEnabled = view.querySelector('#DefaultTmdbEpisodeRatingsEnabled').checked;
            d.mdblistShowRatingBadges = getNullableBoolSelect(view, '#DefaultMdblistShowRatingBadges');
            d.mdblistRatingSources = getRatingSourcesValue(view);
            d.seerrBlockNsfw = getNullableBoolSelect(view, '#DefaultSeerrBlockNsfw');

            config.DefaultUserSettings = d;

            return ApiClient.updatePluginConfiguration(PluginUniqueId, config).then(function (result) {
                Dashboard.processPluginConfigurationUpdateResult(result);
            });
        });
    }

    function pushDefaults(view) {
        if (!window.confirm('Apply the configured defaults to all existing users now?')) return;
        var result = view.querySelector('#PushDefaultsResult');
        var btn = view.querySelector('#PushDefaultsBtn');
        var serverUrl = ApiClient.serverAddress ? ApiClient.serverAddress() : '';
        btn.disabled = true;
        if (result) result.style.display = 'none';
        loading.show();
        // Persist the current form first so the defaults pushed to users reflect unsaved edits.
        saveConfig(view)
            .then(function () {
                return fetch(serverUrl + '/Moonfin/Admin/PushDefaults', { method: 'POST', headers: moonfinAuthHeaders(), body: '{}' });
            })
            .then(parseJsonResponse)
            .then(function (payload) {
                var usersUpdated = payload.usersUpdated != null ? payload.usersUpdated : (payload.usersAffected != null ? payload.usersAffected : payload.UsersAffected);
                if (result) { result.style.display = ''; result.style.color = '#52b54b'; result.textContent = 'Defaults applied to ' + (usersUpdated || 0) + ' user(s).'; }
            })
            .catch(function (error) {
                if (result) { result.style.display = ''; result.style.color = '#d9534f'; result.textContent = error && error.message ? error.message : 'Request failed. Check server logs.'; }
            })
            .finally(function () { btn.disabled = false; loading.hide(); });
    }

    function broadcast(view) {
        var input = view.querySelector('#BroadcastMessageText');
        var result = view.querySelector('#BroadcastMessageResult');
        var btn = view.querySelector('#BroadcastMessageBtn');
        var message = (input && input.value ? input.value : '').trim();
        if (!message) {
            if (result) { result.style.display = ''; result.style.color = '#d9534f'; result.textContent = 'Please enter a message.'; }
            return;
        }
        var serverUrl = ApiClient.serverAddress ? ApiClient.serverAddress() : '';
        btn.disabled = true;
        if (result) result.style.display = 'none';
        loading.show();
        fetch(serverUrl + '/Moonfin/Broadcast', { method: 'POST', headers: moonfinAuthHeaders(), body: JSON.stringify({ message: message }) })
            .then(parseJsonResponse)
            .then(function (payload) {
                var deliveries = payload.deliveries != null ? payload.deliveries : payload.Deliveries;
                if (result) { result.style.display = ''; result.style.color = '#52b54b'; result.textContent = 'Message sent to ' + (deliveries || 0) + ' active stream(s).'; }
                if (input) input.value = '';
            })
            .catch(function (error) {
                if (result) { result.style.display = ''; result.style.color = '#d9534f'; result.textContent = error && error.message ? error.message : 'Request failed. Check server logs.'; }
            })
            .finally(function () { btn.disabled = false; loading.hide(); });
    }

    function bindOnce(view) {
        if (view.__moonfinBound) return;
        view.__moonfinBound = true;
        view.__moonfinState = { timer: null };

        initializeAdminTabs(view);

        var form = view.querySelector('#MoonfinConfigForm');
        if (form) {
            form.addEventListener('submit', function (e) {
                e.preventDefault();
                saveConfig(view);
                return false;
            });
        }

        var themeChooseButton = view.querySelector('#AdminThemeChooseBtn');
        var themeFileInput = view.querySelector('#AdminThemeFileInput');
        var themeUploadButton = view.querySelector('#AdminThemeUploadBtn');
        var themesListContainer = view.querySelector('#AdminThemesList');

        if (themeChooseButton) {
            themeChooseButton.addEventListener('click', function () { if (themeFileInput) themeFileInput.click(); });
        }
        if (themeFileInput) {
            themeFileInput.addEventListener('change', function () {
                var file = themeFileInput.files && themeFileInput.files.length ? themeFileInput.files[0] : null;
                setSelectedThemeFileLabel(view, file);
                if (themeUploadButton) themeUploadButton.disabled = !file;
            });
        }
        if (themeUploadButton) {
            themeUploadButton.addEventListener('click', function () { uploadSelectedThemeFile(view); });
        }
        if (themesListContainer) {
            themesListContainer.addEventListener('click', function (event) {
                var button = event.target.closest('.adminThemeDeleteBtn');
                if (button) deleteUploadedTheme(view, button.getAttribute('data-theme-id') || '');
            });
        }

        var pushDefaultsBtn = view.querySelector('#PushDefaultsBtn');
        if (pushDefaultsBtn) pushDefaultsBtn.addEventListener('click', function () { pushDefaults(view); });

        var broadcastBtn = view.querySelector('#BroadcastMessageBtn');
        if (broadcastBtn) broadcastBtn.addEventListener('click', function () { broadcast(view); });
    }

    function View(view, params) {
        BaseView.apply(this, arguments);
        bindOnce(view);
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        var view = this.view;
        clearSelectedThemeFile(view);
        setThemeUploadResult(view, '', '');
        loadAdminThemesList(view);
        loadConfig(view);
    };

    View.prototype.onPause = function () {
        var view = this.view;
        if (view.__moonfinState && view.__moonfinState.timer) {
            clearInterval(view.__moonfinState.timer);
            view.__moonfinState.timer = null;
        }
        BaseView.prototype.onPause.apply(this, arguments);
    };

    return View;
});
