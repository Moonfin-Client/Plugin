define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select', 'emby-scroller'], function (BaseView, loading) {
    'use strict';

    var PluginUniqueId = '9a1b2c3d-4e5f-6789-abcd-ef0123456789';

    function esc(str) {
        return String(str || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

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
        { id: 'rogerEbert', label: 'Roger Ebert' }
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

    function setNullableRangeInput(view, selectorId, value, unit) {
        var select = view.querySelector(selectorId + '_Set');
        var range = view.querySelector(selectorId);
        var display = view.querySelector(selectorId + '_Val');
        if (!range) return;

        unit = unit || '';

        if (value == null || value === '') {
            if (select) select.value = 'unset';
            range.disabled = true;
            if (display) display.textContent = '--';
        } else {
            if (select) select.value = 'set';
            range.disabled = false;
            range.value = String(value);
            if (display) display.textContent = String(value) + unit;
        }
    }

    function getNullableRangeInput(view, selectorId) {
        var select = view.querySelector(selectorId + '_Set');
        var range = view.querySelector(selectorId);
        if (!range) return null;
        if (select && select.value === 'unset') return null;
        if (range.disabled) return null;
        var parsed = parseInt(range.value, 10);
        return isNaN(parsed) ? null : parsed;
    }

    // The two blur settings are text on the profile, and a JSON number will not
    // deserialize into a string property, so the whole save fails rather than
    // that one value being dropped.
    function getNullableRangeInputAsText(view, selectorId) {
        var parsed = getNullableRangeInput(view, selectorId);
        return parsed == null ? null : String(parsed);
    }

    function bindNullableRangeInput(view, selectorId, unit) {
        var select = view.querySelector(selectorId + '_Set');
        var range = view.querySelector(selectorId);
        var display = view.querySelector(selectorId + '_Val');
        if (!range || !select) return;

        unit = unit || '';

        if (!select.dataset.rangeBound) {
            select.dataset.rangeBound = 'true';
            select.addEventListener('change', function() {
                if (select.value === 'unset') {
                    range.disabled = true;
                    if (display) display.textContent = '--';
                } else {
                    range.disabled = false;
                    if (display) display.textContent = range.value + unit;
                }
            });
        }

        if (!range.dataset.rangeBound) {
            range.dataset.rangeBound = 'true';
            range.addEventListener('input', function() {
                if (display) display.textContent = range.value + unit;
            });
        }
    }

    // ── Tabbed navigation ───────────────────────────────────────────────────

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

    // ISO 639-2 codes, matching the table Core resolves track languages against.
    // The audio and subtitle pickers share the list so the two stay in sync.
    var LANGUAGE_OPTIONS = [
        ['auto', 'Server Default'], ['eng', 'English'], ['spa', 'Spanish'],
        ['fra', 'French'], ['deu', 'German'], ['ita', 'Italian'],
        ['jpn', 'Japanese'], ['kor', 'Korean'], ['zho', 'Chinese'],
        ['rus', 'Russian'], ['por', 'Portuguese'], ['ara', 'Arabic'],
        ['hin', 'Hindi'], ['nld', 'Dutch'], ['pol', 'Polish'],
        ['swe', 'Swedish'], ['nor', 'Norwegian'], ['dan', 'Danish'],
        ['fin', 'Finnish'], ['tha', 'Thai'], ['tur', 'Turkish'],
        ['heb', 'Hebrew'], ['ces', 'Czech'], ['ell', 'Greek'],
        ['ron', 'Romanian'], ['hun', 'Hungarian'], ['ukr', 'Ukrainian'],
        ['ind', 'Indonesian'], ['vie', 'Vietnamese']
    ];

    function fillLanguageSelect(view, selector) {
        var select = view.querySelector(selector);
        if (!select || select.dataset.filled) return;
        select.dataset.filled = 'true';
        var html = '<option value="">Not set (user decides)</option>';
        for (var i = 0; i < LANGUAGE_OPTIONS.length; i++) {
            var code = LANGUAGE_OPTIONS[i][0];
            var label = LANGUAGE_OPTIONS[i][1];
            html += '<option value="' + esc(code) + '">'
                + esc(code === 'auto' ? label : label + ' (' + code + ')') + '</option>';
        }
        select.innerHTML = html;
    }

    // Same ids and declaration order as DetailButton in Core, so an untouched
    // arrangement here matches what a fresh client shows.
    var DETAIL_BUTTONS = [
        { id: 'seerrRequest', label: 'Request' },
        { id: 'seerrRequest4k', label: 'Request 4K' },
        { id: 'shuffle', label: 'Shuffle' },
        { id: 'restart', label: 'Restart', canHide: false },
        { id: 'playOffline', label: 'Play Offline', canHide: false },
        { id: 'audio', label: 'Audio' },
        { id: 'subtitles', label: 'Subtitles' },
        { id: 'version', label: 'Version' },
        { id: 'cast', label: 'Cast' },
        { id: 'trailer', label: 'Trailer' },
        { id: 'watchWithGroup', label: 'Watch with group' },
        { id: 'watched', label: 'Watched' },
        { id: 'favorite', label: 'Favorite' },
        { id: 'playlist', label: 'Playlist' },
        { id: 'download', label: 'Download' },
        { id: 'deleteFiles', label: 'Delete files' },
        { id: 'goToSeries', label: 'Go to series' },
        { id: 'seerrWatchlist', label: 'Watchlist' },
        { id: 'seerrReportIssue', label: 'Report Issue' },
        { id: 'seerrManage', label: 'Manage Requests' },
        { id: 'admin', label: 'Admin' }
    ];

    function loadDetailButtonsPicker(view, order, hidden) {
        var container = view.querySelector('#DefaultDetailButtonsList');
        if (!container) return;
        var hiddenSet = {};
        if (hidden && Array.isArray(hidden)) {
            hidden.forEach(function (id) { hiddenSet[id] = true; });
        }

        var ordered = [];
        var addedSet = {};

        function entryFor(btn) {
            var hideable = btn.canHide !== false;
            return {
                id: btn.id,
                label: btn.label,
                hideable: hideable,
                checked: hideable ? !hiddenSet[btn.id] : true
            };
        }

        if (order && Array.isArray(order) && order.length > 0) {
            order.forEach(function (id) {
                var btn = DETAIL_BUTTONS.find(function (b) { return b.id === id; });
                if (btn) {
                    ordered.push(entryFor(btn));
                    addedSet[btn.id] = true;
                }
            });
        }

        DETAIL_BUTTONS.forEach(function (btn) {
            if (!addedSet[btn.id]) {
                ordered.push(entryFor(btn));
            }
        });

        container.innerHTML = '';
        ordered.forEach(function (item) {
            var row = document.createElement('div');
            row.className = 'detailButtonItem';
            row.dataset.id = item.id;
            row.style.cssText = 'display:flex;align-items:center;gap:8px;padding:5px 8px;border-radius:4px;margin-bottom:2px;background:rgba(128,128,128,0.03);';
            row.innerHTML =
                (item.hideable
                    ? '<input type="checkbox"' + (item.checked ? ' checked' : '') + ' style="accent-color:#00a4dc;width:16px;height:16px;flex-shrink:0;">'
                    : '<span title="Always shown" style="width:16px;height:16px;flex-shrink:0;text-align:center;color:rgba(128,128,128,0.35);">&#x2713;</span>') +
                '<span style="flex:1;font-size:0.9em;color:rgba(128,128,128,0.9);">' + esc(item.label) + '</span>' +
                '<button type="button" class="detailButtonMoveBtn" data-dir="up" style="background:none;border:1px solid rgba(128,128,128,0.2);border-radius:3px;color:rgba(128,128,128,0.7);padding:1px 6px;cursor:pointer;font-size:0.85em;">&#x2191;</button>' +
                '<button type="button" class="detailButtonMoveBtn" data-dir="down" style="background:none;border:1px solid rgba(128,128,128,0.2);border-radius:3px;color:rgba(128,128,128,0.7);padding:1px 6px;cursor:pointer;font-size:0.85em;">&#x2193;</button>';
            container.appendChild(row);
        });

        if (!container.dataset.hasListener) {
            container.dataset.hasListener = 'true';
            container.addEventListener('click', function (e) {
                var btn = e.target.closest('.detailButtonMoveBtn');
                if (!btn) return;
                var row = btn.closest('.detailButtonItem');
                if (!row) return;
                if (btn.dataset.dir === 'up' && row.previousElementSibling) {
                    container.insertBefore(row, row.previousElementSibling);
                } else if (btn.dataset.dir === 'down' && row.nextElementSibling) {
                    container.insertBefore(row.nextElementSibling, row);
                }
            });
        }
    }

    function getDetailButtonsValue(view) {
        var items = view.querySelectorAll('#DefaultDetailButtonsList .detailButtonItem');
        var order = [];
        var hidden = [];
        items.forEach(function (item) {
            var cb = item.querySelector('input[type=checkbox]');
            var id = item.dataset.id;
            order.push(id);
            if (cb && !cb.checked) {
                hidden.push(id);
            }
        });
        return {
            order: order.length > 0 ? order : null,
            hidden: hidden.length > 0 ? hidden : null
        };
    }

    var SEERR_DISCOVERY_ROWS = [
        { id: 'shortcuts', label: 'Seerr Browse' },
        { id: 'recent_requests', label: 'Recent Requests' },
        { id: 'watchlist', label: 'Your Watchlist' },
        { id: 'recently_added', label: 'Recently Added' },
        { id: 'trending', label: 'Trending' },
        { id: 'popular_movies', label: 'Popular Movies' },
        { id: 'movie_genres', label: 'Movie Genres' },
        { id: 'upcoming_movies', label: 'Upcoming Movies' },
        { id: 'studios', label: 'Studios' },
        { id: 'popular_series', label: 'Popular Series' },
        { id: 'series_genres', label: 'Series Genres' },
        { id: 'upcoming_series', label: 'Upcoming Series' },
        { id: 'networks', label: 'Networks' }
    ];

    function loadSeerrDiscoveryPicker(view, order, hidden) {
        var container = view.querySelector('#DefaultSeerrDiscoveryList');
        if (!container) return;
        var hiddenSet = {};
        if (hidden && Array.isArray(hidden)) {
            hidden.forEach(function (id) { hiddenSet[id] = true; });
        }

        var ordered = [];
        var addedSet = {};

        if (order && Array.isArray(order) && order.length > 0) {
            order.forEach(function (id) {
                var row = SEERR_DISCOVERY_ROWS.find(function (r) { return r.id === id; });
                if (row) {
                    ordered.push({ id: row.id, label: row.label, checked: !hiddenSet[row.id] });
                    addedSet[row.id] = true;
                }
            });
        }

        SEERR_DISCOVERY_ROWS.forEach(function (row) {
            if (!addedSet[row.id]) {
                ordered.push({ id: row.id, label: row.label, checked: !hiddenSet[row.id] });
            }
        });

        container.innerHTML = '';
        ordered.forEach(function (item) {
            var row = document.createElement('div');
            row.className = 'seerrDiscoveryItem';
            row.dataset.id = item.id;
            row.style.cssText = 'display:flex;align-items:center;gap:8px;padding:5px 8px;border-radius:4px;margin-bottom:2px;background:rgba(128,128,128,0.03);';
            row.innerHTML =
                '<input type="checkbox"' + (item.checked ? ' checked' : '') + ' style="accent-color:#00a4dc;width:16px;height:16px;flex-shrink:0;">' +
                '<span style="flex:1;font-size:0.9em;color:rgba(128,128,128,0.9);">' + esc(item.label) + '</span>' +
                '<button type="button" class="seerrMoveBtn" data-dir="up" style="background:none;border:1px solid rgba(128,128,128,0.2);border-radius:3px;color:rgba(128,128,128,0.7);padding:1px 6px;cursor:pointer;font-size:0.85em;">&#x2191;</button>' +
                '<button type="button" class="seerrMoveBtn" data-dir="down" style="background:none;border:1px solid rgba(128,128,128,0.2);border-radius:3px;color:rgba(128,128,128,0.7);padding:1px 6px;cursor:pointer;font-size:0.85em;">&#x2193;</button>';
            container.appendChild(row);
        });

        if (!container.dataset.hasListener) {
            container.dataset.hasListener = 'true';
            container.addEventListener('click', function (e) {
                var btn = e.target.closest('.seerrMoveBtn');
                if (!btn) return;
                var row = btn.closest('.seerrDiscoveryItem');
                if (!row) return;
                if (btn.dataset.dir === 'up' && row.previousElementSibling) {
                    container.insertBefore(row, row.previousElementSibling);
                } else if (btn.dataset.dir === 'down' && row.nextElementSibling) {
                    container.insertBefore(row.nextElementSibling, row);
                }
            });
        }
    }

    // Clients carry Seerr rows as seerrRows.rowOrder, which lists the visible rows in
    // order and treats every row it leaves out as switched off.
    function getSeerrDiscoveryRowOrder(view) {
        var items = view.querySelectorAll('#DefaultSeerrDiscoveryList .seerrDiscoveryItem');
        var rowOrder = [];
        items.forEach(function (item) {
            var cb = item.querySelector('input[type=checkbox]');
            if (!cb || cb.checked) {
                rowOrder.push(item.dataset.id);
            }
        });
        return rowOrder.length > 0 ? rowOrder : null;
    }

    // Turns a stored rowOrder back into the picker's order plus hidden pair.
    function seerrRowsToPicker(seerrRows) {
        var rowOrder = seerrRows && Array.isArray(seerrRows.rowOrder) ? seerrRows.rowOrder : null;
        if (!rowOrder || rowOrder.length === 0) return { order: null, hidden: null };
        var visible = {};
        rowOrder.forEach(function (id) { visible[id] = true; });
        var hidden = SEERR_DISCOVERY_ROWS
            .filter(function (row) { return !visible[row.id]; })
            .map(function (row) { return row.id; });
        return { order: rowOrder, hidden: hidden.length > 0 ? hidden : null };
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
        { type: 'seerr_shortcuts', label: 'Seerr Browse' },
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
        { type: 'seerr_networks', label: 'Seerr Networks' },
        { type: 'seerr_watchlist', label: 'Seerr Watchlist' }
    ];

    var HOME_LAYOUT_TABS = [
        { id: 'builtin', label: 'Basics' },
        { id: 'collections', label: 'Collections' },
        { id: 'playlists', label: 'Playlists' },
        { id: 'genres', label: 'Genres' },
        { id: 'seerr', label: 'Seerr' }
    ];

    var HOME_LAYOUT_SEERR_TYPES = {
        seerr_shortcuts: true,
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
        seerr_networks: true,
        seerr_watchlist: true
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
            if (!section) return;
            var normalized;
            if (section.kind === 'pluginDynamic') {
                normalized = {
                    kind: 'pluginDynamic',
                    type: 'none',
                    enabled: section.enabled !== false,
                    order: ordered.length,
                    serverId: section.serverId || '',
                    pluginSource: section.pluginSource || 'hss',
                    pluginSection: section.pluginSection || '',
                    pluginAdditionalData: section.pluginAdditionalData || '',
                    pluginDisplayText: section.pluginDisplayText || section.pluginSection || 'Dynamic row'
                };
            } else {
                if (section.enabled === false) return;
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
            var isDynamic = section.kind === 'pluginDynamic';
            var result = {
                type: isDynamic ? 'none' : section.type,
                // Dynamic rows stay in the editor when disabled, unlike builtins, so writing
                // them all back as enabled would switch every custom row on.
                enabled: isDynamic ? section.enabled !== false : true,
                order: section.order
            };
            if (isDynamic) {
                result.kind = 'pluginDynamic';
                result.pluginSource = section.pluginSource || 'hss';
                // Custom rows have no server of their own and the client keys them on this,
                // so send the placeholder it uses rather than nothing.
                if (section.serverId) result.serverId = section.serverId;
                else if (result.pluginSource === 'custom') result.serverId = 'custom';
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
        if (!statusEl) return;
        var serverUrl = ApiClient.serverAddress ? ApiClient.serverAddress() : '';
        fetch(serverUrl + '/Moonfin/Games/Cores/Status', { headers: moonfinAuthHeaders() })
            .then(function (r) { return r.json(); })
            .then(function (s) {
                if (s.downloading) {
                    statusEl.textContent = 'Downloading cores... (' + (s.filesInstalled || 0) + ' files so far)';
                    if (!state.timer) state.timer = setInterval(function () { refreshCoresStatus(view, state); }, 4000);
                    return;
                }
                if (state.timer) { clearInterval(state.timer); state.timer = null; }
                if (s.installed) { statusEl.textContent = 'Installed on server (offline ready).'; }
                else if (s.state === 'failed') { statusEl.textContent = 'Download failed: ' + (s.error || 'unknown error'); }
                else { statusEl.textContent = 'Using EmulatorJS CDN (no local cores).'; }
            })
            .catch(function () { statusEl.textContent = ''; });
    }

    function initGameCores(view, state) {
        var uploadBtn = view.querySelector('#GameCoresUploadBtn');
        if (uploadBtn && uploadBtn.dataset.wired !== '1') {
            uploadBtn.dataset.wired = '1';
            uploadBtn.addEventListener('click', function () {
                var fileInput = view.querySelector('#GameCoresFile');
                var statusEl = view.querySelector('#GameCoresStatus');
                var file = fileInput && fileInput.files ? fileInput.files[0] : null;
                if (!file) { if (statusEl) statusEl.textContent = 'Choose a .zip file first.'; return; }
                var serverUrl = ApiClient.serverAddress ? ApiClient.serverAddress() : '';
                var headers = moonfinAuthHeaders();
                headers['Content-Type'] = 'application/octet-stream';
                if (statusEl) statusEl.textContent = 'Uploading cores zip... (this can take a while)';
                uploadBtn.disabled = true;
                fetch(serverUrl + '/Moonfin/Games/Cores/Upload', { method: 'POST', headers: headers, body: file })
                    .then(function () { refreshCoresStatus(view, state); })
                    .catch(function () { if (statusEl) statusEl.textContent = 'Upload failed.'; })
                    .finally(function () { uploadBtn.disabled = false; });
            });
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

    // Load and save hide the spinner on their last line, so without this a
    // throw anywhere above leaves the page spinning with nothing on screen.
    function reportConfigError(error) {
        loading.hide();
        var message = error && error.message ? error.message : 'The Moonfin settings page hit an error. Check the browser console.';
        if (Dashboard.alert) Dashboard.alert(message);
        console.error('Moonfin config page error:', error);
    }

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
            view.querySelector('#PushRelayUrl').value = config.PushRelayUrl || '';
            view.querySelector('#PushRelayAppKey').value = config.PushRelayAppKey || '';
            view.querySelector('#WebDefaultServerUrl').value = config.WebDefaultServerUrl || '';
            view.querySelector('#WebForcedServerUrl').value = config.WebForcedServerUrl || '';
            view.querySelector('#WebEnableWebRtcScan').checked = config.WebEnableWebRtcScan !== false;

            view.querySelector('#GamesEnabled').checked = config.GamesEnabled === true;
            loadGameLibraryPicker(view, config.GameLibraryIds || []);
            initGameCores(view, view.__moonfinState);

            var defaults = camelKeysDeep(config.DefaultUserSettings) || {};
            setSelectValue(view, '#DefaultInterfaceStyle', defaults.interfaceStyle, 'Configured style');
            setSelectValue(view, '#DefaultVisualTheme', defaults.visualTheme, 'Configured theme');
            setSelectValue(view, '#DefaultDetailScreenStyle', defaults.detailScreenStyle, 'Configured style');
            setNullableBoolSelect(view, '#DefaultDetailExpandedTabs', defaults.detailExpandedTabs);
            setNullableBoolSelect(view, '#DefaultDetailShowTechnicalDetails', defaults.detailShowTechnicalDetails);
            setSelectValue(view, '#DefaultRecommendationSystemSource', defaults.recommendationSystemSource, 'Configured source');
            setNullableBoolSelect(view, '#DefaultRecommendationsApplyParentalRatingCap', defaults.recommendationsApplyParentalRatingCap);
            // One picker stands in for all three form factors.
            loadDetailButtonsPicker(view,
                defaults.detailButtonOrderTv || defaults.detailButtonOrderMobile || defaults.detailButtonOrderDesktop || null,
                defaults.hiddenDetailButtonsTv || defaults.hiddenDetailButtonsMobile || defaults.hiddenDetailButtonsDesktop || null);
            setSelectValue(view, '#DefaultFocusColor', defaults.focusColor, 'Configured color');
            setSelectValue(view, '#DefaultClockBehavior', defaults.clockBehavior, 'Configured clock');
            setNullableBoolSelect(view, '#DefaultUse24HourClock', defaults.use24HourClock);
            setSelectValue(view, '#DefaultDesktopUiScale', defaults.desktopUiScale, 'Configured scale');
            setNullableBoolSelect(view, '#DefaultBackdropEnabled', defaults.backdropEnabled);
            bindNullableRangeInput(view, '#DefaultBrowsingBlur');
            setNullableRangeInput(view, '#DefaultBrowsingBlur', defaults.browsingBlur);
            bindNullableRangeInput(view, '#DefaultDetailsScreenBlur');
            setNullableRangeInput(view, '#DefaultDetailsScreenBlur', defaults.detailsScreenBlur);
            setNullableBoolSelect(view, '#DefaultThemeMusicEnabled', defaults.themeMusicEnabled);
            setNullableBoolSelect(view, '#DefaultThemeMusicOnHomeRows', defaults.themeMusicOnHomeRows);
            setNullableBoolSelect(view, '#DefaultThemeMusicLoop', defaults.themeMusicLoop);
            bindNullableRangeInput(view, '#DefaultThemeMusicVolume', '%');
            setNullableRangeInput(view, '#DefaultThemeMusicVolume', defaults.themeMusicVolume, '%');
            setSelectValue(view, '#DefaultWatchedIndicator', defaults.watchedIndicator, 'Configured mode');
            setNullableBoolSelect(view, '#DefaultCardFocusExpansion', defaults.cardFocusExpansion);
            setSelectValue(view, '#DefaultScreensaverMode', defaults.screensaverMode, 'Configured mode');

            setSelectValue(view, '#DefaultNavbarPosition', defaults.navbarPosition, 'Configured position');
            setSelectValue(view, '#DefaultNavbarColor', defaults.navbarColor, 'Configured color');
            bindNullableRangeInput(view, '#DefaultNavbarOpacity', '%');
            setNullableRangeInput(view, '#DefaultNavbarOpacity', defaults.navbarOpacity, '%');
            setNullableBoolSelect(view, '#DefaultNavbarAlwaysExpanded', defaults.navbarAlwaysExpanded);
            setNullableBoolSelect(view, '#DefaultEnableFolderView', defaults.enableFolderView);
            setNullableBoolSelect(view, '#DefaultShowSeerrButton', defaults.showSeerrButton);
            setNullableBoolSelect(view, '#DefaultShowServerMessagesButton', defaults.showServerMessagesButton);

            setSelectValue(view, '#DefaultMediaBarSourceType', defaults.mediaBarSourceType, 'Configured source');
            loadAdminGenrePicker(view, defaults.mediaBarExcludedGenres || []);
            setSelectValue(view, '#DefaultMediaBarMode', defaults.mediaBarMode, 'Configured mode');
            setSelectValue(view, '#DefaultMediaBarContentType', defaults.mediaBarContentType, 'Configured content type');
            setSelectValue(view, '#DefaultMediaBarItemCount', defaults.mediaBarItemCount, 'Configured item count');
            setNullableBoolSelect(view, '#DefaultMediaBarAutoAdvance', defaults.mediaBarAutoAdvance);
            setSelectValue(view, '#DefaultMediaBarIntervalMs', defaults.mediaBarIntervalMs, 'Configured interval');
            setNullableBoolSelect(view, '#DefaultMediaBarTrailerPreview', defaults.mediaBarTrailerPreview);
            setNullableBoolSelect(view, '#DefaultMediaBarTrailerAudio', defaults.mediaBarTrailerAudio);
            setNullableBoolSelect(view, '#DefaultMediaBarTrailerCaptions', defaults.mediaBarTrailerCaptions);
            setNullableBoolSelect(view, '#DefaultEpisodePreviewEnabled', defaults.episodePreviewEnabled);
            setNullableBoolSelect(view, '#DefaultPreviewAudioEnabled', defaults.previewAudioEnabled);
            setSelectValue(view, '#DefaultSeasonalSurprise', defaults.seasonalSurprise, 'Configured surprise');

            setSelectValue(view, '#DefaultResumeSubtractDuration', defaults.resumeSubtractDuration != null ? String(defaults.resumeSubtractDuration) : '', 'Configured rewind');
            setSelectValue(view, '#DefaultUnpauseRewindDuration', defaults.unpauseRewindDuration != null ? String(defaults.unpauseRewindDuration) : '', 'Configured rewind');
            setSelectValue(view, '#DefaultSkipBackLength', defaults.skipBackLength != null ? String(defaults.skipBackLength) : '', 'Configured skip');
            setSelectValue(view, '#DefaultSkipForwardLength', defaults.skipForwardLength != null ? String(defaults.skipForwardLength) : '', 'Configured skip');

            fillLanguageSelect(view, '#DefaultDefaultAudioLanguage');
            fillLanguageSelect(view, '#DefaultDefaultSubtitleLanguage');
            setSelectValue(view, '#DefaultDefaultAudioLanguage', defaults.defaultAudioLanguage, 'Configured language');
            setNullableBoolSelect(view, '#DefaultPreferDefaultAudioTrack', defaults.preferDefaultAudioTrack);
            setNullableBoolSelect(view, '#DefaultPreferAudioDescription', defaults.preferAudioDescription);

            setSelectValue(view, '#DefaultSubtitleMode', defaults.subtitleMode, 'Configured mode');
            setSelectValue(view, '#DefaultDefaultSubtitleLanguage', defaults.defaultSubtitleLanguage, 'Configured language');
            setNullableBoolSelect(view, '#DefaultPreferSdhSubtitles', defaults.preferSdhSubtitles);

            setNullableBoolSelect(view, '#DefaultCinemaModeEnabled', defaults.cinemaModeEnabled);
            setNullableBoolSelect(view, '#DefaultAutoplayNextEpisode', defaults.autoplayNextEpisode);
            setSelectValue(view, '#DefaultNextUpTimeout', defaults.nextUpTimeout != null ? String(defaults.nextUpTimeout) : '', 'Configured timeout');
            setSelectValue(view, '#DefaultStillWatchingBehavior', defaults.stillWatchingBehavior, 'Configured behavior');

            setSelectValue(view, '#DefaultHomeRowsStyle', defaults.homeRowsStyle, 'Configured style');
            setNullableBoolSelect(view, '#DefaultFullScreenRows', defaults.fullScreenRows);
            setNullableBoolSelect(view, '#DefaultHomeRowInfoOverlay', defaults.homeRowInfoOverlay);
            bindNullableRangeInput(view, '#DefaultClassicHomeRowsPadding', 'px');
            setNullableRangeInput(view, '#DefaultClassicHomeRowsPadding', defaults.classicHomeRowsPadding, 'px');
            bindNullableRangeInput(view, '#DefaultModernHomeRowsPadding', 'px');
            setNullableRangeInput(view, '#DefaultModernHomeRowsPadding', defaults.modernHomeRowsPadding, 'px');
            setSelectValue(view, '#DefaultPosterSize', defaults.posterSize, 'Configured size');
            setSelectValue(view, '#DefaultHomeImageTypeContinueWatching', defaults.homeImageTypeContinueWatching, 'Configured image type');
            setSelectValue(view, '#DefaultPosterSize', defaults.posterSize, 'Configured size');
            setNullableBoolSelect(view, '#DefaultDisplayFavoritesRows', defaults.displayFavoritesRows);
            setSelectValue(view, '#DefaultFavoritesRowSortBy', defaults.favoritesRowSortBy, 'Configured sort');
            setSelectValue(view, '#DefaultFavoritesRowSortOrder', defaults.favoritesRowSortOrder, 'Configured sort');
            setNullableBoolSelect(view, '#DefaultDisplayCollectionsRows', defaults.displayCollectionsRows);
            setSelectValue(view, '#DefaultCollectionsRowSortBy', defaults.collectionsRowSortBy, 'Configured sort');
            setSelectValue(view, '#DefaultCollectionsRowSortOrder', defaults.collectionsRowSortOrder, 'Configured sort');
            setNullableBoolSelect(view, '#DefaultDisplayGenresRows', defaults.displayGenresRows);
            setSelectValue(view, '#DefaultGenresRowSortBy', defaults.genresRowSortBy, 'Configured sort');
            setSelectValue(view, '#DefaultGenresRowSortOrder', defaults.genresRowSortOrder, 'Configured sort');
            setNullableBoolSelect(view, '#DefaultDisplayPlaylistsRows', defaults.displayPlaylistsRows);
            setSelectValue(view, '#DefaultPlaylistsRowSortBy', defaults.playlistsRowSortBy, 'Configured sort');
            setSelectValue(view, '#DefaultPlaylistsRowSortOrder', defaults.playlistsRowSortOrder, 'Configured sort');
            setNullableBoolSelect(view, '#DefaultDisplayAudioRows', defaults.displayAudioRows);
            setSelectValue(view, '#DefaultAudioRowsSortBy', defaults.audioRowsSortBy, 'Configured sort');
            setSelectValue(view, '#DefaultAudioRowsSortOrder', defaults.audioRowsSortOrder, 'Configured sort');
            setNullableBoolSelect(view, '#DefaultDisplayStudiosRows', defaults.displayStudiosRows);
            setSelectValue(view, '#DefaultStudiosRowSortBy', defaults.studiosRowSortBy, 'Configured sort');
            setSelectValue(view, '#DefaultStudiosRowSortOrder', defaults.studiosRowSortOrder, 'Configured sort');
            setNullableBoolSelect(view, '#DefaultHomeImageUseSeriesImage', defaults.homeImageUseSeriesImage);
            setNullableBoolSelect(view, '#DefaultDisplayRewatchRow', defaults.displayRewatchRow);
            setSelectValue(view, '#DefaultRewatchSortBy', defaults.rewatchSortBy, 'Configured sort');
            setNullableBoolSelect(view, '#DefaultDisplaySinceYouWatchedRows', defaults.displaySinceYouWatchedRows);
            loadHomeSectionsEditor(view, defaults.homeSections || null, defaults.homeRowOrder || null);
            setNullableBoolSelect(view, '#DefaultMergeContinueWatchingNextUp', defaults.mergeContinueWatchingNextUp);
            setSelectValue(view, '#DefaultNextUpMaxDays', defaults.nextUpMaxDays, 'Configured max days');

            setNullableBoolSelect(view, '#DefaultEnableMultiServerLibraries', defaults.enableMultiServerLibraries);
            setNullableBoolSelect(view, '#DefaultMergeRecentRowsByType', defaults.mergeRecentRowsByType);
            setSelectValue(view, '#DefaultRecentlyReleasedSeriesType', defaults.recentlyReleasedSeriesType, 'Configured sort');
            setNullableBoolSelect(view, '#DefaultGroupItemsIntoCollections', defaults.groupItemsIntoCollections);
            setNullableBoolSelect(view, '#DefaultShowMediaDetailsOnLibraryPage', defaults.showMediaDetailsOnLibraryPage);
            setNullableBoolSelect(view, '#DefaultUseDetailedSubHeadings', defaults.useDetailedSubHeadings);
            setNullableBoolSelect(view, '#DefaultHideBackdropsInLibraries', defaults.hideBackdropsInLibraries);

            setNullableBoolSelect(view, '#DefaultShowShuffleButton', defaults.showShuffleButton);
            setNullableBoolSelect(view, '#DefaultShowGenresButton', defaults.showGenresButton);
            setNullableBoolSelect(view, '#DefaultShowFavoritesButton', defaults.showFavoritesButton);
            setNullableBoolSelect(view, '#DefaultShowLibrariesInToolbar', defaults.showLibrariesInToolbar);

            view.querySelector('#DefaultMdblistEnabled').checked = !!defaults.mdblistEnabled;
            view.querySelector('#DefaultTmdbEpisodeRatingsEnabled').checked = !!defaults.tmdbEpisodeRatingsEnabled;
            setNullableBoolSelect(view, '#DefaultMdblistShowRatingBadges', defaults.mdblistShowRatingBadges);
            loadRatingSourcesPicker(view, defaults.mdblistRatingSources || null);
            setNullableBoolSelect(view, '#DefaultSeerrBlockNsfw', defaults.seerrBlockNsfw);
            var seerrPicker = seerrRowsToPicker(defaults.seerrRows);
            loadSeerrDiscoveryPicker(view, seerrPicker.order, seerrPicker.hidden);

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

            loading.hide();
        }).catch(reportConfigError);
    }

    function saveConfig(view) {
        loading.show();
        return ApiClient.getPluginConfiguration(PluginUniqueId).then(function (config) {
            config.EnableSettingsSync = view.querySelector('#EnableSettingsSync').checked;
            config.SeerrEnabled = view.querySelector('#SeerrEnabled').checked;
            config.SeerrUrl = view.querySelector('#SeerrUrl').value || null;
            config.SeerrDisplayName = view.querySelector('#SeerrDisplayName').value || null;
            config.MdblistApiKey = (view.querySelector('#MdblistApiKey').value || '').trim() || null;
            config.TmdbApiKey = (view.querySelector('#TmdbApiKey').value || '').trim() || null;
            config.ImdbListsEnabled = view.querySelector('#ImdbListsEnabled').checked;
            config.StudioLogosEnabled = view.querySelector('#StudioLogosEnabled').checked;
            config.FcmServiceAccountJson = view.querySelector('#FcmServiceAccountJson').value || null;
            config.FcmServiceAccountPath = view.querySelector('#FcmServiceAccountPath').value || null;
            config.PushRelayUrl = (view.querySelector('#PushRelayUrl').value || '').trim()
                || 'https://push.moonfin.io/send';
            config.PushRelayAppKey = (view.querySelector('#PushRelayAppKey').value || '').trim() || null;
            config.WebDefaultServerUrl = view.querySelector('#WebDefaultServerUrl').value || null;
            config.WebForcedServerUrl = view.querySelector('#WebForcedServerUrl').value || null;
            config.WebEnableWebRtcScan = view.querySelector('#WebEnableWebRtcScan').checked;

            config.GamesEnabled = view.querySelector('#GamesEnabled').checked;
            config.GameLibraryIds = Array.prototype.slice.call(view.querySelectorAll('.gameLibraryCb:checked'))
                .map(function (cb) { return cb.getAttribute('data-id'); });

            var d = camelKeysDeep(config.DefaultUserSettings) || {};
            d.interfaceStyle = view.querySelector('#DefaultInterfaceStyle').value || null;
            d.visualTheme = view.querySelector('#DefaultVisualTheme').value || null;
            d.detailScreenStyle = view.querySelector('#DefaultDetailScreenStyle').value || null;
            d.detailExpandedTabs = getNullableBoolSelect(view, '#DefaultDetailExpandedTabs');
            d.detailShowTechnicalDetails = getNullableBoolSelect(view, '#DefaultDetailShowTechnicalDetails');
            d.recommendationSystemSource = view.querySelector('#DefaultRecommendationSystemSource').value || null;
            d.recommendationsApplyParentalRatingCap = getNullableBoolSelect(view, '#DefaultRecommendationsApplyParentalRatingCap');
            var btnVal = getDetailButtonsValue(view);
            // Clients read a different key per form factor, so one arrangement covers all three.
            d.detailButtonOrderTv = btnVal.order;
            d.detailButtonOrderMobile = btnVal.order;
            d.detailButtonOrderDesktop = btnVal.order;
            d.hiddenDetailButtonsTv = btnVal.hidden;
            d.hiddenDetailButtonsMobile = btnVal.hidden;
            d.hiddenDetailButtonsDesktop = btnVal.hidden;
            d.focusColor = view.querySelector('#DefaultFocusColor').value || null;
            d.clockBehavior = view.querySelector('#DefaultClockBehavior').value || null;
            d.use24HourClock = getNullableBoolSelect(view, '#DefaultUse24HourClock');
            d.desktopUiScale = view.querySelector('#DefaultDesktopUiScale').value || null;
            d.backdropEnabled = getNullableBoolSelect(view, '#DefaultBackdropEnabled');
            d.browsingBlur = getNullableRangeInputAsText(view, '#DefaultBrowsingBlur');
            d.detailsScreenBlur = getNullableRangeInputAsText(view, '#DefaultDetailsScreenBlur');
            d.themeMusicEnabled = getNullableBoolSelect(view, '#DefaultThemeMusicEnabled');
            d.themeMusicOnHomeRows = getNullableBoolSelect(view, '#DefaultThemeMusicOnHomeRows');
            d.themeMusicLoop = getNullableBoolSelect(view, '#DefaultThemeMusicLoop');
            d.themeMusicVolume = getNullableRangeInput(view, '#DefaultThemeMusicVolume');
            d.watchedIndicator = view.querySelector('#DefaultWatchedIndicator').value || null;
            d.cardFocusExpansion = getNullableBoolSelect(view, '#DefaultCardFocusExpansion');
            d.screensaverMode = view.querySelector('#DefaultScreensaverMode').value || null;

            d.navbarPosition = view.querySelector('#DefaultNavbarPosition').value || null;
            d.navbarColor = view.querySelector('#DefaultNavbarColor').value || null;
            d.navbarOpacity = getNullableRangeInput(view, '#DefaultNavbarOpacity');
            d.navbarAlwaysExpanded = getNullableBoolSelect(view, '#DefaultNavbarAlwaysExpanded');
            d.enableFolderView = getNullableBoolSelect(view, '#DefaultEnableFolderView');
            d.showSeerrButton = getNullableBoolSelect(view, '#DefaultShowSeerrButton');
            d.showServerMessagesButton = getNullableBoolSelect(view, '#DefaultShowServerMessagesButton');

            d.mediaBarSourceType = view.querySelector('#DefaultMediaBarSourceType').value || null;
            var genreIds = Array.prototype.slice.call(view.querySelectorAll('.adminGenreCb:checked')).map(function (cb) { return cb.dataset.id; });
            d.mediaBarExcludedGenres = genreIds.length > 0 ? genreIds : null;
            d.mediaBarMode = view.querySelector('#DefaultMediaBarMode').value || null;
            d.mediaBarContentType = view.querySelector('#DefaultMediaBarContentType').value || null;
            d.mediaBarItemCount = getNullableIntInput(view, '#DefaultMediaBarItemCount');
            d.mediaBarAutoAdvance = getNullableBoolSelect(view, '#DefaultMediaBarAutoAdvance');
            d.mediaBarIntervalMs = getNullableIntInput(view, '#DefaultMediaBarIntervalMs');
            d.mediaBarTrailerPreview = getNullableBoolSelect(view, '#DefaultMediaBarTrailerPreview');
            d.mediaBarTrailerAudio = getNullableBoolSelect(view, '#DefaultMediaBarTrailerAudio');
            d.mediaBarTrailerCaptions = getNullableBoolSelect(view, '#DefaultMediaBarTrailerCaptions');
            d.episodePreviewEnabled = getNullableBoolSelect(view, '#DefaultEpisodePreviewEnabled');
            d.previewAudioEnabled = getNullableBoolSelect(view, '#DefaultPreviewAudioEnabled');
            d.seasonalSurprise = view.querySelector('#DefaultSeasonalSurprise').value || null;

            // Clients store this one as text, unlike the millisecond fields below.
            d.resumeSubtractDuration = view.querySelector('#DefaultResumeSubtractDuration').value || null;
            d.unpauseRewindDuration = getNullableIntInput(view, '#DefaultUnpauseRewindDuration');
            d.skipBackLength = getNullableIntInput(view, '#DefaultSkipBackLength');
            d.skipForwardLength = getNullableIntInput(view, '#DefaultSkipForwardLength');

            d.defaultAudioLanguage = view.querySelector('#DefaultDefaultAudioLanguage').value.trim() || null;
            d.preferDefaultAudioTrack = getNullableBoolSelect(view, '#DefaultPreferDefaultAudioTrack');
            d.preferAudioDescription = getNullableBoolSelect(view, '#DefaultPreferAudioDescription');

            d.subtitleMode = view.querySelector('#DefaultSubtitleMode').value || null;
            d.defaultSubtitleLanguage = view.querySelector('#DefaultDefaultSubtitleLanguage').value.trim() || null;
            d.preferSdhSubtitles = getNullableBoolSelect(view, '#DefaultPreferSdhSubtitles');

            d.cinemaModeEnabled = getNullableBoolSelect(view, '#DefaultCinemaModeEnabled');
            d.autoplayNextEpisode = getNullableBoolSelect(view, '#DefaultAutoplayNextEpisode');
            d.nextUpTimeout = getNullableIntInput(view, '#DefaultNextUpTimeout');
            d.stillWatchingBehavior = view.querySelector('#DefaultStillWatchingBehavior').value || null;

            if (view.querySelector('#DefaultCollectionPicker .adminCollectionCb')) {
                var collectionIds = Array.prototype.slice.call(view.querySelectorAll('#DefaultCollectionPicker .adminCollectionCb:checked')).map(function (cb) { return cb.dataset.id; });
                d.mediaBarCollectionIds = collectionIds.length > 0 ? collectionIds : null;
            }
            if (view.querySelector('#DefaultLibraryPicker .adminLibraryCb')) {
                var libraryIds = Array.prototype.slice.call(view.querySelectorAll('#DefaultLibraryPicker .adminLibraryCb:checked')).map(function (cb) { return cb.dataset.id; });
                d.mediaBarLibraryIds = libraryIds.length > 0 ? libraryIds : null;
            }

            d.homeRowsStyle = view.querySelector('#DefaultHomeRowsStyle').value || null;
            d.fullScreenRows = getNullableBoolSelect(view, '#DefaultFullScreenRows');
            d.homeRowInfoOverlay = getNullableBoolSelect(view, '#DefaultHomeRowInfoOverlay');
            d.classicHomeRowsPadding = getNullableRangeInput(view, '#DefaultClassicHomeRowsPadding');
            d.modernHomeRowsPadding = getNullableRangeInput(view, '#DefaultModernHomeRowsPadding');
            d.posterSize = view.querySelector('#DefaultPosterSize').value || null;
            d.homeImageTypeContinueWatching = view.querySelector('#DefaultHomeImageTypeContinueWatching').value || null;
            d.displayFavoritesRows = getNullableBoolSelect(view, '#DefaultDisplayFavoritesRows');
            d.favoritesRowSortBy = view.querySelector('#DefaultFavoritesRowSortBy').value || null;
            d.favoritesRowSortOrder = view.querySelector('#DefaultFavoritesRowSortOrder').value || null;
            d.displayCollectionsRows = getNullableBoolSelect(view, '#DefaultDisplayCollectionsRows');
            d.collectionsRowSortBy = view.querySelector('#DefaultCollectionsRowSortBy').value || null;
            d.collectionsRowSortOrder = view.querySelector('#DefaultCollectionsRowSortOrder').value || null;
            d.displayGenresRows = getNullableBoolSelect(view, '#DefaultDisplayGenresRows');
            d.genresRowSortBy = view.querySelector('#DefaultGenresRowSortBy').value || null;
            d.genresRowSortOrder = view.querySelector('#DefaultGenresRowSortOrder').value || null;
            d.displayPlaylistsRows = getNullableBoolSelect(view, '#DefaultDisplayPlaylistsRows');
            d.playlistsRowSortBy = view.querySelector('#DefaultPlaylistsRowSortBy').value || null;
            d.playlistsRowSortOrder = view.querySelector('#DefaultPlaylistsRowSortOrder').value || null;
            d.displayAudioRows = getNullableBoolSelect(view, '#DefaultDisplayAudioRows');
            d.audioRowsSortBy = view.querySelector('#DefaultAudioRowsSortBy').value || null;
            d.audioRowsSortOrder = view.querySelector('#DefaultAudioRowsSortOrder').value || null;
            d.displayStudiosRows = getNullableBoolSelect(view, '#DefaultDisplayStudiosRows');
            d.studiosRowSortBy = view.querySelector('#DefaultStudiosRowSortBy').value || null;
            d.studiosRowSortOrder = view.querySelector('#DefaultStudiosRowSortOrder').value || null;
            d.displayRewatchRow = getNullableBoolSelect(view, '#DefaultDisplayRewatchRow');
            d.rewatchSortBy = view.querySelector('#DefaultRewatchSortBy').value || null;
            d.displaySinceYouWatchedRows = getNullableBoolSelect(view, '#DefaultDisplaySinceYouWatchedRows');
            d.homeImageUseSeriesImage = getNullableBoolSelect(view, '#DefaultHomeImageUseSeriesImage');
            var homeSections = getHomeSectionsValue(view);
            d.homeSections = homeSections;
            d.homeRowOrder = getHomeRowOrderValue(view, homeSections);
            d.mergeContinueWatchingNextUp = getNullableBoolSelect(view, '#DefaultMergeContinueWatchingNextUp');
            d.nextUpMaxDays = getNullableIntInput(view, '#DefaultNextUpMaxDays');

            d.enableMultiServerLibraries = getNullableBoolSelect(view, '#DefaultEnableMultiServerLibraries');
            d.mergeRecentRowsByType = getNullableBoolSelect(view, '#DefaultMergeRecentRowsByType');
            d.recentlyReleasedSeriesType = view.querySelector('#DefaultRecentlyReleasedSeriesType').value || null;
            d.groupItemsIntoCollections = getNullableBoolSelect(view, '#DefaultGroupItemsIntoCollections');
            d.showMediaDetailsOnLibraryPage = getNullableBoolSelect(view, '#DefaultShowMediaDetailsOnLibraryPage');
            d.useDetailedSubHeadings = getNullableBoolSelect(view, '#DefaultUseDetailedSubHeadings');
            d.hideBackdropsInLibraries = getNullableBoolSelect(view, '#DefaultHideBackdropsInLibraries');

            d.showShuffleButton = getNullableBoolSelect(view, '#DefaultShowShuffleButton');
            d.showGenresButton = getNullableBoolSelect(view, '#DefaultShowGenresButton');
            d.showFavoritesButton = getNullableBoolSelect(view, '#DefaultShowFavoritesButton');
            d.showLibrariesInToolbar = getNullableBoolSelect(view, '#DefaultShowLibrariesInToolbar');

            d.mediaBarTrailerPreview = getNullableBoolSelect(view, '#DefaultMediaBarTrailerPreview');
            d.episodePreviewEnabled = getNullableBoolSelect(view, '#DefaultEpisodePreviewEnabled');
            d.previewAudioEnabled = getNullableBoolSelect(view, '#DefaultPreviewAudioEnabled');

            d.mdblistEnabled = view.querySelector('#DefaultMdblistEnabled').checked;
            d.tmdbEpisodeRatingsEnabled = view.querySelector('#DefaultTmdbEpisodeRatingsEnabled').checked;
            d.mdblistShowRatingBadges = getNullableBoolSelect(view, '#DefaultMdblistShowRatingBadges');
            d.mdblistRatingSources = getRatingSourcesValue(view);
            d.seerrBlockNsfw = getNullableBoolSelect(view, '#DefaultSeerrBlockNsfw');
            // seerrRows carries more than the ordering, so merge rather than replace.
            var seerrRows = Object.assign({}, d.seerrRows || {});
            seerrRows.rowOrder = getSeerrDiscoveryRowOrder(view);
            d.seerrRows = seerrRows;

            config.DefaultUserSettings = d;

            return ApiClient.updatePluginConfiguration(PluginUniqueId, config).then(function (result) {
                Dashboard.processPluginConfigurationUpdateResult(result);
            });
        }).catch(function (error) {
            // Rethrown so pushDefaults, which chains on this, stops rather than
            // pushing defaults the save never persisted.
            reportConfigError(error);
            return Promise.reject(error);
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

    function testMdblistKey(view) {
        var resultEl = view.querySelector('#MdblistActionResult');
        var key = (view.querySelector('#MdblistApiKey').value || '').trim();
        if (resultEl) resultEl.textContent = 'Testing…';
        ApiClient.getJSON(ApiClient.getUrl('Moonfin/MdbList/KeyInfo', key ? { key: key } : {})).then(function (info) {
            if (!resultEl) return;
            if (info && info.success) {
                resultEl.textContent = 'Key ok: ' + info.username +
                    ' (' + (info.plan || (info.isSupporter ? 'Supporter' : 'Free')) + '), ' +
                    (info.apiRequestsCount || 0) + '/' + (info.apiRequests || 0) + ' requests used today.';
            } else {
                resultEl.textContent = (info && info.error) || 'Key test failed.';
            }
        }).catch(function () {
            if (resultEl) resultEl.textContent = 'Key test failed. Could not reach the server.';
        });
    }

    function clearMdblistCache(view) {
        var resultEl = view.querySelector('#MdblistActionResult');
        var btn = view.querySelector('#MdblistClearCacheBtn');
        if (btn) btn.disabled = true;
        if (resultEl) resultEl.textContent = 'Clearing…';
        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('Moonfin/MdbList/ClearCache'),
            dataType: 'json'
        }).then(function (result) {
            if (resultEl) {
                resultEl.textContent = 'Cleared ' + ((result && result.removed) || 0) +
                    ' cached ratings. They will refetch as items are viewed.';
            }
        }).catch(function () {
            if (resultEl) resultEl.textContent = 'Could not clear the ratings cache.';
        }).finally(function () {
            if (btn) btn.disabled = false;
        });
    }

    var moonfinMessageColors = {
        white: '#e8eaed',
        green: '#4caf6d',
        blue: '#4a9ee0',
        yellow: '#e0b040',
        red: '#e05260'
    };

    var moonfinDeliveryLabels = {
        inbox: 'Notify',
        popup: 'Opens in app'
    };

    function setMessageResult(view, text, color) {
        var result = view.querySelector('#MessageSaveResult');
        if (!result) return;

        if (!text) {
            result.style.display = 'none';
            result.textContent = '';
            return;
        }

        result.style.display = '';
        result.style.color = color || '';
        result.textContent = text;
    }

    // The date inputs work in the admin's own time zone, but the server stores UTC.
    function messageDateToInput(utcValue) {
        if (!utcValue) return '';

        var date = new Date(utcValue);
        if (isNaN(date.getTime())) return '';

        var pad = function (n) { return (n < 10 ? '0' : '') + n; };
        return date.getFullYear() + '-' + pad(date.getMonth() + 1) + '-' + pad(date.getDate()) +
            'T' + pad(date.getHours()) + ':' + pad(date.getMinutes());
    }

    function messageDateFromInput(value) {
        if (!value) return null;
        var date = new Date(value);
        return isNaN(date.getTime()) ? null : date.toISOString();
    }

    function formatMessageDate(utcValue) {
        if (!utcValue) return '';
        var date = new Date(utcValue);
        return isNaN(date.getTime()) ? '' : date.toLocaleString();
    }

    function toggleMessageTargets(view) {
        var audience = view.querySelector('#MessageAudience');
        var row = view.querySelector('#MessageTargetsRow');
        if (!audience || !row) return;
        row.style.display = audience.value === 'users' ? '' : 'none';
    }

    function loadMessageTargetUsers(view) {
        var select = view.querySelector('#MessageTargets');
        if (!select || !ApiClient.getUsers) return Promise.resolve();

        return ApiClient.getUsers().then(function (users) {
            select.innerHTML = '';
            (users || []).forEach(function (user) {
                var option = document.createElement('option');
                option.value = user.Id;
                option.textContent = user.Name;
                select.appendChild(option);
            });
        }).catch(function () {});
    }

    function resetMessageForm(view) {
        view.__moonfinEditingMessageId = null;

        view.querySelector('#MessageTitle').value = '';
        view.querySelector('#MessageBody').value = '';
        view.querySelector('#MessageColor').value = 'white';
        view.querySelector('#MessageDelivery').value = 'inbox';
        view.querySelector('#MessageAudience').value = 'all';
        view.querySelector('#MessageStart').value = '';
        view.querySelector('#MessageEnd').value = '';
        view.querySelector('#MessageActionLabel').value = '';
        view.querySelector('#MessageActionUrl').value = '';

        var targets = view.querySelector('#MessageTargets');
        if (targets) {
            Array.prototype.forEach.call(targets.options, function (option) { option.selected = false; });
        }

        var saveBtn = view.querySelector('#MessageSaveBtn');
        if (saveBtn) saveBtn.querySelector('span').textContent = 'Save Message';

        var resetBtn = view.querySelector('#MessageResetBtn');
        if (resetBtn) resetBtn.style.display = 'none';

        toggleMessageTargets(view);
        setMessageResult(view, '', '');
    }

    function editMessage(view, item) {
        view.__moonfinEditingMessageId = item.Id || item.id || null;

        view.querySelector('#MessageTitle').value = item.Title || item.title || '';
        view.querySelector('#MessageBody').value = item.Body || item.body || '';
        view.querySelector('#MessageColor').value = item.Color || item.color || 'white';
        view.querySelector('#MessageDelivery').value = item.Delivery || item.delivery || 'inbox';
        view.querySelector('#MessageAudience').value = item.Audience || item.audience || 'all';
        view.querySelector('#MessageStart').value = messageDateToInput(item.StartUtc || item.startUtc);
        view.querySelector('#MessageEnd').value = messageDateToInput(item.EndUtc || item.endUtc);
        view.querySelector('#MessageActionLabel').value = item.ActionLabel || item.actionLabel || '';
        view.querySelector('#MessageActionUrl').value = item.ActionUrl || item.actionUrl || '';

        var selected = item.TargetUserIds || item.targetUserIds || [];
        var targets = view.querySelector('#MessageTargets');
        if (targets) {
            Array.prototype.forEach.call(targets.options, function (option) {
                option.selected = selected.some(function (id) {
                    return String(id).toLowerCase() === String(option.value).toLowerCase();
                });
            });
        }

        var saveBtn = view.querySelector('#MessageSaveBtn');
        if (saveBtn) saveBtn.querySelector('span').textContent = 'Update Message';

        var resetBtn = view.querySelector('#MessageResetBtn');
        if (resetBtn) resetBtn.style.display = '';

        toggleMessageTargets(view);
        setMessageResult(view, '', '');

        var titleInput = view.querySelector('#MessageTitle');
        if (titleInput) titleInput.focus();
    }

    function renderMessagesList(view, items) {
        var container = view.querySelector('#MessagesList');
        if (!container) return;

        if (!items || items.length === 0) {
            container.innerHTML = '<div style="padding:8px;opacity:0.6;font-size:0.9em;">No messages yet.</div>';
            return;
        }

        var now = new Date();
        var html = '';

        items.forEach(function (item, index) {
            var id = item.Id || item.id || '';
            var title = item.Title || item.title || '';
            var body = item.Body || item.body || '';
            var pick = item.Color || item.color || 'white';
            var delivery = item.Delivery || item.delivery || 'inbox';
            var audience = item.Audience || item.audience || 'all';
            var start = item.StartUtc || item.startUtc;
            var end = item.EndUtc || item.endUtc;
            var targets = item.TargetUserIds || item.targetUserIds || [];
            var color = moonfinMessageColors[pick] || moonfinMessageColors.white;

            var state = 'Showing now';
            if (start && new Date(start) > now) {
                state = 'Starts ' + formatMessageDate(start);
            } else if (end && new Date(end) <= now) {
                state = 'Expired';
            } else if (end) {
                state = 'Until ' + formatMessageDate(end);
            }

            var who = audience === 'admins'
                ? 'Admins only'
                : audience === 'users'
                    ? targets.length + (targets.length === 1 ? ' user' : ' users')
                    : 'Everyone';

            html += '<div style="display:flex;align-items:flex-start;gap:10px;padding:8px;border:1px solid rgba(128,128,128,0.3);border-left:3px solid ' + color + ';border-radius:4px;margin-bottom:6px;">' +
                '<div style="flex:1;min-width:0;">' +
                '<div style="display:flex;align-items:center;gap:8px;flex-wrap:wrap;">' +
                '<strong style="font-size:0.95em;color:' + color + ';">' + esc(title || '(no title)') + '</strong>' +
                '</div>' +
                '<div style="font-size:0.82em;opacity:0.8;margin-top:4px;white-space:pre-wrap;">' + esc(body.length > 160 ? body.slice(0, 160) + '…' : body) + '</div>' +
                '<div style="font-size:0.78em;opacity:0.6;margin-top:4px;">' +
                esc(state) + ' • ' + esc(who) + ' • ' + esc(moonfinDeliveryLabels[delivery] || delivery) +
                '</div>' +
                '</div>' +
                '<div style="display:flex;flex-direction:column;gap:2px;">' +
                '<button type="button" class="moonfinMessageUpBtn" data-message-id="' + esc(id) + '" title="Move up"' + (index === 0 ? ' disabled' : '') + ' style="background:none;border:1px solid rgba(128,128,128,0.5);border-radius:4px;padding:0 6px;cursor:pointer;line-height:1.3;">&#9650;</button>' +
                '<button type="button" class="moonfinMessageDownBtn" data-message-id="' + esc(id) + '" title="Move down"' + (index === items.length - 1 ? ' disabled' : '') + ' style="background:none;border:1px solid rgba(128,128,128,0.5);border-radius:4px;padding:0 6px;cursor:pointer;line-height:1.3;">&#9660;</button>' +
                '</div>' +
                '<button type="button" class="moonfinMessageEditBtn" data-message-id="' + esc(id) + '" title="Edit message" style="background:none;border:1px solid rgba(128,128,128,0.5);border-radius:4px;padding:2px 8px;cursor:pointer;line-height:1.1;">Edit</button>' +
                '<button type="button" class="moonfinMessageDeleteBtn" data-message-id="' + esc(id) + '" title="Delete message" style="background:none;border:1px solid rgba(239,68,68,0.6);color:#ef4444;border-radius:4px;padding:2px 8px;cursor:pointer;line-height:1.1;">&#x2715;</button>' +
                '</div>';
        });

        container.innerHTML = html;
    }

    function loadMessagesList(view) {
        var container = view.querySelector('#MessagesList');
        if (!container) return;

        var serverUrl = ApiClient.serverAddress ? ApiClient.serverAddress() : '';
        container.innerHTML = '<div style="padding:8px;opacity:0.6;font-size:0.9em;">Loading messages...</div>';

        fetch(serverUrl + '/Moonfin/Admin/Messages', { method: 'GET', headers: moonfinAuthHeaders() })
            .then(parseJsonResponse)
            .then(function (payload) {
                var items = payload.items || payload.Items || [];
                view.__moonfinMessagesCache = items;
                renderMessagesList(view, items);
            })
            .catch(function (error) {
                container.innerHTML = '<div style="padding:8px;color:#d9534f;font-size:0.9em;">' +
                    esc((error && error.message) ? error.message : 'Failed to load messages.') + '</div>';
            });
    }

    // Moves one message up or down and saves the whole order, so the app shows
    // them in the same sequence as this list.
    function moveMessage(view, messageId, delta) {
        var items = (view.__moonfinMessagesCache || []).slice();
        var from = -1;
        for (var i = 0; i < items.length; i++) {
            if ((items[i].Id || items[i].id) === messageId) { from = i; break; }
        }

        var to = from + delta;
        if (from < 0 || to < 0 || to >= items.length) return;

        var moved = items.splice(from, 1)[0];
        items.splice(to, 0, moved);

        // Redrawn straight away so the arrows feel instant, then saved.
        view.__moonfinMessagesCache = items;
        renderMessagesList(view, items);

        var serverUrl = ApiClient.serverAddress ? ApiClient.serverAddress() : '';
        fetch(serverUrl + '/Moonfin/Admin/Messages/Order', {
            method: 'POST',
            headers: moonfinAuthHeaders(),
            body: JSON.stringify({
                Ids: items.map(function (item) { return item.Id || item.id; })
            })
        })
            .then(parseJsonResponse)
            .catch(function (error) {
                setMessageResult(
                    view,
                    (error && error.message) ? error.message : 'Could not save the new order.',
                    '#d9534f'
                );
                loadMessagesList(view);
            });
    }

    function saveMessage(view) {
        var saveBtn = view.querySelector('#MessageSaveBtn');
        var title = (view.querySelector('#MessageTitle').value || '').trim();
        var body = (view.querySelector('#MessageBody').value || '').trim();

        if (!title && !body) {
            setMessageResult(view, 'Enter a title or a message.', '#d9534f');
            return;
        }

        var audience = view.querySelector('#MessageAudience').value;
        var targetSelect = view.querySelector('#MessageTargets');
        var targetUserIds = [];
        if (audience === 'users' && targetSelect) {
            targetUserIds = Array.prototype.filter.call(targetSelect.options, function (option) {
                return option.selected;
            }).map(function (option) { return option.value; });

            if (targetUserIds.length === 0) {
                setMessageResult(view, 'Pick at least one user, or change who sees it.', '#d9534f');
                return;
            }
        }

        var payload = {
            Id: view.__moonfinEditingMessageId || '',
            Title: title,
            Body: body,
            Color: view.querySelector('#MessageColor').value,
            Delivery: view.querySelector('#MessageDelivery').value,
            Audience: audience,
            TargetUserIds: targetUserIds,
            ActionLabel: (view.querySelector('#MessageActionLabel').value || '').trim(),
            ActionUrl: (view.querySelector('#MessageActionUrl').value || '').trim(),
            StartUtc: messageDateFromInput(view.querySelector('#MessageStart').value),
            EndUtc: messageDateFromInput(view.querySelector('#MessageEnd').value)
        };

        var serverUrl = ApiClient.serverAddress ? ApiClient.serverAddress() : '';
        var wasEditing = !!view.__moonfinEditingMessageId;

        if (saveBtn) saveBtn.disabled = true;
        setMessageResult(view, '', '');

        fetch(serverUrl + '/Moonfin/Admin/Messages', {
            method: 'POST',
            headers: moonfinAuthHeaders(),
            body: JSON.stringify(payload)
        })
            .then(parseJsonResponse)
            .then(function (response) {
                var saved = response.item || response.Item || {};
                var warnings = [];

                if (payload.ActionUrl && !(saved.ActionUrl || saved.actionUrl)) {
                    warnings.push('the link was dropped, it must start with http:// or https://');
                }
                if (payload.EndUtc && !(saved.EndUtc || saved.endUtc)) {
                    warnings.push('the end date was dropped, it was before the start date');
                }

                var message = wasEditing ? 'Message updated.' : 'Message saved.';
                if (warnings.length) message += ' Note: ' + warnings.join(', ') + '.';

                // resetMessageForm clears the result line, so set it afterwards.
                resetMessageForm(view);
                setMessageResult(view, message, warnings.length ? '#f0ad4e' : '#52b54b');
                loadMessagesList(view);
            })
            .catch(function (error) {
                setMessageResult(view, (error && error.message) ? error.message : 'Save failed.', '#d9534f');
            })
            .finally(function () {
                if (saveBtn) saveBtn.disabled = false;
            });
    }

    function deleteMessage(view, messageId) {
        if (!messageId) return;
        if (!window.confirm('Delete this message?')) return;

        var serverUrl = ApiClient.serverAddress ? ApiClient.serverAddress() : '';

        fetch(serverUrl + '/Moonfin/Admin/Messages/' + encodeURIComponent(messageId), {
            method: 'DELETE',
            headers: moonfinAuthHeaders()
        })
            .then(parseJsonResponse)
            .then(function () {
                if (view.__moonfinEditingMessageId === messageId) resetMessageForm(view);
                setMessageResult(view, 'Message deleted.', '#52b54b');
                loadMessagesList(view);
            })
            .catch(function (error) {
                setMessageResult(view, (error && error.message) ? error.message : 'Delete failed.', '#d9534f');
            });
    }

    function initializeDefaultsSubtabs(view) {
        var subnav = view.querySelector('.defaultsSubnavBar');
        if (!subnav || subnav.dataset.bound) return;
        subnav.dataset.bound = 'true';

        subnav.addEventListener('click', function (e) {
            var btn = e.target.closest('.defaultsSubtabBtn');
            if (!btn) return;
            var subtab = btn.dataset.subtab;
            if (!subtab) return;

            var buttons = subnav.querySelectorAll('.defaultsSubtabBtn');
            buttons.forEach(function (b) { b.classList.remove('active'); });
            btn.classList.add('active');

            var panels = view.querySelectorAll('.defaultsSubtabPanel');
            panels.forEach(function (p) {
                if (p.dataset.subtab === subtab) {
                    p.classList.add('active');
                } else {
                    p.classList.remove('active');
                }
            });
        });
    }

    function bindOnce(view) {
        if (view.__moonfinBound) return;
        view.__moonfinBound = true;
        view.__moonfinState = { timer: null };

        initializeAdminTabs(view);
        initializeDefaultsSubtabs(view);

        var form = view.querySelector('#MoonfinConfigForm');
        if (form) {
            form.addEventListener('submit', function (e) {
                e.preventDefault();
                // saveConfig already reported it. Swallowed so the rethrow it
                // makes for pushDefaults does not land as unhandled here.
                saveConfig(view).catch(function () {});
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

        var messageAudience = view.querySelector('#MessageAudience');
        if (messageAudience) {
            messageAudience.addEventListener('change', function () { toggleMessageTargets(view); });
        }

        var messageSaveBtn = view.querySelector('#MessageSaveBtn');
        if (messageSaveBtn) messageSaveBtn.addEventListener('click', function () { saveMessage(view); });

        var messageResetBtn = view.querySelector('#MessageResetBtn');
        if (messageResetBtn) messageResetBtn.addEventListener('click', function () { resetMessageForm(view); });

        var messagesListContainer = view.querySelector('#MessagesList');
        if (messagesListContainer) {
            messagesListContainer.addEventListener('click', function (event) {
                var upButton = event.target.closest('.moonfinMessageUpBtn');
                if (upButton) {
                    moveMessage(view, upButton.getAttribute('data-message-id') || '', -1);
                    return;
                }

                var downButton = event.target.closest('.moonfinMessageDownBtn');
                if (downButton) {
                    moveMessage(view, downButton.getAttribute('data-message-id') || '', 1);
                    return;
                }

                var editButton = event.target.closest('.moonfinMessageEditBtn');
                if (editButton) {
                    var editId = editButton.getAttribute('data-message-id') || '';
                    var cached = view.__moonfinMessagesCache || [];
                    var found = cached.filter(function (item) {
                        return (item.Id || item.id) === editId;
                    })[0];
                    if (found) editMessage(view, found);
                    return;
                }

                var deleteButton = event.target.closest('.moonfinMessageDeleteBtn');
                if (deleteButton) deleteMessage(view, deleteButton.getAttribute('data-message-id') || '');
            });
        }

        var mdblistTestBtn = view.querySelector('#MdblistTestKeyBtn');
        if (mdblistTestBtn) mdblistTestBtn.addEventListener('click', function () { testMdblistKey(view); });

        var mdblistClearCacheBtn = view.querySelector('#MdblistClearCacheBtn');
        if (mdblistClearCacheBtn) mdblistClearCacheBtn.addEventListener('click', function () { clearMdblistCache(view); });
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
        // The user list must be there before the form can preselect targets on edit.
        loadMessageTargetUsers(view).then(function () {
            resetMessageForm(view);
            loadMessagesList(view);
        });
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
