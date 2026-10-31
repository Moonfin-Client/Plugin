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

    // Ids are the client's wire names for the pinnable tabs.
    var BOTTOM_NAVBAR_TABS = [
        ['search', 'Search'],
        ['libraries', 'Libraries'],
        ['favorites', 'Favorites'],
        ['genres', 'Genres'],
        ['liveTv', 'Live TV'],
        ['discover', 'Discover (Seerr)'],
        ['folders', 'Folders']
    ];

    function setBottomNavbarTabSelects(view, tabs) {
        var selects = view.querySelectorAll('.bottomNavbarTabSelect');
        for (var i = 0; i < selects.length; i++) {
            var select = selects[i];
            if (!select.options.length) {
                var unset = document.createElement('option');
                unset.value = '';
                unset.textContent = i === 0 ? 'Not set (automatic)' : 'None';
                select.appendChild(unset);
                BOTTOM_NAVBAR_TABS.forEach(function (tab) {
                    var option = document.createElement('option');
                    option.value = tab[0];
                    option.textContent = tab[1];
                    select.appendChild(option);
                });
            }
            setSelectValue(view, '#' + select.id, tabs && tabs[i] ? tabs[i] : '', 'Configured tab');
        }
    }

    // Blank slots are skipped and repeats dropped, the same way the client
    // reads the list. Nothing picked leaves it to each device.
    function getBottomNavbarTabSelects(view) {
        var picked = [];
        var selects = view.querySelectorAll('.bottomNavbarTabSelect');
        for (var i = 0; i < selects.length; i++) {
            var value = selects[i].value;
            if (value && picked.indexOf(value) === -1) picked.push(value);
        }
        return picked.length ? picked : null;
    }

    // The switch says whether a card plays anything, and the mode says which kind. Defaults
    // saved before the mode existed only have the switch.
    function mediaPreviewModeOf(defaults) {
        if (defaults.episodePreviewEnabled === false) return 'off';
        if (defaults.episodePreviewEnabled !== true) return '';
        return defaults.mediaPreviewMode === 'trailer' ? 'trailer' : 'media';
    }

    // The switch says whether Home shows a backdrop, and the style says how. Defaults saved
    // before the style existed only have the switch.
    function homeBackdropStyleOf(defaults) {
        if (defaults.homeBackdropEnabled === false) return 'off';
        if (defaults.homeBackdropEnabled !== true) return '';
        var style = defaults.homeBackdropStyle;
        return style === 'corner' || style === 'top' ? style : 'fullScreen';
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

    // The holidays in seasonal-holidays.json, by id.
    var SEASONAL_HOLIDAYS = [
        ['newYear', "New Year's"],
        ['valentines', "Valentine's Day"],
        ['easter', 'Easter'],
        ['pride', 'Pride'],
        ['halloween', 'Halloween'],
        ['thanksgiving', 'Thanksgiving'],
        ['christmas', 'Christmas'],
        ['lunarNewYear', 'Lunar New Year'],
        ['diwali', 'Diwali']
    ];

    // A ticked box means the holiday shows. The stored list holds the hidden ones.
    function renderSeasonalHolidayChecks(view, hidden) {
        var container = view.querySelector('#DefaultSeasonalRowHolidays');
        if (!container) return;
        var hiddenIds = Array.isArray(hidden) ? hidden : [];
        container.innerHTML = SEASONAL_HOLIDAYS.map(function (holiday) {
            var checked = hiddenIds.indexOf(holiday[0]) === -1 ? ' checked' : '';
            return '<label class="emby-checkbox-label" style="display:block;margin:4px 0;">' +
                '<input type="checkbox" is="emby-checkbox" data-seasonal-holiday="' + holiday[0] + '"' + checked + ' />' +
                '<span>' + esc(holiday[1]) + '</span></label>';
        }).join('');
    }

    function readSeasonalHiddenHolidays(view) {
        var boxes = view.querySelectorAll('#DefaultSeasonalRowHolidays input[data-seasonal-holiday]');
        var hidden = [];
        for (var i = 0; i < boxes.length; i++) {
            if (!boxes[i].checked) hidden.push(boxes[i].getAttribute('data-seasonal-holiday'));
        }
        return hidden.length > 0 ? hidden : null;
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

        Array.prototype.slice.call(view.querySelectorAll('[data-open-tab]')).forEach(function (button) {
            button.addEventListener('click', function () {
                selectTab(button.getAttribute('data-open-tab'));
            });
        });

        var syncToggle = view.querySelector('#EnableSettingsSync');
        if (syncToggle) {
            syncToggle.addEventListener('change', function () { updateSyncStatus(view); });
        }

        var saved = null;
        try { saved = window.localStorage.getItem('moonfinAdminActiveTab'); } catch (e) {}
        var validSaved = saved && panels.some(function (p) { return p.getAttribute('data-tab') === saved; });
        selectTab(validSaved ? saved : 'settingsSync');

        initializeDefaultsCategories(view);
        initializeSettingsSearch(view);
        view.addEventListener('change', function () { applyFieldDependencies(view); });
    }

    // Each heading in a User Defaults subtab folds the settings under it, up to the next
    // heading. A subtab with only one category starts open.
    function initializeDefaultsCategories(view) {
        view.querySelectorAll('.defaultsSubtabPanel').forEach(function (panel) {
            var headings = panel.querySelectorAll(':scope > h4');
            var startOpen = headings.length === 1;
            headings.forEach(function (heading) {
                var category = document.createElement('div');
                category.className = 'moonfinCategory' + (startOpen ? '' : ' is-collapsed');
                var body = document.createElement('div');
                body.className = 'moonfinCategoryBody';
                while (heading.nextSibling && heading.nextSibling.tagName !== 'H4') {
                    body.appendChild(heading.nextSibling);
                }

                var toggle = document.createElement('button');
                toggle.type = 'button';
                toggle.className = 'moonfinCategoryToggle';
                toggle.setAttribute('aria-expanded', String(startOpen));
                while (heading.firstChild) {
                    toggle.appendChild(heading.firstChild);
                }
                heading.appendChild(toggle);
                toggle.addEventListener('click', function () {
                    var collapsed = category.classList.toggle('is-collapsed');
                    toggle.setAttribute('aria-expanded', String(!collapsed));
                });

                panel.insertBefore(category, heading);
                category.appendChild(heading);
                category.appendChild(body);
            });
        });
    }

    function updateSyncStatus(view) {
        var toggle = view.querySelector('#EnableSettingsSync');
        var card = view.querySelector('#MoonfinSyncStatusCard');
        if (toggle && card) {
            card.classList.toggle('is-off', !toggle.checked);
        }
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

    // A rule hides its fields while every parent is set to one of its off values, which are
    // 'false' unless the rule lists others. "Not set" leaves the choice to users, so it never
    // hides anything. A hidden parent counts as off and hides its own fields too, so a rule
    // has to come after the rule that hides its parent.
    var FIELD_DEPENDENCIES = [
        { parent: 'SeerrEnabled', fields: ['SeerrUrl', 'SeerrDisplayName', 'PublicServerUrl', 'DefaultShowSeerrButton', 'DefaultSeerrHeading', 'DefaultSeerrBlockNsfw', 'DefaultSeerrShowMissingCollectionItems', 'DefaultShowSeerrAvailabilityBadges', 'DefaultSeerrDiscoveryList', 'DefaultEnableRadarrCalendar', 'DefaultEnableSonarrCalendar', 'DefaultSeerrRowsSortBy', 'DefaultSeerrRowsSortOrder'] },
        { parent: 'SeerrEnabled', off: ['true'], fields: ['DefaultSeerrOffNote'] },
        { parent: 'LiveTvDetectIncompleteRecordings', fields: ['LiveTvAutoRecordCompleteShowing', 'LiveTvDeleteIncompleteCopies', 'LiveTvIncompleteThresholdPercent'] },
        { parent: 'DefaultEnableRadarrCalendar', fields: ['DefaultRadarrCalendarShowCinema', 'DefaultRadarrCalendarShowDigital', 'DefaultRadarrCalendarShowPhysical', 'DefaultRadarrCalendarShowDate', 'DefaultMergeRadarrSonarrCalendars'] },
        { parent: 'DefaultEnableSonarrCalendar', fields: ['DefaultSonarrCalendarShowDate', 'DefaultSonarrCalendarShowEpisodeInfo', 'DefaultMergeRadarrSonarrCalendars'] },
        { parent: 'MdblistOfficialListsEnabled', fields: ['MdblistOfficialListsMaxItems'] },
        { parent: 'AnimeMarkersEnabled', fields: ['AnimeMarkerRecapLookup', 'AnimeMarkerShowFiller', 'AnimeMarkerMaxAgeDays'] },
        { parent: 'AnimeAudioMarkersEnabled', fields: ['AnimeAudioMarkersMovies', 'AnimeAudioSeparateDualAudio', 'AnimeAudioLabelStyle'] },
        { parent: ['AnimeMarkersEnabled', 'AnimeAudioMarkersEnabled'], fields: ['AnimeMarkerLibraryPicker', 'AnimeAudioTrustSelectedLibraries', 'AnimeMarkerPlacement', 'AnimeMarkerDiagBtn', 'AnimeMarkerPreviewName', 'AnimeMarkerVerboseLogging', 'AnimeMarkerLogBtn'] },
        { parent: 'GamesEnabled', fields: ['GameCoresStatus', 'GameCoresFile', 'GameLibraryPicker', 'GamesRommUrl', 'GamesRommToken', 'GamesRommLibraryId', 'GamesRommTestBtn'] },
        { parent: 'DefaultNavbarPosition', off: ['top', 'left'], fields: ['DefaultBottomNavbarStyle', 'DefaultBottomNavbarTab1'] },
        { parent: 'DefaultNavbarPosition', off: ['bottom'], fields: ['DefaultNavbarAlwaysExpanded'] },
        { parent: 'DefaultShowShuffleButton', fields: ['DefaultShuffleContentType'] },
        { parent: 'DefaultSeasonalSurprise', off: ['none'], fields: ['DefaultSeasonalDensity', 'DefaultSeasonalFollower'] },
        { parent: 'DefaultLoadingAnimationImage', off: ['none'], fields: ['DefaultLoadingAnimationPosition', 'DefaultLoadingAnimationSize', 'DefaultLoadingAnimationSpeed', 'DefaultShowLoadingAnimationText'] },
        { parent: 'DefaultScreensaverComponent', off: ['none'], fields: ['DefaultScreensaverMovement', 'DefaultScreensaverSize'] },
        { parent: 'DefaultScreensaverMovement', off: ['slow', 'moderate', 'fast', 'ultra'], fields: ['DefaultScreensaverPosition'] },
        { parent: 'DefaultScreensaverBackdrop', off: ['black', 'moonfin', 'calm', 'neonPulse', 'aurora'], fields: ['DefaultScreensaverContentType', 'DefaultScreensaverLibraryPicker', 'DefaultScreensaverCollectionPicker', 'DefaultScreensaverGenrePicker'] },
        { parent: 'DefaultDisplayAudioRows', fields: ['DefaultAudioRowsSortBy', 'DefaultAudioRowsSortOrder'] },
        { parent: 'DefaultDisplayCollectionsRows', fields: ['DefaultCollectionsRowSortBy', 'DefaultCollectionsRowSortOrder', 'DefaultCollectionsRowShowEpisodes'] },
        { parent: 'DefaultDisplayFavoritesRows', fields: ['DefaultFavoritesRowSortBy', 'DefaultFavoritesRowSortOrder'] },
        { parent: 'DefaultDisplayGenresRows', fields: ['DefaultGenresRowSortBy', 'DefaultGenresRowSortOrder', 'DefaultGenresRowItemFilter'] },
        { parent: 'DefaultDisplayPlaylistsRows', fields: ['DefaultPlaylistsRowSortBy', 'DefaultPlaylistsRowSortOrder', 'DefaultPlaylistsRowShowEpisodes'] },
        { parent: 'DefaultDisplayRewatchRow', fields: ['DefaultRewatchSortBy', 'DefaultRewatchIncludeMovies', 'DefaultRewatchIncludeShows', 'DefaultRewatchIncludeCollections'] },
        { parent: 'DefaultDisplaySinceYouWatchedRows', fields: ['DefaultSinceYouWatchedSource', 'DefaultSinceYouWatchedSourceType', 'DefaultSinceYouWatchedSourceItem', 'DefaultSinceYouWatched1Enabled', 'DefaultSinceYouWatched2Enabled', 'DefaultSinceYouWatched3Enabled', 'DefaultSinceYouWatched4Enabled', 'DefaultSinceYouWatched5Enabled'] },
        { parent: 'DefaultSinceYouWatchedSource', off: ['online'], fields: ['DefaultSinceYouWatchedIncludeWatched'] },
        { parent: 'DefaultDisplayStudiosRows', fields: ['DefaultStudiosRowSortBy', 'DefaultStudiosRowSortOrder'] },
        { parent: 'DefaultMediaBarMode', off: ['off'], fields: ['DefaultMediaBarContentType', 'DefaultMediaBarItemCount', 'DefaultMediaBarSourceType', 'DefaultCollectionPickerSection', 'DefaultLibraryPickerSection', 'DefaultGenrePicker', 'DefaultMediaBarAutoAdvance', 'DefaultMediaBarOpacity', 'DefaultMediaBarOverlayColor', 'DefaultMediaBarRowPeek', 'DefaultMediaBarTrailerPreview'] },
        { parent: 'DefaultMediaBarMode', off: ['off', 'bookshelf', 'aya'], fields: ['DefaultMediaBarRatingsHeading', 'DefaultMediaBarMdblistEnabled', 'DefaultMediaBarMdblistRatingSourcesList', 'DefaultMediaBarMdblistShowRatingNames', 'DefaultMediaBarMdblistShowRatingBadges'] },
        { parent: 'DefaultMediaBarAutoAdvance', fields: ['DefaultMediaBarIntervalMs'] },
        { parent: 'DefaultMediaBarTrailerPreview', fields: ['DefaultMediaBarTrailerAudio', 'DefaultTrailerVolume', 'DefaultMediaBarTrailerCaptions'] },
        { parent: 'DefaultHomeRowsStyle', off: ['v1'], fields: ['DefaultModernCardsOnMyMediaRow', 'DefaultModernHomeRowsPadding'] },
        { parent: 'DefaultHomeRowsStyle', off: ['v2'], fields: ['DefaultClassicHomeRowsPadding', 'DefaultHomeRowInfoOverlay', 'DefaultHomeImageTypeContinueWatching', 'DefaultHomeRowsImageTypeOverride'] },
        { parent: 'DefaultHomeRowsImageTypeOverride', fields: ['DefaultHomeRowsImageType'] },
        { parent: 'DefaultHomeRowsImageTypeOverride', off: ['true'], fields: ['DefaultHomeRowImageTypesSection'] },
        { parent: 'DefaultMediaPreviewMode', off: ['off'], fields: ['DefaultPreviewAudioEnabled'] },
        { parent: 'DefaultSeasonalRowEnabled', fields: ['DefaultSeasonalRowCountry', 'DefaultSeasonalRowHolidays'] },
        { parent: 'DefaultDetailScreenStyle', off: ['modern', 'spotlight', 'nouveau', 'minimalist'], fields: ['DefaultDetailUseSeriesThumbnails'] },
        { parent: 'DefaultDetailScreenStyle', off: ['classic', 'spotlight', 'nouveau', 'minimalist'], fields: ['DefaultDetailExpandedTabs'] },
        { parent: 'DefaultThemeMusicEnabled', fields: ['DefaultThemeMusicVolume', 'DefaultThemeMusicOnHomeRows', 'DefaultThemeMusicLoop'] },
        { parent: 'DefaultSubtitleMode', off: ['none'], fields: ['DefaultDefaultSubtitleLanguage', 'DefaultFallbackSubtitleLanguage', 'DefaultPreferSdhSubtitles', 'DefaultPreferTextSubtitles', 'DefaultPreferExternalSubtitles'] },
        { parent: 'DefaultSegmentActionOutro', off: ['doNothing', 'skip'], fields: ['DefaultReplaceSkipOutroWithNextUp'] },
        { parent: ['DefaultSegmentActionIntro', 'DefaultSegmentActionRecap', 'DefaultSegmentActionPreview', 'DefaultSegmentActionCommercial', 'DefaultSegmentActionOutro'], off: ['doNothing', 'skip'], fields: ['DefaultMediaSegmentAutoHide'] },
        { parent: 'DefaultCinemaModeEnabled', fields: ['DefaultCinemaModeEpisodesEnabled'] }
    ];

    // The whole row a field sits in. A checkbox inside a grouped list hides with its group.
    function dependentFieldBlock(el) {
        var block = el.closest('.inputContainer, .checkboxContainer') || el;
        return (block.parentElement && block.parentElement.closest('.inputContainer')) || block;
    }

    function isDependencyParentOff(el, offValues, hiddenBlocks) {
        if (hiddenBlocks.indexOf(dependentFieldBlock(el)) !== -1) return true;
        var value = el.type === 'checkbox' ? String(el.checked) : el.value;
        return offValues.indexOf(value) !== -1;
    }

    function applyFieldDependencies(view) {
        var hiddenBlocks = [];
        FIELD_DEPENDENCIES.forEach(function (rule) {
            var off = [].concat(rule.parent).every(function (id) {
                var el = view.querySelector('#' + id);
                return !!el && isDependencyParentOff(el, rule.off || ['false'], hiddenBlocks);
            });
            if (!off) return;
            rule.fields.forEach(function (id) {
                var el = view.querySelector('#' + id);
                var block = el && dependentFieldBlock(el);
                if (block && hiddenBlocks.indexOf(block) === -1) hiddenBlocks.push(block);
            });
        });
        view.querySelectorAll('.moonfin-hidden-by-parent').forEach(function (block) {
            if (hiddenBlocks.indexOf(block) === -1) block.classList.remove('moonfin-hidden-by-parent');
        });
        hiddenBlocks.forEach(function (block) {
            block.classList.add('moonfin-hidden-by-parent');
        });
    }

    // ── Pickers ─────────────────────────────────────────────────────────────

    function loadGameLibraryPicker(view, selectedIds) {
        loadLibraryPicker(view, '#GameLibraryPicker', 'gameLibraryCb', selectedIds);
    }

    function loadRommLibraryPicker(view, selectedId) {
        var select = view.querySelector('#GamesRommLibraryId');
        if (!select) return;
        ApiClient.getUserViews(ApiClient.getCurrentUserId()).then(function (result) {
            var html = '<option value="">None</option>';
            (result.Items || []).forEach(function (item) {
                html += '<option value="' + esc(item.Id) + '">' + esc(item.Name || 'Untitled') + '</option>';
            });
            select.innerHTML = html;
            select.value = selectedId;
        });
    }

    function initRommTest(view) {
        var button = view.querySelector('#GamesRommTestBtn');
        if (!button || button.dataset.wired === '1') return;
        button.dataset.wired = '1';
        button.addEventListener('click', function () {
            var statusEl = view.querySelector('#GamesRommStatus');
            var serverUrl = ApiClient.serverAddress ? ApiClient.serverAddress() : '';
            var headers = moonfinAuthHeaders();
            headers['Content-Type'] = 'application/json';
            statusEl.textContent = 'Checking...';
            button.disabled = true;
            fetch(serverUrl + '/Moonfin/Games/Romm/Test', {
                method: 'POST',
                headers: headers,
                body: JSON.stringify({
                    url: view.querySelector('#GamesRommUrl').value.trim(),
                    token: view.querySelector('#GamesRommToken').value.trim()
                })
            })
                .then(function (r) { return r.json(); })
                .then(function (result) {
                    if (!result.ok) {
                        statusEl.textContent = result.error || 'The test failed.';
                        return;
                    }
                    statusEl.textContent = 'Connected to RomM' + (result.version ? ' ' + result.version : '') + '. ' +
                        result.supportedPlatformCount + ' of its ' + result.platformCount + ' platforms play in Moonfin, with ' +
                        result.supportedRomCount + ' games between them.';
                })
                .catch(function () { statusEl.textContent = 'The test failed.'; })
                .finally(function () { button.disabled = false; });
        });
    }

    function loadLibraryPicker(view, pickerSelector, checkboxClass, selectedIds) {
        var picker = view.querySelector(pickerSelector);
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
                    '<input type="checkbox" class="' + checkboxClass + '" data-id="' + item.Id + '"' + (isChecked ? ' checked' : '') + ' style="width:16px;height:16px;">' +
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

    function renderScreensaverPickerRows(picker, cbClass, entries, selectedValues) {
        var html = '';
        for (var i = 0; i < entries.length; i++) {
            var entry = entries[i];
            var isChecked = selectedValues.indexOf(entry.value) !== -1;
            html += '<label style="display:flex;align-items:center;gap:8px;padding:6px 8px;border-radius:4px;cursor:pointer;' + (isChecked ? 'background:rgba(0,164,220,0.1);' : '') + '">' +
                '<input type="checkbox" class="' + cbClass + '" data-value="' + esc(entry.value) + '"' + (isChecked ? ' checked' : '') + ' style="accent-color:#00a4dc;width:16px;height:16px;">' +
                '<div style="flex:1;min-width:0;"><div style="font-size:0.9em;color:rgba(128,128,128,0.9);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">' + esc(entry.label) + '</div></div></label>';
        }
        picker.innerHTML = html;
    }

    function loadScreensaverLibraryPicker(view, selectedIds) {
        var picker = view.querySelector('#DefaultScreensaverLibraryPicker');
        if (!picker) return;
        picker.innerHTML = '<div style="padding:8px;color:rgba(128,128,128,0.5);font-size:0.9em;">Loading...</div>';
        var userId = ApiClient.getCurrentUserId();
        ApiClient.getUserViews(userId).then(function (result) {
            var items = (result.Items || []).filter(function (item) {
                var ct = item.CollectionType;
                return ct === 'movies' || ct === 'tvshows' || ct === 'mixed' || !ct;
            });
            if (items.length === 0) {
                picker.innerHTML = '<div style="padding:8px;color:rgba(128,128,128,0.5);font-size:0.9em;">No media libraries found.</div>';
                return;
            }
            renderScreensaverPickerRows(picker, 'screensaverLibraryCb', items.map(function (item) {
                return { value: item.Id, label: item.Name || 'Untitled' };
            }), selectedIds);
        });
    }

    function loadScreensaverCollectionPicker(view, selectedIds) {
        var picker = view.querySelector('#DefaultScreensaverCollectionPicker');
        if (!picker) return;
        picker.innerHTML = '<div style="padding:8px;color:rgba(128,128,128,0.5);font-size:0.9em;">Loading...</div>';
        var userId = ApiClient.getCurrentUserId();
        ApiClient.getItems(userId, {
            userId: userId,
            includeItemTypes: 'BoxSet',
            sortBy: 'SortName',
            sortOrder: 'Ascending',
            recursive: true,
            limit: 100
        }).then(function (result) {
            var items = result.Items || [];
            if (items.length === 0) {
                picker.innerHTML = '<div style="padding:8px;color:rgba(128,128,128,0.5);font-size:0.9em;">No collections found.</div>';
                return;
            }
            renderScreensaverPickerRows(picker, 'screensaverCollectionCb', items.map(function (item) {
                return { value: item.Id, label: item.Name || 'Untitled' };
            }), selectedIds);
        });
    }

    function loadScreensaverGenrePicker(view, selectedNames) {
        var picker = view.querySelector('#DefaultScreensaverGenrePicker');
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
                // Clients store screensaver genre exclusions by name, not id
                renderScreensaverPickerRows(picker, 'screensaverGenreCb', genres.map(function (g) {
                    var gName = g.name || g.Name || '';
                    return { value: gName, label: gName };
                }), selectedNames);
            })
            .catch(function () {
                picker.innerHTML = '<div style="padding:8px;color:rgba(128,128,128,0.5);font-size:0.9em;">Failed to load genres.</div>';
            });
    }

    function loadRatingSourcesPicker(view, selectedIds, listSelector) {
        var container = view.querySelector(listSelector || '#DefaultMdblistRatingSourcesList');
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

    function getRatingSourcesValue(view, listSelector) {
        var items = view.querySelectorAll((listSelector || '#DefaultMdblistRatingSourcesList') + ' .mdblistRatingItem');
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
        ['auto', 'Server Default'],
        ['ara', 'Arabic'], ['dan', 'Danish'], ['deu', 'German'], ['eng', 'English'],
        ['fin', 'Finnish'], ['fra', 'French'], ['hin', 'Hindi'], ['ita', 'Italian'],
        ['jpn', 'Japanese'], ['kor', 'Korean'], ['nld', 'Dutch'], ['nor', 'Norwegian'],
        ['pol', 'Polish'], ['por', 'Portuguese'], ['rus', 'Russian'], ['spa', 'Spanish'],
        ['swe', 'Swedish'], ['tha', 'Thai'], ['tur', 'Turkish'], ['zho', 'Chinese'],
        ['afr', 'Afrikaans'], ['aka', 'Akan'], ['amh', 'Amharic'], ['asm', 'Assamese'],
        ['aze', 'Azerbaijani'], ['bak', 'Bashkir'], ['bel', 'Belarusian'], ['bem', 'Bemba'],
        ['ben', 'Bengali'], ['bod', 'Tibetan'], ['bos', 'Bosnian'], ['bre', 'Breton'],
        ['bul', 'Bulgarian'], ['cat', 'Catalan'], ['ces', 'Czech'], ['cha', 'Chamorro'],
        ['che', 'Chechen'], ['chv', 'Chuvash'], ['cos', 'Corsican'], ['cre', 'Cree'],
        ['crh', 'Crimean Tatar'], ['cym', 'Welsh'], ['div', 'Divehi'], ['dzo', 'Dzongkha'],
        ['ell', 'Greek'], ['est', 'Estonian'], ['eus', 'Basque'], ['ewe', 'Ewe'],
        ['fao', 'Faroese'], ['fas', 'Persian'], ['fij', 'Fijian'], ['fil', 'Filipino'],
        ['frc', 'French (Canada)'], ['ful', 'Fulah'], ['gla', 'Scottish Gaelic'], ['gle', 'Irish'],
        ['glg', 'Galician'], ['grn', 'Guarani'], ['guj', 'Gujarati'], ['hat', 'Haitian Creole'],
        ['hau', 'Hausa'], ['haw', 'Hawaiian'], ['heb', 'Hebrew'], ['hil', 'Hiligaynon'],
        ['hmn', 'Hmong'], ['hrv', 'Croatian'], ['hun', 'Hungarian'], ['hye', 'Armenian'],
        ['ibo', 'Igbo'], ['iii', 'Sichuan Yi'], ['iku', 'Inuktitut'], ['ind', 'Indonesian'],
        ['isl', 'Icelandic'], ['jav', 'Javanese'], ['kal', 'Kalaallisut'], ['kan', 'Kannada'],
        ['kas', 'Kashmiri'], ['kat', 'Georgian'], ['kau', 'Kanuri'], ['kaz', 'Kazakh'],
        ['khm', 'Khmer'], ['kik', 'Kikuyu'], ['kin', 'Kinyarwanda'], ['kir', 'Kyrgyz'],
        ['kok', 'Konkani'], ['kom', 'Komi'], ['kon', 'Kongo'], ['kua', 'Kuanyama'],
        ['kur', 'Kurdish'], ['lao', 'Lao'], ['lat', 'Latin'], ['lav', 'Latvian'],
        ['lim', 'Limburgish'], ['lin', 'Lingala'], ['lit', 'Lithuanian'], ['ltz', 'Luxembourgish'],
        ['lug', 'Ganda'], ['luo', 'Luo'], ['mal', 'Malayalam'], ['mar', 'Marathi'],
        ['mkd', 'Macedonian'], ['mlg', 'Malagasy'], ['mlt', 'Maltese'], ['mon', 'Mongolian'],
        ['mri', 'Maori'], ['msa', 'Malay'], ['mya', 'Burmese'], ['nav', 'Navajo'],
        ['nbl', 'South Ndebele'], ['nde', 'North Ndebele'], ['ndo', 'Ndonga'], ['nds', 'Low German'],
        ['nep', 'Nepali'], ['new', 'Newari'], ['nno', 'Norwegian Nynorsk'], ['nob', 'Norwegian Bokmål'],
        ['nya', 'Chichewa'], ['oci', 'Occitan'], ['oji', 'Ojibwa'], ['ori', 'Oriya'],
        ['orm', 'Oromo'], ['oss', 'Ossetian'], ['pan', 'Punjabi'], ['pus', 'Pashto'],
        ['que', 'Quechua'], ['roh', 'Romansh'], ['ron', 'Romanian'], ['run', 'Rundi'],
        ['san', 'Sanskrit'], ['sin', 'Sinhala'], ['slk', 'Slovak'], ['slv', 'Slovenian'],
        ['sme', 'Northern Sami'], ['sna', 'Shona'], ['snd', 'Sindhi'], ['som', 'Somali'],
        ['sot', 'Southern Sotho'], ['sqi', 'Albanian'], ['srd', 'Sardinian'], ['srp', 'Serbian'],
        ['ssw', 'Swati'], ['sun', 'Sundanese'], ['swa', 'Swahili'], ['syr', 'Syriac'],
        ['tam', 'Tamil'], ['tat', 'Tatar'], ['tel', 'Telugu'], ['tgk', 'Tajik'],
        ['tgl', 'Tagalog'], ['tir', 'Tigrinya'], ['tsn', 'Tswana'], ['tso', 'Tsonga'],
        ['tuk', 'Turkmen'], ['uig', 'Uyghur'], ['ukr', 'Ukrainian'], ['urd', 'Urdu'],
        ['uzb', 'Uzbek'], ['vie', 'Vietnamese'], ['wol', 'Wolof'], ['xho', 'Xhosa'],
        ['yid', 'Yiddish'], ['yor', 'Yoruba'], ['zha', 'Zhuang'], ['zul', 'Zulu']
    ];

    // Fallback pickers leave auto out because an empty value already means no fallback.
    function fillLanguageSelect(view, selector, excludeAuto) {
        var select = view.querySelector(selector);
        if (!select || select.dataset.filled) return;
        select.dataset.filled = 'true';
        var html = '<option value="">Not set (user decides)</option>';
        for (var i = 0; i < LANGUAGE_OPTIONS.length; i++) {
            var code = LANGUAGE_OPTIONS[i][0];
            if (code === 'auto' && excludeAuto) continue;
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
        { id: 'audio', label: 'Audio' },
        { id: 'subtitles', label: 'Subtitles' },
        { id: 'version', label: 'Version' },
        { id: 'cast', label: 'Cast' },
        { id: 'trailer', label: 'Trailer' },
        { id: 'watchWithGroup', label: 'Watch with group' },
        { id: 'watched', label: 'Watched' },
        { id: 'favorite', label: 'Favorite' },
        { id: 'personalRating', label: 'Rate' },
        { id: 'playlist', label: 'Playlist' },
        { id: 'download', label: 'Download' },
        { id: 'deleteFiles', label: 'Delete files' },
        { id: 'goToSeries', label: 'Go to series' },
        { id: 'seerrWatchlist', label: 'Watchlist' },
        { id: 'seerrReportIssue', label: 'Report Issue' },
        { id: 'seerrManage', label: 'Manage Requests' },
        { id: 'admin', label: 'Admin' }
    ];

    var OSD_BUTTONS = [
        { id: 'syncPlay', label: 'SyncPlay' },
        { id: 'favorite', label: 'Favorite' },
        { id: 'speed', label: 'Playback Speed' },
        { id: 'chapters', label: 'Chapters' },
        { id: 'subtitles', label: 'Subtitles' },
        { id: 'audio', label: 'Audio' },
        { id: 'castAndCrew', label: 'Cast & Crew' },
        { id: 'cast', label: 'Cast' },
        { id: 'volume', label: 'Volume' },
        { id: 'quality', label: 'Quality' },
        { id: 'zoom', label: 'Zoom' },
        { id: 'orientation', label: 'Orientation' },
        { id: 'info', label: 'Info' },
        { id: 'fullscreen', label: 'Fullscreen' },
        { id: 'floatOnTop', label: 'Float on Top' }
    ];

    var DETAIL_METADATA = [
        { id: 'year', label: 'Release Year' },
        { id: 'parentalRating', label: 'Parental Rating' },
        { id: 'runtimeAndSeasons', label: 'Runtime & Seasons' },
        { id: 'status', label: 'Series Status' },
        { id: 'upcomingEpisodeDate', label: 'Upcoming Episodes' },
        { id: 'genres', label: 'Genres' },
        { id: 'seerrAvailability', label: 'Seerr Availability' }
    ];

    // The app's Details screen section ids, which are what the hidden lists store.
    var DETAIL_SECTIONS = [
        { id: 'logo', label: 'Logo' },
        { id: 'tagline', label: 'Tagline' },
        { id: 'poster', label: 'Poster' },
        { id: 'versionBadge', label: 'Version badge' },
        { id: 'upNext', label: 'Next Up' },
        { id: 'lyrics', label: 'Lyrics' },
        { id: 'cast', label: 'Cast' },
        { id: 'crew', label: 'Directors & writers' },
        { id: 'studios', label: 'Studios' },
        { id: 'chapters', label: 'Chapters' },
        { id: 'extras', label: 'Extras' },
        { id: 'collections', label: 'Collections' },
        { id: 'moreLikeThis', label: 'More Like This' },
        { id: 'reviews', label: 'Reviews' },
        { id: 'moreEpisodes', label: 'More episodes' },
        { id: 'mediaInfo', label: 'Media info' },
        { id: 'seerrGenresTags', label: 'Seerr: Genres & tags' },
        { id: 'seerrStats', label: 'Seerr: Stats' },
        { id: 'seerrStreaming', label: 'Seerr: Streaming services' },
        { id: 'seerrRecommendations', label: 'Seerr: Recommendations' },
        { id: 'seerrSimilar', label: 'Seerr: Similar titles' },
        { id: 'seerrCollection', label: 'Seerr: Collection banner' },
        { id: 'seerrPersonAppearances', label: 'Seerr: Appearances' },
        { id: 'seerrPersonCrew', label: 'Seerr: Crew credits' },
        { id: 'biography', label: 'Person pages: Biography' },
        { id: 'birthplace', label: 'Person pages: Birthplace' },
        { id: 'personLinks', label: 'Person pages: Links (phones and desktops only)' },
        { id: 'guestAppearances', label: 'Person pages: Guest appearances' },
        { id: 'musicVideos', label: 'Person pages: Music videos' },
        { id: 'playlistOrder', label: 'Collection pages: Playlist order' },
        { id: 'bookGenres', label: 'Book genres' },
        { id: 'photoExif', label: 'Photo details' }
    ];

    function loadButtonPicker(view, selector, buttons, order, hidden, fixedOrder) {
        var container = view.querySelector(selector);
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
                var btn = buttons.find(function (b) { return b.id === id; });
                if (btn) {
                    ordered.push(entryFor(btn));
                    addedSet[btn.id] = true;
                }
            });
        }

        buttons.forEach(function (btn) {
            if (!addedSet[btn.id]) {
                ordered.push(entryFor(btn));
            }
        });

        var isConfigured = !!((order && order.length) || (hidden && hidden.length));

        container.innerHTML = '';

        var bar = document.createElement('div');
        bar.className = 'detailButtonPickerBar';
        bar.style.cssText = 'display:flex;align-items:center;gap:8px;margin-bottom:6px;';
        bar.innerHTML =
            '<span class="detailButtonPickerState fieldDescription" style="flex:1;margin:0;"></span>' +
            '<button type="button" class="detailButtonClearBtn" style="background:none;border:1px solid rgba(128,128,128,0.2);border-radius:3px;color:rgba(128,128,128,0.7);padding:2px 8px;cursor:pointer;font-size:0.85em;">Clear</button>';
        container.appendChild(bar);

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
                (fixedOrder ? '' :
                    '<button type="button" class="detailButtonMoveBtn" data-dir="up" style="background:none;border:1px solid rgba(128,128,128,0.2);border-radius:3px;color:rgba(128,128,128,0.7);padding:1px 6px;cursor:pointer;font-size:0.85em;">&#x2191;</button>' +
                    '<button type="button" class="detailButtonMoveBtn" data-dir="down" style="background:none;border:1px solid rgba(128,128,128,0.2);border-radius:3px;color:rgba(128,128,128,0.7);padding:1px 6px;cursor:pointer;font-size:0.85em;">&#x2193;</button>');
            container.appendChild(row);
        });

        setButtonPickerConfigured(container, isConfigured);

        if (!container.dataset.hasListener) {
            container.dataset.hasListener = 'true';
            container.addEventListener('click', function (e) {
                if (e.target.closest('.detailButtonClearBtn')) {
                    // Back to not set, so the push stops carrying a layout at all.
                    loadButtonPicker(view, selector, buttons, null, null, fixedOrder);
                    return;
                }

                var btn = e.target.closest('.detailButtonMoveBtn');
                if (!btn) return;
                var row = btn.closest('.detailButtonItem');
                if (!row) return;
                if (btn.dataset.dir === 'up' && row.previousElementSibling
                    && row.previousElementSibling.classList.contains('detailButtonItem')) {
                    container.insertBefore(row, row.previousElementSibling);
                    setButtonPickerConfigured(container, true);
                } else if (btn.dataset.dir === 'down' && row.nextElementSibling) {
                    container.insertBefore(row.nextElementSibling, row);
                    setButtonPickerConfigured(container, true);
                }
            });

            container.addEventListener('change', function (e) {
                if (e.target && e.target.type === 'checkbox') {
                    setButtonPickerConfigured(container, true);
                }
            });
        }
    }

    function setButtonPickerConfigured(container, configured) {
        container.dataset.configured = configured ? 'true' : 'false';
        var state = container.querySelector('.detailButtonPickerState');
        var clear = container.querySelector('.detailButtonClearBtn');
        if (state) {
            state.textContent = configured
                ? 'Set by you. Applying defaults will replace each user\'s button layout.'
                : 'Not set. Users keep their own button layout.';
        }
        if (clear) {
            clear.style.display = configured ? '' : 'none';
        }
    }

    function getButtonPickerValue(view, selector) {
        var container = view.querySelector(selector);

        // An untouched picker sends nothing. It renders every button either way, so its
        // contents would read as a choice and overwrite what each user arranged.
        if (!container || container.dataset.configured !== 'true') {
            return { order: null, hidden: null };
        }

        var items = container.querySelectorAll('.detailButtonItem');
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


    // Same order and wording as LibrarySortBy in Core. The two flagged
    // sorts run through their own endpoint, so only the rows that read
    // that endpoint offer them.
    var LIBRARY_SORT_OPTIONS = [
        ['playlistOrder', 'Playlist Order', true],
        ['name', 'Name'],
        ['dateAdded', 'Date Added'],
        ['dateEpisodeAdded', 'Date Episode Added'],
        ['premiereDate', 'Release Date'],
        ['rating', 'Rating'],
        ['runtime', 'Runtime'],
        ['random', 'Random'],
        ['criticRating', 'Critic Rating'],
        ['communityRating', 'Community Rating'],
        ['myRating', 'My Rating', true],
        ['datePlayed', 'Last Played'],
        ['playCount', 'Play Count'],
        ['albumArtist', 'Album Artist'],
        ['album', 'Album'],
        ['artist', 'Artist'],
        ['trackNumber', 'Number'],
        ['genre', 'Genre'],
        ['foldersFirst', 'Folders First']
    ];

    function fillSortSelect(view, selector, includeDedicated) {
        var select = view.querySelector(selector);
        if (!select || select.dataset.filled) return;
        select.dataset.filled = 'true';
        var html = '<option value="">Not set (user decides)</option>';
        LIBRARY_SORT_OPTIONS.forEach(function (option) {
            if (option[2] && !includeDedicated) return;
            html += '<option value="' + esc(option[0]) + '">' + esc(option[1]) + '</option>';
        });
        select.innerHTML = html;
    }

    var EXTERNAL_ROW_SORT_OPTIONS = [
        ['none', 'Default (List Order)'],
        ['name', 'Name'],
        ['releaseDate', 'Release Date'],
        ['rating', 'Rating'],
        ['popularity', 'Popularity'],
        ['shuffle', 'Shuffle']
    ];

    function fillExternalRowSortSelect(view, selector) {
        var select = view.querySelector(selector);
        if (!select || select.dataset.filled) return;
        select.dataset.filled = 'true';
        var html = '<option value="">Not set (user decides)</option>';
        EXTERNAL_ROW_SORT_OPTIONS.forEach(function (option) {
            html += '<option value="' + esc(option[0]) + '">' + esc(option[1]) + '</option>';
        });
        select.innerHTML = html;
    }

    var SEGMENT_ACTIONS = [
        ['doNothing', 'Do nothing'],
        ['skip', 'Skip automatically'],
        ['askToSkip', 'Ask before skipping']
    ];

    var SEGMENT_TYPES = ['intro', 'recap', 'preview', 'commercial', 'outro'];

    function segmentActionSelector(type) {
        return '#DefaultSegmentAction' + type.charAt(0).toUpperCase() + type.slice(1);
    }

    function loadSegmentActions(view, raw) {
        var stored = {};
        String(raw || '').split(',').forEach(function(part) {
            var pair = part.split(':');
            if (pair.length !== 2) return;
            var name = pair[0].trim().toLowerCase();
            if (name) stored[name] = pair[1].trim();
        });
        SEGMENT_TYPES.forEach(function (type) {
            var el = view.querySelector(segmentActionSelector(type));
            if (!el) return;
            if (!el.dataset.filled) {
                el.dataset.filled = 'true';
                var html = '<option value="">Not set (user decides)</option>';
                SEGMENT_ACTIONS.forEach(function (action) {
                    html += '<option value="' + esc(action[0]) + '">' + esc(action[1]) + '</option>';
                });
                el.innerHTML = html;
            }
            el.value = stored[type] || '';
        });
    }

    // Written in the order the clients write it, so a value that came
    // from a client and goes back untouched is unchanged.
    function getSegmentActionsValue(view) {
        var parts = [];
        SEGMENT_TYPES.forEach(function(type) {
            var el = view.querySelector(segmentActionSelector(type));
            if (el && el.value) parts.push(type + ':' + el.value);
        });
        return parts.length > 0 ? parts.join(',') : null;
    }

    var HOME_ROW_IMAGE_TYPE_OPTIONS = '<option value="">Not set (user decides)</option>'
        + '<option value="poster">Poster</option>'
        + '<option value="thumb">Thumbnail</option>'
        + '<option value="banner">Banner</option>';

    // Continue Watching has its own setting above, and Library Buttons has no artwork to pick.
    var HOME_ROW_IMAGE_TYPE_SKIPPED = { resume: true, librarybuttons: true };

    function loadHomeRowImageTypes(view, raw) {
        var stored = {};
        String(raw || '').split(',').forEach(function (part) {
            var pair = part.split(':');
            if (pair.length !== 2) return;
            var name = pair[0].trim().toLowerCase();
            if (name) stored[name] = pair[1].trim().toLowerCase();
        });
        var container = view.querySelector('#DefaultHomeRowImageTypes');
        if (!container) return;
        if (!container.dataset.filled) {
            container.dataset.filled = 'true';
            var html = '';
            HOME_SECTION_DEFINITIONS.forEach(function (row) {
                if (HOME_ROW_IMAGE_TYPE_SKIPPED[row.type]) return;
                var id = 'DefaultHomeRowImageType_' + row.type;
                html += '<div class="inputContainer">'
                    + '<label class="inputLabel inputLabelUnfocused" for="' + esc(id) + '">' + esc(row.label) + '</label>'
                    + '<select id="' + esc(id) + '" is="emby-select" data-row="' + esc(row.type) + '">' + HOME_ROW_IMAGE_TYPE_OPTIONS + '</select>'
                    + '</div>';
            });
            container.innerHTML = html;
        }
        container.querySelectorAll('select[data-row]').forEach(function (el) {
            el.value = stored[el.dataset.row] || '';
        });
    }

    function getHomeRowImageTypesValue(view) {
        var parts = [];
        view.querySelectorAll('#DefaultHomeRowImageTypes select[data-row]').forEach(function (el) {
            if (el.value) parts.push(el.dataset.row + ':' + el.value);
        });
        return parts.length > 0 ? parts.join(',') : null;
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
        { type: 'onnow', label: 'On Now' },
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
        { type: 'studios', label: 'Studios' },
        { type: 'playlists', label: 'Playlists' },
        { type: 'audioartists', label: 'Artists' },
        { type: 'audioalbums', label: 'Albums' },
        { type: 'audioplaylists', label: 'Audio Playlists' },
        { type: 'sinceyouwatched1', label: 'Since You Watched 1' },
        { type: 'sinceyouwatched2', label: 'Since You Watched 2' },
        { type: 'sinceyouwatched3', label: 'Since You Watched 3' },
        { type: 'sinceyouwatched4', label: 'Since You Watched 4' },
        { type: 'sinceyouwatched5', label: 'Since You Watched 5' },
        { type: 'rewatch', label: 'Rewatch' },
        { type: 'seasonal', label: 'Seasonal' },
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
        { type: 'seerr_watchlist', label: 'Seerr Watchlist' },
        { type: 'imdb_top_250_movies', label: 'IMDB Top 250 Movies' },
        { type: 'imdb_top_250_tv_shows', label: 'IMDB Top 250 Shows' },
        { type: 'imdb_most_popular_movies', label: 'IMDB Most Popular Movies' },
        { type: 'imdb_most_popular_tv_shows', label: 'IMDB Most Popular Shows' },
        { type: 'imdb_lowest_rated_movies', label: 'IMDB Lowest Rated Movies' },
        { type: 'imdb_top_english_movies', label: 'IMDB Top English Movies' },
        { type: 'tmdb_popular_movies', label: 'TMDB Popular Movies' },
        { type: 'tmdb_top_rated_movies', label: 'TMDB Top Rated Movies' },
        { type: 'tmdb_now_playing_movies', label: 'TMDB Now Playing Movies' },
        { type: 'tmdb_upcoming_movies', label: 'TMDB Upcoming Movies' },
        { type: 'tmdb_popular_tv', label: 'TMDB Popular TV' },
        { type: 'tmdb_top_rated_tv', label: 'TMDB Top Rated TV' },
        { type: 'tmdb_airing_today_tv', label: 'TMDB Airing Today TV' },
        { type: 'tmdb_on_the_air_tv', label: 'TMDB On The Air TV' },
        { type: 'tmdb_trending_movie_daily', label: 'TMDB Trending Movies (Daily)' },
        { type: 'tmdb_trending_movie_weekly', label: 'TMDB Trending Movies (Weekly)' },
        { type: 'tmdb_trending_tv_daily', label: 'TMDB Trending TV (Daily)' },
        { type: 'tmdb_trending_tv_weekly', label: 'TMDB Trending TV (Weekly)' },
        { type: 'tmdb_trending_all_weekly', label: 'TMDB Trending All (Weekly)' },
        { type: 'radarr_calendar', label: 'Radarr Upcoming' },
        { type: 'sonarr_calendar', label: 'Sonarr Upcoming' }
    ];

    var HOME_LAYOUT_TABS = [
        { id: 'builtin', label: 'Basics' },
        { id: 'collections', label: 'Collections' },
        { id: 'playlists', label: 'Playlists' },
        { id: 'genres', label: 'Genres' },
        { id: 'seerr', label: 'Seerr' },
        { id: 'external', label: 'External Lists' },
        { id: 'custom', label: 'Custom Lists' }
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
        seerr_watchlist: true,
        radarr_calendar: true,
        sonarr_calendar: true
    };

    function isSeerrHomeSectionType(type) {
        return !!HOME_LAYOUT_SEERR_TYPES[type];
    }

    function isExternalHomeSectionType(type) {
        return /^(imdb|tmdb)_/.test(type);
    }

    function getHomeLayoutState(view) {
        if (!view.__moonfinHomeLayout) {
            view.__moonfinHomeLayout = {
                sections: [],
                selectedIndex: null,
                activeTab: 'builtin',
                search: '',
                available: { builtin: [], collections: [], playlists: [], genres: [], seerr: [], external: [] },
                customList: { sortOrder: 'desc', editingSection: null, busy: false }
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
            return isSeerrHomeSectionType(section.type) ? 'Seerr' : (isExternalHomeSectionType(section.type) ? 'External Lists' : 'Basics');
        }
        if (section.pluginSource === 'collections') return 'Collection';
        if (section.pluginSource === 'playlists') return 'Playlist';
        if (section.pluginSource === 'genres') return 'Genre';
        if (section.pluginSource === 'hss') return 'HSS';
        if (section.pluginSource === 'custom') return 'Custom List';
        return 'Dynamic';
    }

    function homeSectionBadgeClass(section) {
        if (!section || section.kind !== 'pluginDynamic') {
            if (section && isSeerrHomeSectionType(section.type)) return 'homeLayoutBadge-seerr';
            if (section && isExternalHomeSectionType(section.type)) return 'homeLayoutBadge-external';
            return 'homeLayoutBadge-builtin';
        }
        if (section.pluginSource === 'collections') return 'homeLayoutBadge-collections';
        if (section.pluginSource === 'playlists') return 'homeLayoutBadge-playlists';
        if (section.pluginSource === 'genres') return 'homeLayoutBadge-genres';
        if (section.pluginSource === 'custom') return 'homeLayoutBadge-custom';
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
            meta: homeSectionMeta({ kind: 'builtin', type: definition.type }),
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
            .filter(function (definition) { return !isSeerrHomeSectionType(definition.type) && !isExternalHomeSectionType(definition.type); })
            .map(createBuiltinCandidate);
        state.available.seerr = HOME_SECTION_DEFINITIONS
            .filter(function (definition) { return isSeerrHomeSectionType(definition.type); })
            .map(createBuiltinCandidate);
        state.available.external = HOME_SECTION_DEFINITIONS
            .filter(function (definition) { return isExternalHomeSectionType(definition.type); })
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
        if (state.sections[index] === customListState(view).editingSection) {
            resetCustomListForm(view);
        }
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
            var editable = isCustomListSection(section);
            var row = document.createElement('div');
            row.className = 'homeLayoutRow' +
                (editable ? ' has-edit' : '') +
                (state.selectedIndex === index ? ' is-selected' : '');
            row.dataset.index = String(index);
            row.innerHTML =
                '<div class="homeLayoutRowText">' +
                '<div class="homeLayoutRowTitle">' + esc(homeSectionLabel(section)) + '</div>' +
                '<span class="homeLayoutBadge ' + homeSectionBadgeClass(section) + '">' + esc(homeSectionMeta(section)) + '</span>' +
                '</div>' +
                (editable ? '<button type="button" class="homeLayoutIconButton" data-action="edit" title="Edit">&#x270E;</button>' : '') +
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

        // Custom lists are made with a form rather than picked from a catalog.
        var isCustomTab = tab === 'custom';
        var customForm = view.querySelector('#DefaultHomeCustomListForm');
        var searchInput = view.querySelector('#DefaultHomeAvailableSearch');
        container.style.display = isCustomTab ? 'none' : '';
        if (searchInput) searchInput.style.display = isCustomTab ? 'none' : '';
        if (customForm) customForm.style.display = isCustomTab ? '' : 'none';
        if (isCustomTab) {
            refreshCustomListForm(view);
            return;
        }

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
        bindCustomListForm(view);
        resetCustomListForm(view);
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
                if (action === 'edit') editCustomListSection(view, index);
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

    // ── Custom lists ────────────────────────────────────────────────────────
    // The form mirrors Add Custom Home Row in the apps, and writes the same row they do,
    // so a list an admin adds here loads in every app the way a user's own would.

    var CUSTOM_LIST_TYPES = {
        tmdb: [['user_list', 'List from TMDB List ID or URL'], ['movie_collection', 'Collection from TMDB Collection ID or URL']],
        letterboxd: [['user_diary', 'Diary']],
        mdblist: [['list_url', 'List from URL'], ['user_list', 'List from User ID']]
    };

    var CUSTOM_LIST_SORT_ORDERS = {
        title: [['asc', 'A to Z'], ['desc', 'Z to A']],
        year: [['desc', 'Newest First'], ['asc', 'Earliest First']],
        popularity: [['desc', 'Most Popular First'], ['asc', 'Least Popular First']],
        rating: [['desc', 'Highest First'], ['asc', 'Lowest First']]
    };

    function customListState(view) {
        return getHomeLayoutState(view).customList;
    }

    function customListEl(view, id) {
        return view.querySelector('#' + id);
    }

    function showCustomListEl(view, id, shown) {
        var el = customListEl(view, id);
        if (el) el.style.display = shown ? '' : 'none';
    }

    function isCustomListSection(section) {
        return !!section && section.kind === 'pluginDynamic' && section.pluginSource === 'custom';
    }

    function setCustomListOptions(select, options, value) {
        select.innerHTML = options.map(function (option) {
            return '<option value="' + esc(option[0]) + '">' + esc(option[1]) + '</option>';
        }).join('');
        var known = options.some(function (option) { return option[0] === value; });
        select.value = known ? value : options[0][0];
    }

    function refreshCustomListForm(view, preferredType) {
        var sourceSelect = customListEl(view, 'DefaultCustomListSource');
        var typeSelect = customListEl(view, 'DefaultCustomListType');
        if (!sourceSelect || !typeSelect) return;

        var source = sourceSelect.value;
        var types = CUSTOM_LIST_TYPES[source];
        setCustomListOptions(typeSelect, types, preferredType || typeSelect.value);
        var type = typeSelect.value;
        showCustomListEl(view, 'DefaultCustomListTypeField', types.length > 1);

        showCustomListEl(view, 'DefaultCustomListLetterboxdField', source === 'letterboxd');
        showCustomListEl(view, 'DefaultCustomListTmdbField', source === 'tmdb');
        showCustomListEl(view, 'DefaultCustomListMdblistUrlField', source === 'mdblist' && type === 'list_url');
        showCustomListEl(view, 'DefaultCustomListMdblistUserField', source === 'mdblist' && type !== 'list_url');
        var isCollection = type === 'movie_collection';
        customListEl(view, 'DefaultCustomListTmdbIdLabel').textContent = isCollection ? 'TMDB Collection ID or URL' : 'TMDB List ID or URL';
        customListEl(view, 'DefaultCustomListTmdbId').placeholder = isCollection
            ? '12345 OR https://www.themoviedb.org/collection/12345'
            : '12345 OR https://www.themoviedb.org/list/12345';

        var sortOrders = CUSTOM_LIST_SORT_ORDERS[customListEl(view, 'DefaultCustomListSortBy').value];
        showCustomListEl(view, 'DefaultCustomListSortOrderField', !!sortOrders);
        if (sortOrders) {
            setCustomListOptions(customListEl(view, 'DefaultCustomListSortOrder'), sortOrders, customListState(view).sortOrder);
        }

        showCustomListEl(view, 'DefaultCustomListUserRatingsField', source === 'letterboxd');

        // The server's keys cover users who haven't set their own.
        var tmdbKey = ((customListEl(view, 'TmdbApiKey') || {}).value || '').trim();
        var mdblistKey = ((customListEl(view, 'MdblistApiKey') || {}).value || '').trim();
        var warning = '';
        if (source === 'letterboxd' && !tmdbKey) {
            warning = 'Letterboxd lists need a TMDB API key to resolve their titles. Set one on the Integrations tab, or users need their own.';
        } else if (source === 'tmdb' && !tmdbKey) {
            warning = 'TMDB lists need a TMDB API key. Set one on the Integrations tab, or users need their own.';
        } else if (source === 'mdblist' && !mdblistKey) {
            warning = 'MDBList lists need an MDBList API key. Set one on the Integrations tab, or users need their own.';
        }
        var warningEl = customListEl(view, 'DefaultCustomListKeyWarning');
        warningEl.textContent = warning;
        warningEl.style.display = warning ? '' : 'none';
    }

    function showCustomListResult(view, message, isError) {
        var result = customListEl(view, 'DefaultCustomListResult');
        if (!result) return;
        result.textContent = message || '';
        result.classList.toggle('is-error', !!isError);
        result.style.display = message ? '' : 'none';
    }

    function setCustomListBusy(view, busy) {
        customListState(view).busy = busy;
        customListEl(view, 'DefaultCustomListSave').disabled = busy;
        customListEl(view, 'DefaultCustomListCancel').disabled = busy;
    }

    function updateCustomListButtons(view) {
        var editing = !!customListState(view).editingSection;
        customListEl(view, 'DefaultCustomListSave').textContent = editing ? 'Save Changes' : 'Add to Layout';
        showCustomListEl(view, 'DefaultCustomListCancel', editing);
    }

    function clearCustomListFields(view) {
        ['DefaultCustomListLetterboxdUser', 'DefaultCustomListTmdbId', 'DefaultCustomListMdblistUrl',
            'DefaultCustomListMdblistUser', 'DefaultCustomListMdblistList', 'DefaultCustomListName'].forEach(function (id) {
            customListEl(view, id).value = '';
        });
    }

    function resetCustomListForm(view) {
        if (!customListEl(view, 'DefaultCustomListSource')) return;
        var form = customListState(view);
        form.editingSection = null;
        form.sortOrder = 'desc';
        clearCustomListFields(view);
        customListEl(view, 'DefaultCustomListSource').value = 'tmdb';
        customListEl(view, 'DefaultCustomListSortBy').value = 'none';
        customListEl(view, 'DefaultCustomListUserRatings').checked = false;
        showCustomListResult(view, '', false);
        updateCustomListButtons(view);
        refreshCustomListForm(view, 'user_list');
    }

    function fillCustomListForm(view, section) {
        var rowConfig = {};
        try {
            rowConfig = JSON.parse(section.pluginAdditionalData || '{}') || {};
        } catch (e) {
            rowConfig = {};
        }

        var source = rowConfig.source || 'tmdb';
        var type = rowConfig.type || 'user_list';
        // The apps no longer offer IMDb here and open an old IMDb row as a TMDB list.
        if (source === 'imdb') {
            source = 'tmdb';
            type = 'user_list';
        }
        if (!CUSTOM_LIST_TYPES[source]) source = 'tmdb';
        var params = rowConfig.params || {};

        clearCustomListFields(view);
        customListEl(view, 'DefaultCustomListSource').value = source;
        if (source === 'letterboxd') {
            customListEl(view, 'DefaultCustomListLetterboxdUser').value = params.user || '';
        } else if (source === 'tmdb') {
            customListEl(view, 'DefaultCustomListTmdbId').value = params.id || '';
        } else if (source === 'mdblist') {
            customListEl(view, 'DefaultCustomListMdblistUser').value = params.username || '';
            if (type === 'list_url') {
                customListEl(view, 'DefaultCustomListMdblistUrl').value = params.username && params.listname
                    ? 'https://mdblist.com/lists/' + params.username + '/' + params.listname
                    : '';
            } else {
                customListEl(view, 'DefaultCustomListMdblistList').value = params.listname || '';
            }
        }

        var sortBy = rowConfig.sort_by || 'none';
        var sortBySelect = customListEl(view, 'DefaultCustomListSortBy');
        sortBySelect.value = sortBy;
        if (sortBySelect.value !== sortBy) sortBySelect.value = 'none';
        customListState(view).sortOrder = rowConfig.sort_order === 'asc' ? 'asc' : 'desc';
        customListEl(view, 'DefaultCustomListUserRatings').checked = rowConfig.show_user_ratings === true;
        customListEl(view, 'DefaultCustomListName').value = section.pluginDisplayText || '';
        showCustomListResult(view, '', false);
        refreshCustomListForm(view, type);
    }

    function customListParams(view, source, type) {
        var params = {};
        if (source === 'letterboxd') {
            params.user = customListEl(view, 'DefaultCustomListLetterboxdUser').value.trim().toLowerCase();
        } else if (source === 'tmdb') {
            var rawId = customListEl(view, 'DefaultCustomListTmdbId').value.trim();
            if (rawId.indexOf('/') !== -1 || rawId.indexOf('http') === 0) {
                if (rawId.charAt(rawId.length - 1) === '/') rawId = rawId.slice(0, -1);
                var idParts = rawId.split('/');
                rawId = idParts[idParts.length - 1];
            }
            params.id = rawId;
        } else if (source === 'mdblist') {
            if (type === 'list_url') {
                // Takes https://mdblist.com/lists/{username}/{listname} with an optional trailing
                // slash, query or fragment. Anchoring on /lists/ keeps extra path segments out.
                var rawUrl = customListEl(view, 'DefaultCustomListMdblistUrl').value.trim();
                var queryStart = rawUrl.search(/[?#]/);
                if (queryStart >= 0) rawUrl = rawUrl.slice(0, queryStart);
                while (rawUrl.charAt(rawUrl.length - 1) === '/') rawUrl = rawUrl.slice(0, -1);
                var urlParts = rawUrl.split('/').filter(function (part) { return part.length > 0; });
                var parsedUser = '';
                var parsedList = '';
                var listsIndex = urlParts.lastIndexOf('lists');
                if (listsIndex >= 0 && urlParts.length >= listsIndex + 3) {
                    parsedUser = urlParts[listsIndex + 1];
                    parsedList = urlParts[listsIndex + 2];
                } else if (urlParts.length >= 2) {
                    parsedList = urlParts[urlParts.length - 1];
                    parsedUser = urlParts[urlParts.length - 2];
                }
                params.username = parsedUser;
                params.listname = parsedList;
            } else {
                params.username = customListEl(view, 'DefaultCustomListMdblistUser').value.trim();
                params.listname = customListEl(view, 'DefaultCustomListMdblistList').value.trim();
            }
        }
        return params;
    }

    function customListSourceUrl(source, type, params) {
        if (source === 'tmdb') {
            return 'https://www.themoviedb.org/' + (type === 'movie_collection' ? 'collection/' : 'list/') + params.id;
        }
        if (source === 'letterboxd') return 'https://letterboxd.com/' + params.user + '/';
        return 'https://mdblist.com/lists/' + params.username + '/' + params.listname + '/';
    }

    function saveCustomListRow(view) {
        var form = customListState(view);
        if (form.busy) return;
        var name = customListEl(view, 'DefaultCustomListName').value.trim();
        if (!name) {
            showCustomListResult(view, 'Give the row a name first.', true);
            return;
        }

        var source = customListEl(view, 'DefaultCustomListSource').value;
        var type = customListEl(view, 'DefaultCustomListType').value;
        var params = customListParams(view, source, type);
        var additionalData = JSON.stringify({
            source: source,
            type: type,
            params: params,
            sort_by: customListEl(view, 'DefaultCustomListSortBy').value,
            sort_order: form.sortOrder,
            show_user_ratings: customListEl(view, 'DefaultCustomListUserRatings').checked
        });

        // Checked the way the apps check it before saving, so a list that can't load never
        // reaches anyone's home screen.
        var url = ApiClient.getUrl('Moonfin/CustomRows/Items', {
            source: source,
            type: type,
            params: JSON.stringify(params)
        });
        setCustomListBusy(view, true);
        showCustomListResult(view, 'Checking the list...', false);
        fetch(url, { method: 'GET', headers: moonfinAuthHeaders() })
            .then(parseJsonResponse)
            .then(function (payload) {
                if (payload.success === false) throw new Error(payload.error || 'The list could not be fetched.');
                if (!payload.items || payload.items.length === 0) throw new Error('No results returned.');
                setCustomListBusy(view, false);
                commitCustomListRow(view, name, additionalData);
            })
            .catch(function (error) {
                setCustomListBusy(view, false);
                showCustomListResult(view, 'Couldn\'t fetch items from ' + customListSourceUrl(source, type, params) +
                    '. ' + ((error && error.message) || error), true);
            });
    }

    function commitCustomListRow(view, name, additionalData) {
        var state = getHomeLayoutState(view);
        var editing = customListState(view).editingSection;
        var editingIndex = editing ? state.sections.indexOf(editing) : -1;
        var section = {
            kind: 'pluginDynamic',
            type: 'none',
            enabled: true,
            order: 0,
            serverId: 'custom',
            pluginSource: 'custom',
            pluginSection: editingIndex >= 0 && editing.pluginSection ? editing.pluginSection : 'custom_row_' + Date.now(),
            pluginAdditionalData: additionalData,
            pluginDisplayText: name
        };

        if (editingIndex >= 0) {
            state.sections[editingIndex] = section;
            state.selectedIndex = editingIndex;
        } else {
            var insertIndex = getHomeLayoutInsertIndex(state);
            state.sections.splice(insertIndex, 0, section);
            state.selectedIndex = insertIndex;
        }

        renumberHomeLayoutSections(state);
        resetCustomListForm(view);
        showCustomListResult(view, (editingIndex >= 0 ? 'Updated ' : 'Added ') + name + '. Save to keep it.', false);
        renderHomeSectionsEditor(view);
        scrollSelectedHomeLayoutRowIntoView(view);
    }

    function editCustomListSection(view, index) {
        var state = getHomeLayoutState(view);
        var section = state.sections[index];
        if (!isCustomListSection(section)) return;
        customListState(view).editingSection = section;
        fillCustomListForm(view, section);
        updateCustomListButtons(view);
        state.selectedIndex = index;
        state.activeTab = 'custom';
        renderHomeSectionsEditor(view);
    }

    function bindCustomListForm(view) {
        var form = customListEl(view, 'DefaultHomeCustomListForm');
        if (!form || form.dataset.bound) return;
        form.dataset.bound = 'true';

        customListEl(view, 'DefaultCustomListSource').addEventListener('change', function (event) {
            refreshCustomListForm(view, CUSTOM_LIST_TYPES[event.target.value][0][0]);
        });
        customListEl(view, 'DefaultCustomListType').addEventListener('change', function () {
            refreshCustomListForm(view);
        });
        customListEl(view, 'DefaultCustomListSortBy').addEventListener('change', function (event) {
            customListState(view).sortOrder = event.target.value === 'title' ? 'asc' : 'desc';
            refreshCustomListForm(view);
        });
        customListEl(view, 'DefaultCustomListSortOrder').addEventListener('change', function (event) {
            customListState(view).sortOrder = event.target.value;
        });
        customListEl(view, 'DefaultCustomListSave').addEventListener('click', function () {
            saveCustomListRow(view);
        });
        customListEl(view, 'DefaultCustomListCancel').addEventListener('click', function () {
            resetCustomListForm(view);
        });
        // Enter would otherwise submit the whole settings form.
        form.addEventListener('keydown', function (event) {
            if (event.key !== 'Enter' || event.target.tagName !== 'INPUT') return;
            event.preventDefault();
            saveCustomListRow(view);
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
            updateSyncStatus(view);
            view.querySelector('#SeerrEnabled').checked = config.SeerrEnabled;
            view.querySelector('#SeerrUrl').value = config.SeerrUrl || '';
            view.querySelector('#SeerrDisplayName').value = config.SeerrDisplayName || '';
            view.querySelector('#PublicServerUrl').value = config.PublicServerUrl || '';
            view.querySelector('#MdblistApiKey').value = config.MdblistApiKey || '';
            view.querySelector('#MdblistOfficialListsEnabled').checked = config.MdblistOfficialListsEnabled !== false;
            view.querySelector('#MdblistOfficialListsMaxItems').value = config.MdblistOfficialListsMaxItems || 250;
            view.querySelector('#TmdbApiKey').value = config.TmdbApiKey || '';
            view.querySelector('#ImdbListsEnabled').checked = config.ImdbListsEnabled !== false;
            view.querySelector('#StudioLogosEnabled').checked = config.StudioLogosEnabled !== false;
            view.querySelector('#StudioLogosMaxAgeDays').value = config.StudioLogosMaxAgeDays || 30;
            view.querySelector('#AnimeMarkersEnabled').checked = config.AnimeMarkersEnabled === true;
            view.querySelector('#AnimeMarkerRecapLookup').checked = config.AnimeMarkerRecapLookup !== false;
            view.querySelector('#AnimeAudioMarkersEnabled').checked = config.AnimeAudioMarkersEnabled === true;
            view.querySelector('#AnimeAudioMarkersMovies').checked = config.AnimeAudioMarkersMovies === true;
            view.querySelector('#AnimeAudioSeparateDualAudio').checked = config.AnimeAudioSeparateDualAudio === true;
            view.querySelector('#AnimeAudioTrustSelectedLibraries').checked = config.AnimeAudioTrustSelectedLibraries === true;
            view.querySelector('#AnimeMarkerPlacement').value = config.AnimeMarkerPlacement || 'below';
            view.querySelector('#AnimeAudioLabelStyle').value = config.AnimeAudioLabelStyle || 'full';
            view.querySelector('#AnimeMarkerShowFiller').checked = config.AnimeMarkerShowFiller !== false;
            view.querySelector('#AnimeMarkerShowMixed').checked = config.AnimeMarkerShowMixed !== false;
            view.querySelector('#AnimeMarkerShowRecap').checked = config.AnimeMarkerShowRecap !== false;
            view.querySelector('#AnimeMarkerShowMangaCanon').checked = config.AnimeMarkerShowMangaCanon === true;
            view.querySelector('#AnimeMarkerShowAnimeCanon').checked = config.AnimeMarkerShowAnimeCanon === true;
            view.querySelector('#AnimeMarkerMaxAgeDays').value = config.AnimeMarkerMaxAgeDays || 30;
            view.querySelector('#AnimeMarkerVerboseLogging').checked = config.AnimeMarkerVerboseLogging === true;
            loadLibraryPicker(view, '#AnimeMarkerLibraryPicker', 'animeMarkerLibraryCb', config.AnimeMarkerLibraryIds || []);
            view.querySelector('#FcmServiceAccountJson').value = config.FcmServiceAccountJson || '';
            view.querySelector('#FcmServiceAccountPath').value = config.FcmServiceAccountPath || '';
            view.querySelector('#PushRelayUrl').value = config.PushRelayUrl || '';
            view.querySelector('#PushRelayAppKey').value = config.PushRelayAppKey || '';
            view.querySelector('#WebDefaultServerUrl').value = config.WebDefaultServerUrl || '';
            view.querySelector('#WebForcedServerUrl').value = config.WebForcedServerUrl || '';
            view.querySelector('#WebEnableWebRtcScan').checked = config.WebEnableWebRtcScan !== false;
            view.querySelector('#EnableClientLogUpload').checked = config.EnableClientLogUpload !== false;
            view.querySelector('#LiveTvDetectIncompleteRecordings').checked = config.LiveTvDetectIncompleteRecordings !== false;
            view.querySelector('#LiveTvAutoRecordCompleteShowing').checked = config.LiveTvAutoRecordCompleteShowing === true;
            view.querySelector('#LiveTvIncompleteThresholdPercent').value = config.LiveTvIncompleteThresholdPercent || 90;
            view.querySelector('#LiveTvDeleteIncompleteCopies').checked = config.LiveTvDeleteIncompleteCopies === true;

            loadLibraryPicker(view, '#DefaultHiddenLibraryPicker', 'defaultHiddenLibraryCb', config.DefaultHiddenLibraryIds || []);

            view.querySelector('#GamesEnabled').checked = config.GamesEnabled === true;
            loadGameLibraryPicker(view, config.GameLibraryIds || []);
            initGameCores(view, view.__moonfinState);
            view.querySelector('#GamesRommUrl').value = config.GamesRommUrl || '';
            view.querySelector('#GamesRommToken').value = config.GamesRommToken || '';
            loadRommLibraryPicker(view, config.GamesRommLibraryId || '');
            initRommTest(view);

            var defaults = camelKeysDeep(config.DefaultUserSettings) || {};
            setSelectValue(view, '#DefaultInterfaceStyle', defaults.interfaceStyle, 'Configured style');
            setSelectValue(view, '#DefaultVisualTheme', defaults.visualTheme, 'Configured theme');
            setSelectValue(view, '#DefaultDetailScreenStyle', defaults.detailScreenStyle, 'Configured style');
            setNullableBoolSelect(view, '#DefaultDetailExpandedTabs', defaults.detailExpandedTabs);
            setNullableBoolSelect(view, '#DefaultDetailShowTechnicalDetails', defaults.detailShowTechnicalDetails);
            setSelectValue(view, '#DefaultRecommendationSystemSource', defaults.recommendationSystemSource, 'Configured source');
            setNullableBoolSelect(view, '#DefaultRecommendationsApplyParentalRatingCap', defaults.recommendationsApplyParentalRatingCap);
            // One picker stands in for all three form factors.
            loadButtonPicker(view, '#DefaultDetailMetadataList', DETAIL_METADATA,
                defaults.detailMetadataOrderTv || defaults.detailMetadataOrderMobile || defaults.detailMetadataOrderDesktop || null,
                defaults.hiddenDetailMetadataTv || defaults.hiddenDetailMetadataMobile || defaults.hiddenDetailMetadataDesktop || null);
            loadButtonPicker(view, '#DefaultDetailButtonsList', DETAIL_BUTTONS,
                defaults.detailButtonOrderTv || defaults.detailButtonOrderMobile || defaults.detailButtonOrderDesktop || null,
                defaults.hiddenDetailButtonsTv || defaults.hiddenDetailButtonsMobile || defaults.hiddenDetailButtonsDesktop || null);
            loadButtonPicker(view, '#DefaultDetailSectionsList', DETAIL_SECTIONS, null,
                defaults.hiddenDetailSectionsTv || defaults.hiddenDetailSectionsMobile || defaults.hiddenDetailSectionsDesktop || null,
                true);
            setSelectValue(view, '#DefaultFocusColor', defaults.focusColor, 'Configured color');
            setSelectValue(view, '#DefaultClockBehavior', defaults.clockBehavior, 'Configured clock');
            setNullableBoolSelect(view, '#DefaultUse24HourClock', defaults.use24HourClock);
            setSelectValue(view, '#DefaultDesktopUiScale', defaults.desktopUiScale, 'Configured scale');
            setSelectValue(view, '#DefaultHomeBackdropStyle', homeBackdropStyleOf(defaults), 'Configured style');
            setNullableBoolSelect(view, '#DefaultDetailsBackdropEnabled', defaults.detailsBackdropEnabled);
            setSelectValue(view, '#DefaultDetailsBackdropSize', defaults.detailsBackdropSize, 'Configured size');
            setNullableBoolSelect(view, '#DefaultShowBookDiscoverTab', defaults.showBookDiscoverTab);
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
            setSelectValue(view, '#DefaultScreensaverBackdrop', defaults.screensaverBackdrop, 'Configured backdrop');
            setSelectValue(view, '#DefaultScreensaverComponent', defaults.screensaverComponent, 'Configured component');
            setSelectValue(view, '#DefaultScreensaverMovement', defaults.screensaverMovement, 'Configured movement');
            setSelectValue(view, '#DefaultScreensaverPosition', defaults.screensaverPosition, 'Configured position');
            setSelectValue(view, '#DefaultScreensaverSize', defaults.screensaverSize, 'Configured size');
            setSelectValue(view, '#DefaultScreensaverContentType', defaults.screensaverContentType, 'Configured content type');
            loadScreensaverLibraryPicker(view, defaults.screensaverLibraryIds || []);
            loadScreensaverCollectionPicker(view, defaults.screensaverCollectionIds || []);
            loadScreensaverGenrePicker(view, defaults.screensaverExcludedGenres || []);
            setSelectValue(view, '#DefaultLoadingAnimationImage', defaults.loadingAnimationImage, 'Configured image');
            setSelectValue(view, '#DefaultLoadingAnimationPosition', defaults.loadingAnimationPosition, 'Configured position');
            setSelectValue(view, '#DefaultLoadingAnimationSize', defaults.loadingAnimationSize, 'Configured size');
            setSelectValue(view, '#DefaultLoadingAnimationSpeed', defaults.loadingAnimationSpeed, 'Configured speed');
            setNullableBoolSelect(view, '#DefaultShowLoadingAnimationText', defaults.showLoadingAnimationText);

            setSelectValue(view, '#DefaultNavbarPosition', defaults.navbarPosition, 'Configured position');
            setSelectValue(view, '#DefaultNavbarColor', defaults.navbarColor, 'Configured color');
            bindNullableRangeInput(view, '#DefaultNavbarOpacity', '%');
            setNullableRangeInput(view, '#DefaultNavbarOpacity', defaults.navbarOpacity, '%');
            setNullableBoolSelect(view, '#DefaultNavbarAlwaysExpanded', defaults.navbarAlwaysExpanded);
            setSelectValue(view, '#DefaultBottomNavbarStyle', defaults.bottomNavbarStyle, 'Configured style');
            setBottomNavbarTabSelects(view, defaults.bottomNavbarTabs);
            setNullableBoolSelect(view, '#DefaultEnableFolderView', defaults.enableFolderView);
            setNullableBoolSelect(view, '#DefaultShowSeerrButton', defaults.showSeerrButton);
            setNullableBoolSelect(view, '#DefaultShowLiveTvButton', defaults.showLiveTvButton);
            setNullableBoolSelect(view, '#DefaultShowDownloadsButton', defaults.showDownloadsButton);
            setNullableBoolSelect(view, '#DefaultShowServerMessagesButton', defaults.showServerMessagesButton);

            setSelectValue(view, '#DefaultMediaBarSourceType', defaults.mediaBarSourceType, 'Configured source');
            loadAdminGenrePicker(view, defaults.mediaBarExcludedGenres || []);
            setSelectValue(view, '#DefaultMediaBarMode', defaults.mediaBarMode, 'Configured mode');
            setSelectValue(view, '#DefaultMediaBarContentType', defaults.mediaBarContentType, 'Configured content type');
            setSelectValue(view, '#DefaultMediaBarItemCount', defaults.mediaBarItemCount, 'Configured item count');
            setNullableBoolSelect(view, '#DefaultMediaBarAutoAdvance', defaults.mediaBarAutoAdvance);
            setSelectValue(view, '#DefaultMediaBarIntervalMs', defaults.mediaBarIntervalMs, 'Configured interval');
            setNullableBoolSelect(view, '#DefaultMediaBarRowPeek', defaults.mediaBarRowPeek);
            setNullableBoolSelect(view, '#DefaultMediaBarTrailerPreview', defaults.mediaBarTrailerPreview);
            setNullableBoolSelect(view, '#DefaultMediaBarTrailerAudio', defaults.mediaBarTrailerAudio);
            bindNullableRangeInput(view, '#DefaultTrailerVolume', '%');
            setNullableRangeInput(view, '#DefaultTrailerVolume', defaults.trailerVolume, '%');
            setNullableBoolSelect(view, '#DefaultMediaBarTrailerCaptions', defaults.mediaBarTrailerCaptions);
            setSelectValue(view, '#DefaultMediaPreviewMode', mediaPreviewModeOf(defaults), 'Configured preview');
            setNullableBoolSelect(view, '#DefaultPreviewAudioEnabled', defaults.previewAudioEnabled);
            bindNullableRangeInput(view, '#DefaultPreviewVolume', '%');
            setNullableRangeInput(view, '#DefaultPreviewVolume', defaults.previewVolume, '%');
            setSelectValue(view, '#DefaultSeasonalSurprise', defaults.seasonalSurprise, 'Configured surprise');
            setSelectValue(view, '#DefaultSeasonalDensity', defaults.seasonalDensity, 'Configured density');
            setNullableBoolSelect(view, '#DefaultSeasonalFollower', defaults.seasonalFollower);
            setNullableBoolSelect(view, '#DefaultSeasonalRowEnabled', defaults.seasonalRowEnabled);
            setSelectValue(view, '#DefaultSeasonalRowCountry', defaults.seasonalRowCountry, 'Configured country');
            renderSeasonalHolidayChecks(view, defaults.seasonalRowHiddenHolidays);

            setSelectValue(view, '#DefaultResumeSubtractDuration', defaults.resumeSubtractDuration != null ? String(defaults.resumeSubtractDuration) : '', 'Configured rewind');
            setSelectValue(view, '#DefaultUnpauseRewindDuration', defaults.unpauseRewindDuration != null ? String(defaults.unpauseRewindDuration) : '', 'Configured rewind');
            setSelectValue(view, '#DefaultSkipBackLength', defaults.skipBackLength != null ? String(defaults.skipBackLength) : '', 'Configured skip');
            setSelectValue(view, '#DefaultSkipForwardLength', defaults.skipForwardLength != null ? String(defaults.skipForwardLength) : '', 'Configured skip');

            fillLanguageSelect(view, '#DefaultDefaultAudioLanguage');
            fillLanguageSelect(view, '#DefaultDefaultSubtitleLanguage');
            fillLanguageSelect(view, '#DefaultFallbackAudioLanguage', true);
            fillLanguageSelect(view, '#DefaultFallbackSubtitleLanguage', true);
            setSelectValue(view, '#DefaultDefaultAudioLanguage', defaults.defaultAudioLanguage, 'Configured language');
            setSelectValue(view, '#DefaultFallbackAudioLanguage', defaults.fallbackAudioLanguage, 'Configured language');
            setNullableBoolSelect(view, '#DefaultPreferDefaultAudioTrack', defaults.preferDefaultAudioTrack);
            setNullableBoolSelect(view, '#DefaultPreferAudioDescription', defaults.preferAudioDescription);

            setSelectValue(view, '#DefaultSubtitleMode', defaults.subtitleMode, 'Configured mode');
            setSelectValue(view, '#DefaultDefaultSubtitleLanguage', defaults.defaultSubtitleLanguage, 'Configured language');
            setSelectValue(view, '#DefaultFallbackSubtitleLanguage', defaults.fallbackSubtitleLanguage, 'Configured language');
            setNullableBoolSelect(view, '#DefaultPreferSdhSubtitles', defaults.preferSdhSubtitles);
            setNullableBoolSelect(view, '#DefaultPreferTextSubtitles', defaults.preferTextSubtitles);
            setNullableBoolSelect(view, '#DefaultPreferExternalSubtitles', defaults.preferExternalSubtitles);

            setNullableBoolSelect(view, '#DefaultCinemaModeEnabled', defaults.cinemaModeEnabled);
            setNullableBoolSelect(view, '#DefaultCinemaModeSkipEnabled', defaults.cinemaModeSkipEnabled);
            setNullableBoolSelect(view, '#DefaultAutoplayNextEpisode', defaults.autoplayNextEpisode);
            setSelectValue(view, '#DefaultNextUpTimeout', defaults.nextUpTimeout != null ? String(defaults.nextUpTimeout) : '', 'Configured timeout');
            setSelectValue(view, '#DefaultStillWatchingBehavior', defaults.stillWatchingBehavior, 'Configured behavior');
            loadSegmentActions(view, defaults.mediaSegmentActions);
            setSelectValue(view, '#DefaultMediaSegmentCountdown', defaults.mediaSegmentCountdown, 'Configured countdown');
            setSelectValue(view, '#DefaultMediaSegmentAutoHide', defaults.mediaSegmentAutoHide, 'Configured timeout');
            setNullableBoolSelect(view, '#DefaultReplaceSkipOutroWithNextUp', defaults.replaceSkipOutroWithNextUp);
            setNullableBoolSelect(view, '#DefaultCinemaModeEpisodesEnabled', defaults.cinemaModeEpisodesEnabled);

            setSelectValue(view, '#DefaultHomeRowsStyle', defaults.homeRowsStyle, 'Configured style');
            setNullableBoolSelect(view, '#DefaultModernCardsOnMyMediaRow', defaults.modernCardsOnMyMediaRow);
            setNullableBoolSelect(view, '#DefaultFullScreenRows', defaults.fullScreenRows);
            setNullableBoolSelect(view, '#DefaultHomeRowInfoOverlay', defaults.homeRowInfoOverlay);
            bindNullableRangeInput(view, '#DefaultClassicHomeRowsPadding', 'px');
            setNullableRangeInput(view, '#DefaultClassicHomeRowsPadding', defaults.classicHomeRowsPadding, 'px');
            bindNullableRangeInput(view, '#DefaultModernHomeRowsPadding', 'px');
            setNullableRangeInput(view, '#DefaultModernHomeRowsPadding', defaults.modernHomeRowsPadding, 'px');
            setSelectValue(view, '#DefaultHomeImageTypeContinueWatching', defaults.homeImageTypeContinueWatching, 'Configured image type');
            loadHomeRowImageTypes(view, defaults.homeRowImageTypes);
            setSelectValue(view, '#DefaultPosterSize', defaults.posterSize, 'Configured size');
            setNullableBoolSelect(view, '#DefaultDisplayFavoritesRows', defaults.displayFavoritesRows);
            fillSortSelect(view, '#DefaultFavoritesRowSortBy', false);
            setSelectValue(view, '#DefaultFavoritesRowSortBy', defaults.favoritesRowSortBy, 'Configured sort');
            setNullableBoolSelect(view, '#DefaultDisplayCollectionsRows', defaults.displayCollectionsRows);
            fillSortSelect(view, '#DefaultCollectionsRowSortBy', true);
            setSelectValue(view, '#DefaultCollectionsRowSortBy', defaults.collectionsRowSortBy, 'Configured sort');
            setNullableBoolSelect(view, '#DefaultDisplayGenresRows', defaults.displayGenresRows);
            fillSortSelect(view, '#DefaultGenresRowSortBy', false);
            setSelectValue(view, '#DefaultGenresRowSortBy', defaults.genresRowSortBy, 'Configured sort');
            setNullableBoolSelect(view, '#DefaultDisplayPlaylistsRows', defaults.displayPlaylistsRows);
            fillSortSelect(view, '#DefaultPlaylistsRowSortBy', true);
            setSelectValue(view, '#DefaultPlaylistsRowSortBy', defaults.playlistsRowSortBy, 'Configured sort');
            setNullableBoolSelect(view, '#DefaultDisplayAudioRows', defaults.displayAudioRows);
            setSelectValue(view, '#DefaultAudioRowsSortBy', defaults.audioRowsSortBy, 'Configured sort');
            setNullableBoolSelect(view, '#DefaultHomeImageUseSeriesImage', defaults.homeImageUseSeriesImage);
            setNullableBoolSelect(view, '#DefaultDisplayRewatchRow', defaults.displayRewatchRow);
            setSelectValue(view, '#DefaultRewatchSortBy', defaults.rewatchSortBy, 'Configured sort');
            setNullableBoolSelect(view, '#DefaultDisplaySinceYouWatchedRows', defaults.displaySinceYouWatchedRows);
            loadHomeSectionsEditor(view, defaults.homeSections || null, defaults.homeRowOrder || null);
            setNullableBoolSelect(view, '#DefaultMergeContinueWatchingNextUp', defaults.mergeContinueWatchingNextUp);
            setSelectValue(view, '#DefaultContinueWatchingDefaultAction', defaults.continueWatchingDefaultAction, 'Configured default action');
            setSelectValue(view, '#DefaultNextUpMaxDays', defaults.nextUpMaxDays, 'Configured max days');

            setNullableBoolSelect(view, '#DefaultEnableMultiServerLibraries', defaults.enableMultiServerLibraries);
            setNullableBoolSelect(view, '#DefaultMergeRecentRowsByType', defaults.mergeRecentRowsByType);
            setSelectValue(view, '#DefaultRecentlyReleasedSeriesType', defaults.recentlyReleasedSeriesType, 'Configured sort');
            setNullableBoolSelect(view, '#DefaultGroupItemsIntoCollections', defaults.groupItemsIntoCollections);
            setNullableBoolSelect(view, '#DefaultShowMediaDetailsOnLibraryPage', defaults.showMediaDetailsOnLibraryPage);
            setNullableBoolSelect(view, '#DefaultLibraryCardTitles', defaults.libraryCardTitles);
            setNullableBoolSelect(view, '#DefaultUseDetailedSubHeadings', defaults.useDetailedSubHeadings);
            setNullableBoolSelect(view, '#DefaultHideBackdropsInLibraries', defaults.hideBackdropsInLibraries);

            setNullableBoolSelect(view, '#DefaultShowShuffleButton', defaults.showShuffleButton);
            setNullableBoolSelect(view, '#DefaultShowGenresButton', defaults.showGenresButton);
            setNullableBoolSelect(view, '#DefaultShowFavoritesButton', defaults.showFavoritesButton);
            setNullableBoolSelect(view, '#DefaultShowLibrariesInToolbar', defaults.showLibrariesInToolbar);
            setNullableBoolSelect(view, '#DefaultShowLibrariesIndividually', defaults.showLibrariesIndividually);
            setNullableBoolSelect(view, '#DefaultShowSyncPlayButton', defaults.showSyncPlayButton);
            setNullableBoolSelect(view, '#DefaultSyncPlayEnabled', defaults.syncPlayEnabled);

            setNullableBoolSelect(view, '#DefaultMdblistEnabled', defaults.mdblistEnabled);
            setNullableBoolSelect(view, '#DefaultTmdbEpisodeRatingsEnabled', defaults.tmdbEpisodeRatingsEnabled);
            setNullableBoolSelect(view, '#DefaultMdblistShowRatingBadges', defaults.mdblistShowRatingBadges);
            setNullableBoolSelect(view, '#DefaultMdblistShowRatingNames', defaults.mdblistShowRatingNames);
            loadRatingSourcesPicker(view, defaults.mdblistRatingSources || null);
            setNullableBoolSelect(view, '#DefaultHomeRowsMdblistEnabled', defaults.homeRowsMdblistEnabled);
            loadRatingSourcesPicker(view, defaults.homeRowsMdblistRatingSources || null, '#DefaultHomeRowsMdblistRatingSourcesList');
            setNullableBoolSelect(view, '#DefaultHomeRowsTmdbEpisodeRatingsEnabled', defaults.homeRowsTmdbEpisodeRatingsEnabled);
            setNullableBoolSelect(view, '#DefaultHomeRowsMdblistShowRatingNames', defaults.homeRowsMdblistShowRatingNames);
            setNullableBoolSelect(view, '#DefaultHomeRowsMdblistShowRatingBadges', defaults.homeRowsMdblistShowRatingBadges);
            setNullableBoolSelect(view, '#DefaultMediaBarMdblistEnabled', defaults.mediaBarMdblistEnabled);
            loadRatingSourcesPicker(view, defaults.mediaBarMdblistRatingSources || null, '#DefaultMediaBarMdblistRatingSourcesList');
            setNullableBoolSelect(view, '#DefaultMediaBarMdblistShowRatingNames', defaults.mediaBarMdblistShowRatingNames);
            setNullableBoolSelect(view, '#DefaultMediaBarMdblistShowRatingBadges', defaults.mediaBarMdblistShowRatingBadges);
            setNullableBoolSelect(view, '#DefaultSeerrBlockNsfw', defaults.seerrBlockNsfw);
            setNullableBoolSelect(view, '#DefaultSeerrShowMissingCollectionItems', defaults.seerrShowMissingCollectionItems);
            setNullableBoolSelect(view, '#DefaultShowSeerrAvailabilityBadges', defaults.showSeerrAvailabilityBadges);
            fillExternalRowSortSelect(view, '#DefaultSeerrRowsSortBy');
            setSelectValue(view, '#DefaultSeerrRowsSortBy', defaults.seerrRowsSortBy, 'Configured sort');
            setSelectValue(view, '#DefaultSeerrRowsSortOrder', defaults.seerrRowsSortOrder, 'Configured direction');
            var seerrPicker = seerrRowsToPicker(defaults.seerrRows);
            loadSeerrDiscoveryPicker(view, seerrPicker.order, seerrPicker.hidden);

            setNullableBoolSelect(view, '#DefaultConfirmExit', defaults.confirmExit);
            setSelectValue(view, '#DefaultKeyboardStyle', defaults.keyboardStyle, 'Configured style');
            setNullableBoolSelect(view, '#DefaultUpdateNotificationsEnabled', defaults.updateNotificationsEnabled);
            setNullableBoolSelect(view, '#DefaultHideDetailsMediaDescription', defaults.hideDetailsMediaDescription);
            setNullableBoolSelect(view, '#DefaultDetailTrailersExternal', defaults.detailTrailersExternal);
            setNullableBoolSelect(view, '#DefaultDetailUseSeriesThumbnails', defaults.detailUseSeriesThumbnails);
            setNullableBoolSelect(view, '#DefaultPersonPageGroupItems', defaults.personPageGroupItems);
            setNullableBoolSelect(view, '#DefaultHomeRowsImageTypeOverride', defaults.homeRowsImageTypeOverride);
            setNullableBoolSelect(view, '#DefaultHideHomeMediaDescription', defaults.hideHomeMediaDescription);
            setNullableBoolSelect(view, '#DefaultHideHomeWatchProgress', defaults.hideHomeWatchProgress);
            setNullableBoolSelect(view, '#DefaultHideHomeUnwatchedCount', defaults.hideHomeUnwatchedCount);
            setNullableBoolSelect(view, '#DefaultCollectionsRowShowEpisodes', defaults.collectionsRowShowEpisodes);
            setNullableBoolSelect(view, '#DefaultPlaylistsRowShowEpisodes', defaults.playlistsRowShowEpisodes);
            setNullableBoolSelect(view, '#DefaultDisplayStudiosRows', defaults.displayStudiosRows);
            setNullableBoolSelect(view, '#DefaultRewatchIncludeMovies', defaults.rewatchIncludeMovies);
            setNullableBoolSelect(view, '#DefaultRewatchIncludeShows', defaults.rewatchIncludeShows);
            setNullableBoolSelect(view, '#DefaultRewatchIncludeCollections', defaults.rewatchIncludeCollections);
            setNullableBoolSelect(view, '#DefaultSinceYouWatchedIncludeWatched', defaults.sinceYouWatchedIncludeWatched);
            setNullableBoolSelect(view, '#DefaultSinceYouWatched1Enabled', defaults.sinceYouWatched1Enabled);
            setNullableBoolSelect(view, '#DefaultSinceYouWatched2Enabled', defaults.sinceYouWatched2Enabled);
            setNullableBoolSelect(view, '#DefaultSinceYouWatched3Enabled', defaults.sinceYouWatched3Enabled);
            setNullableBoolSelect(view, '#DefaultSinceYouWatched4Enabled', defaults.sinceYouWatched4Enabled);
            setNullableBoolSelect(view, '#DefaultSinceYouWatched5Enabled', defaults.sinceYouWatched5Enabled);
            setNullableBoolSelect(view, '#DefaultDisplayAudioLatest', defaults.displayAudioLatest);
            setNullableBoolSelect(view, '#DefaultDisplayAudioLastPlayed', defaults.displayAudioLastPlayed);
            setNullableBoolSelect(view, '#DefaultDisplayAudioFavorites', defaults.displayAudioFavorites);
            setNullableBoolSelect(view, '#DefaultDisplayAudioPlaylists', defaults.displayAudioPlaylists);
            setNullableBoolSelect(view, '#DefaultDisplayAudioAlbumArtists', defaults.displayAudioAlbumArtists);
            setNullableBoolSelect(view, '#DefaultDisplayAudioArtists', defaults.displayAudioArtists);
            setNullableBoolSelect(view, '#DefaultDisplayAudioAlbums', defaults.displayAudioAlbums);
            fillExternalRowSortSelect(view, '#DefaultImdbRowsSortBy');
            setSelectValue(view, '#DefaultImdbRowsSortBy', defaults.imdbRowsSortBy, 'Configured sort');
            setSelectValue(view, '#DefaultImdbRowsSortOrder', defaults.imdbRowsSortOrder, 'Configured direction');
            setNullableBoolSelect(view, '#DefaultImdbTop250MoviesEnabled', defaults.imdbTop250MoviesEnabled);
            setNullableBoolSelect(view, '#DefaultImdbTop250TvShowsEnabled', defaults.imdbTop250TvShowsEnabled);
            setNullableBoolSelect(view, '#DefaultImdbMostPopularMoviesEnabled', defaults.imdbMostPopularMoviesEnabled);
            setNullableBoolSelect(view, '#DefaultImdbMostPopularTvShowsEnabled', defaults.imdbMostPopularTvShowsEnabled);
            setNullableBoolSelect(view, '#DefaultImdbTopEnglishMoviesEnabled', defaults.imdbTopEnglishMoviesEnabled);
            setNullableBoolSelect(view, '#DefaultImdbLowestRatedMoviesEnabled', defaults.imdbLowestRatedMoviesEnabled);
            fillExternalRowSortSelect(view, '#DefaultTmdbRowsSortBy');
            setSelectValue(view, '#DefaultTmdbRowsSortBy', defaults.tmdbRowsSortBy, 'Configured sort');
            setSelectValue(view, '#DefaultTmdbRowsSortOrder', defaults.tmdbRowsSortOrder, 'Configured direction');
            setNullableBoolSelect(view, '#DefaultTmdbTrendingAllWeeklyEnabled', defaults.tmdbTrendingAllWeeklyEnabled);
            setNullableBoolSelect(view, '#DefaultTmdbTrendingMovieDailyEnabled', defaults.tmdbTrendingMovieDailyEnabled);
            setNullableBoolSelect(view, '#DefaultTmdbTrendingMovieWeeklyEnabled', defaults.tmdbTrendingMovieWeeklyEnabled);
            setNullableBoolSelect(view, '#DefaultTmdbTrendingTvDailyEnabled', defaults.tmdbTrendingTvDailyEnabled);
            setNullableBoolSelect(view, '#DefaultTmdbTrendingTvWeeklyEnabled', defaults.tmdbTrendingTvWeeklyEnabled);
            setNullableBoolSelect(view, '#DefaultTmdbPopularMoviesEnabled', defaults.tmdbPopularMoviesEnabled);
            setNullableBoolSelect(view, '#DefaultTmdbPopularTvEnabled', defaults.tmdbPopularTvEnabled);
            setNullableBoolSelect(view, '#DefaultTmdbTopRatedMoviesEnabled', defaults.tmdbTopRatedMoviesEnabled);
            setNullableBoolSelect(view, '#DefaultTmdbTopRatedTvEnabled', defaults.tmdbTopRatedTvEnabled);
            setNullableBoolSelect(view, '#DefaultTmdbNowPlayingMoviesEnabled', defaults.tmdbNowPlayingMoviesEnabled);
            setNullableBoolSelect(view, '#DefaultTmdbUpcomingMoviesEnabled', defaults.tmdbUpcomingMoviesEnabled);
            setNullableBoolSelect(view, '#DefaultTmdbAiringTodayTvEnabled', defaults.tmdbAiringTodayTvEnabled);
            setNullableBoolSelect(view, '#DefaultTmdbOnTheAirTvEnabled', defaults.tmdbOnTheAirTvEnabled);
            setNullableBoolSelect(view, '#DefaultEnableRadarrCalendar', defaults.enableRadarrCalendar);
            setNullableBoolSelect(view, '#DefaultEnableSonarrCalendar', defaults.enableSonarrCalendar);
            setNullableBoolSelect(view, '#DefaultMergeRadarrSonarrCalendars', defaults.mergeRadarrSonarrCalendars);
            setNullableBoolSelect(view, '#DefaultRadarrCalendarShowCinema', defaults.radarrCalendarShowCinema);
            setNullableBoolSelect(view, '#DefaultRadarrCalendarShowDigital', defaults.radarrCalendarShowDigital);
            setNullableBoolSelect(view, '#DefaultRadarrCalendarShowPhysical', defaults.radarrCalendarShowPhysical);
            setNullableBoolSelect(view, '#DefaultRadarrCalendarShowDate', defaults.radarrCalendarShowDate);
            setNullableBoolSelect(view, '#DefaultSonarrCalendarShowDate', defaults.sonarrCalendarShowDate);
            setNullableBoolSelect(view, '#DefaultSonarrCalendarShowEpisodeInfo', defaults.sonarrCalendarShowEpisodeInfo);
            setSelectValue(view, '#DefaultPersonalRatingStyle', defaults.personalRatingStyle, 'Configured style');
            setSelectValue(view, '#DefaultPersonPageSortOption', defaults.personPageSortOption, 'Configured sort');
            setSelectValue(view, '#DefaultShuffleContentType', defaults.shuffleContentType, 'Configured content');
            setSelectValue(view, '#DefaultHomeRowsImageType', defaults.homeRowsImageType, 'Configured image type');
            setSelectValue(view, '#DefaultMediaTypeBadgeBehavior', defaults.mediaTypeBadgeBehavior, 'Configured behavior');
            setSelectValue(view, '#DefaultAudioRowsSortOrder', defaults.audioRowsSortOrder, 'Configured direction');
            setSelectValue(view, '#DefaultCollectionsRowSortOrder', defaults.collectionsRowSortOrder, 'Configured direction');
            setSelectValue(view, '#DefaultFavoritesRowSortOrder', defaults.favoritesRowSortOrder, 'Configured direction');
            setSelectValue(view, '#DefaultGenresRowSortOrder', defaults.genresRowSortOrder, 'Configured direction');
            setSelectValue(view, '#DefaultGenresRowItemFilter', defaults.genresRowItemFilter, 'Configured content');
            setSelectValue(view, '#DefaultPlaylistsRowSortOrder', defaults.playlistsRowSortOrder, 'Configured direction');
            fillSortSelect(view, '#DefaultStudiosRowSortBy', false);
            setSelectValue(view, '#DefaultStudiosRowSortBy', defaults.studiosRowSortBy, 'Configured sort');
            setSelectValue(view, '#DefaultStudiosRowSortOrder', defaults.studiosRowSortOrder, 'Configured direction');
            setSelectValue(view, '#DefaultSinceYouWatchedSource', defaults.sinceYouWatchedSource, 'Configured source');
            setSelectValue(view, '#DefaultSinceYouWatchedSourceType', defaults.sinceYouWatchedSourceType, 'Configured content');
            setSelectValue(view, '#DefaultSinceYouWatchedSourceItem', defaults.sinceYouWatchedSourceItem, 'Configured source');
            setSelectValue(view, '#DefaultLibraryStyle', defaults.libraryStyle, 'Configured style');
            setSelectValue(view, '#DefaultBookLibraryStyle', defaults.bookLibraryStyle, 'Configured style');
            setSelectValue(view, '#DefaultMusicLibraryStyle', defaults.musicLibraryStyle, 'Configured style');
            setSelectValue(view, '#DefaultLibraryPosterSize', defaults.libraryPosterSize, 'Configured size');
            setSelectValue(view, '#DefaultPlaylistPosterSize', defaults.playlistPosterSize, 'Configured size');
            setSelectValue(view, '#DefaultFavoritesViewStyle', defaults.favoritesViewStyle, 'Configured view');
            setSelectValue(view, '#DefaultAllGenresImageType', defaults.allGenresImageType, 'Configured image type');
            setSelectValue(view, '#DefaultAudioSortOption', defaults.audioSortOption, 'Configured sort');
            setSelectValue(view, '#DefaultLiveTvChannelSortBy', defaults.liveTvChannelSortBy, 'Configured sort');
            setSelectValue(view, '#DefaultEpgMobileView', defaults.epgMobileView, 'Configured view');
            setSelectValue(view, '#DefaultMediaBarOverlayColor', defaults.mediaBarOverlayColor, 'Configured color');
            bindNullableRangeInput(view, '#DefaultMediaBarOpacity', '%');
            setNullableRangeInput(view, '#DefaultMediaBarOpacity', defaults.mediaBarOpacity, '%');
            loadButtonPicker(view, '#DefaultOsdButtonsList', OSD_BUTTONS,
                defaults.osdButtonOrderTv || defaults.osdButtonOrderMobile || defaults.osdButtonOrderDesktop || null,
                defaults.hiddenOsdButtonsTv || defaults.hiddenOsdButtonsMobile || defaults.hiddenOsdButtonsDesktop || null);

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
            applyFieldDependencies(view);

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
            config.PublicServerUrl = view.querySelector('#PublicServerUrl').value || null;
            config.MdblistApiKey = (view.querySelector('#MdblistApiKey').value || '').trim() || null;
            config.MdblistOfficialListsEnabled = view.querySelector('#MdblistOfficialListsEnabled').checked;
            config.MdblistOfficialListsMaxItems = parseInt(view.querySelector('#MdblistOfficialListsMaxItems').value, 10) || 250;
            config.TmdbApiKey = (view.querySelector('#TmdbApiKey').value || '').trim() || null;
            config.ImdbListsEnabled = view.querySelector('#ImdbListsEnabled').checked;
            config.StudioLogosEnabled = view.querySelector('#StudioLogosEnabled').checked;
            config.StudioLogosMaxAgeDays = parseInt(view.querySelector('#StudioLogosMaxAgeDays').value, 10) || 30;
            config.AnimeMarkersEnabled = view.querySelector('#AnimeMarkersEnabled').checked;
            config.AnimeMarkerRecapLookup = view.querySelector('#AnimeMarkerRecapLookup').checked;
            config.AnimeAudioMarkersEnabled = view.querySelector('#AnimeAudioMarkersEnabled').checked;
            config.AnimeAudioMarkersMovies = view.querySelector('#AnimeAudioMarkersMovies').checked;
            config.AnimeAudioSeparateDualAudio = view.querySelector('#AnimeAudioSeparateDualAudio').checked;
            config.AnimeAudioTrustSelectedLibraries = view.querySelector('#AnimeAudioTrustSelectedLibraries').checked;
            config.AnimeMarkerPlacement = view.querySelector('#AnimeMarkerPlacement').value || 'below';
            config.AnimeAudioLabelStyle = view.querySelector('#AnimeAudioLabelStyle').value || 'full';
            config.AnimeMarkerShowFiller = view.querySelector('#AnimeMarkerShowFiller').checked;
            config.AnimeMarkerShowMixed = view.querySelector('#AnimeMarkerShowMixed').checked;
            config.AnimeMarkerShowRecap = view.querySelector('#AnimeMarkerShowRecap').checked;
            config.AnimeMarkerShowMangaCanon = view.querySelector('#AnimeMarkerShowMangaCanon').checked;
            config.AnimeMarkerShowAnimeCanon = view.querySelector('#AnimeMarkerShowAnimeCanon').checked;
            config.AnimeMarkerMaxAgeDays = parseInt(view.querySelector('#AnimeMarkerMaxAgeDays').value, 10) || 30;
            config.AnimeMarkerVerboseLogging = view.querySelector('#AnimeMarkerVerboseLogging').checked;
            // Only read once the picker has drawn, so a save during its load keeps the stored list.
            if (view.querySelector('#AnimeMarkerLibraryPicker .animeMarkerLibraryCb')) {
                config.AnimeMarkerLibraryIds = Array.prototype.slice.call(view.querySelectorAll('.animeMarkerLibraryCb:checked'))
                    .map(function (cb) { return cb.getAttribute('data-id'); });
            }
            config.FcmServiceAccountJson = view.querySelector('#FcmServiceAccountJson').value || null;
            config.FcmServiceAccountPath = view.querySelector('#FcmServiceAccountPath').value || null;
            config.PushRelayUrl = (view.querySelector('#PushRelayUrl').value || '').trim()
                || 'https://push.moonfin.io/send';
            config.PushRelayAppKey = (view.querySelector('#PushRelayAppKey').value || '').trim() || null;
            config.WebDefaultServerUrl = view.querySelector('#WebDefaultServerUrl').value || null;
            config.WebForcedServerUrl = view.querySelector('#WebForcedServerUrl').value || null;
            config.WebEnableWebRtcScan = view.querySelector('#WebEnableWebRtcScan').checked;
            config.EnableClientLogUpload = view.querySelector('#EnableClientLogUpload').checked;
            config.LiveTvDetectIncompleteRecordings = view.querySelector('#LiveTvDetectIncompleteRecordings').checked;
            config.LiveTvAutoRecordCompleteShowing = view.querySelector('#LiveTvAutoRecordCompleteShowing').checked;
            config.LiveTvIncompleteThresholdPercent = Math.min(100, Math.max(50, parseInt(view.querySelector('#LiveTvIncompleteThresholdPercent').value, 10) || 90));
            config.LiveTvDeleteIncompleteCopies = view.querySelector('#LiveTvDeleteIncompleteCopies').checked;

            if (view.querySelector('.defaultHiddenLibraryCb') != null) {
                config.DefaultHiddenLibraryIds = Array.prototype.slice.call(view.querySelectorAll('.defaultHiddenLibraryCb:checked'))
                    .map(function (cb) { return cb.getAttribute('data-id'); });
            }

            config.GamesEnabled = view.querySelector('#GamesEnabled').checked;
            config.GameLibraryIds = Array.prototype.slice.call(view.querySelectorAll('.gameLibraryCb:checked'))
                .map(function (cb) { return cb.getAttribute('data-id'); });
            config.GamesRommUrl = view.querySelector('#GamesRommUrl').value.trim() || null;
            config.GamesRommToken = view.querySelector('#GamesRommToken').value.trim() || null;
            config.GamesRommLibraryId = view.querySelector('#GamesRommLibraryId').value || null;

            var d = camelKeysDeep(config.DefaultUserSettings) || {};
            d.interfaceStyle = view.querySelector('#DefaultInterfaceStyle').value || null;
            d.visualTheme = view.querySelector('#DefaultVisualTheme').value || null;
            d.detailScreenStyle = view.querySelector('#DefaultDetailScreenStyle').value || null;
            d.detailExpandedTabs = getNullableBoolSelect(view, '#DefaultDetailExpandedTabs');
            d.detailShowTechnicalDetails = getNullableBoolSelect(view, '#DefaultDetailShowTechnicalDetails');
            d.recommendationSystemSource = view.querySelector('#DefaultRecommendationSystemSource').value || null;
            d.recommendationsApplyParentalRatingCap = getNullableBoolSelect(view, '#DefaultRecommendationsApplyParentalRatingCap');
            var metaVal = getButtonPickerValue(view, '#DefaultDetailMetadataList');
            d.detailMetadataOrderTv = metaVal.order;
            d.detailMetadataOrderMobile = metaVal.order;
            d.detailMetadataOrderDesktop = metaVal.order;
            d.hiddenDetailMetadataTv = metaVal.hidden;
            d.hiddenDetailMetadataMobile = metaVal.hidden;
            d.hiddenDetailMetadataDesktop = metaVal.hidden;
            var btnVal = getButtonPickerValue(view, '#DefaultDetailButtonsList');
            // Clients read a different key per form factor, so one arrangement covers all three.
            d.detailButtonOrderTv = btnVal.order;
            d.detailButtonOrderMobile = btnVal.order;
            d.detailButtonOrderDesktop = btnVal.order;
            d.hiddenDetailButtonsTv = btnVal.hidden;
            d.hiddenDetailButtonsMobile = btnVal.hidden;
            d.hiddenDetailButtonsDesktop = btnVal.hidden;
            var sectionVal = getButtonPickerValue(view, '#DefaultDetailSectionsList');
            d.hiddenDetailSectionsTv = sectionVal.hidden;
            d.hiddenDetailSectionsMobile = sectionVal.hidden;
            d.hiddenDetailSectionsDesktop = sectionVal.hidden;
            d.focusColor = view.querySelector('#DefaultFocusColor').value || null;
            d.clockBehavior = view.querySelector('#DefaultClockBehavior').value || null;
            d.use24HourClock = getNullableBoolSelect(view, '#DefaultUse24HourClock');
            d.desktopUiScale = view.querySelector('#DefaultDesktopUiScale').value || null;
            var homeBackdropStyle = view.querySelector('#DefaultHomeBackdropStyle').value;
            // Phones and older apps only read the on and off switch, so it's written alongside the style.
            d.homeBackdropEnabled = homeBackdropStyle ? homeBackdropStyle !== 'off' : null;
            d.homeBackdropStyle = homeBackdropStyle || null;
            d.detailsBackdropEnabled = getNullableBoolSelect(view, '#DefaultDetailsBackdropEnabled');
            d.detailsBackdropSize = view.querySelector('#DefaultDetailsBackdropSize').value || null;
            d.showBookDiscoverTab = getNullableBoolSelect(view, '#DefaultShowBookDiscoverTab');
            d.browsingBlur = getNullableRangeInputAsText(view, '#DefaultBrowsingBlur');
            d.detailsScreenBlur = getNullableRangeInputAsText(view, '#DefaultDetailsScreenBlur');
            d.themeMusicEnabled = getNullableBoolSelect(view, '#DefaultThemeMusicEnabled');
            d.themeMusicOnHomeRows = getNullableBoolSelect(view, '#DefaultThemeMusicOnHomeRows');
            d.themeMusicLoop = getNullableBoolSelect(view, '#DefaultThemeMusicLoop');
            d.themeMusicVolume = getNullableRangeInput(view, '#DefaultThemeMusicVolume');
            d.watchedIndicator = view.querySelector('#DefaultWatchedIndicator').value || null;
            d.cardFocusExpansion = getNullableBoolSelect(view, '#DefaultCardFocusExpansion');
            d.screensaverMode = view.querySelector('#DefaultScreensaverMode').value || null;
            d.screensaverBackdrop = view.querySelector('#DefaultScreensaverBackdrop').value || null;
            d.screensaverComponent = view.querySelector('#DefaultScreensaverComponent').value || null;
            d.screensaverMovement = view.querySelector('#DefaultScreensaverMovement').value || null;
            d.screensaverPosition = view.querySelector('#DefaultScreensaverPosition').value || null;
            d.screensaverSize = view.querySelector('#DefaultScreensaverSize').value || null;
            d.screensaverContentType = view.querySelector('#DefaultScreensaverContentType').value || null;
            if (view.querySelector('#DefaultScreensaverLibraryPicker .screensaverLibraryCb')) {
                var screensaverLibraryIds = Array.prototype.slice.call(view.querySelectorAll('#DefaultScreensaverLibraryPicker .screensaverLibraryCb:checked')).map(function (cb) { return cb.dataset.value; });
                d.screensaverLibraryIds = screensaverLibraryIds.length > 0 ? screensaverLibraryIds : null;
            }
            if (view.querySelector('#DefaultScreensaverCollectionPicker .screensaverCollectionCb')) {
                var screensaverCollectionIds = Array.prototype.slice.call(view.querySelectorAll('#DefaultScreensaverCollectionPicker .screensaverCollectionCb:checked')).map(function (cb) { return cb.dataset.value; });
                d.screensaverCollectionIds = screensaverCollectionIds.length > 0 ? screensaverCollectionIds : null;
            }
            if (view.querySelector('#DefaultScreensaverGenrePicker .screensaverGenreCb')) {
                var screensaverGenreNames = Array.prototype.slice.call(view.querySelectorAll('#DefaultScreensaverGenrePicker .screensaverGenreCb:checked')).map(function (cb) { return cb.dataset.value; });
                d.screensaverExcludedGenres = screensaverGenreNames.length > 0 ? screensaverGenreNames : null;
            }
            d.loadingAnimationImage = view.querySelector('#DefaultLoadingAnimationImage').value || null;
            d.loadingAnimationPosition = view.querySelector('#DefaultLoadingAnimationPosition').value || null;
            d.loadingAnimationSize = view.querySelector('#DefaultLoadingAnimationSize').value || null;
            d.loadingAnimationSpeed = view.querySelector('#DefaultLoadingAnimationSpeed').value || null;
            d.showLoadingAnimationText = getNullableBoolSelect(view, '#DefaultShowLoadingAnimationText');

            d.navbarPosition = view.querySelector('#DefaultNavbarPosition').value || null;
            d.navbarColor = view.querySelector('#DefaultNavbarColor').value || null;
            d.navbarOpacity = getNullableRangeInput(view, '#DefaultNavbarOpacity');
            d.navbarAlwaysExpanded = getNullableBoolSelect(view, '#DefaultNavbarAlwaysExpanded');
            d.bottomNavbarStyle = view.querySelector('#DefaultBottomNavbarStyle').value || null;
            d.bottomNavbarTabs = getBottomNavbarTabSelects(view);
            d.enableFolderView = getNullableBoolSelect(view, '#DefaultEnableFolderView');
            d.showSeerrButton = getNullableBoolSelect(view, '#DefaultShowSeerrButton');
            d.showLiveTvButton = getNullableBoolSelect(view, '#DefaultShowLiveTvButton');
            d.showDownloadsButton = getNullableBoolSelect(view, '#DefaultShowDownloadsButton');
            d.showServerMessagesButton = getNullableBoolSelect(view, '#DefaultShowServerMessagesButton');

            d.mediaBarSourceType = view.querySelector('#DefaultMediaBarSourceType').value || null;
            var genreIds = Array.prototype.slice.call(view.querySelectorAll('.adminGenreCb:checked')).map(function (cb) { return cb.dataset.id; });
            d.mediaBarExcludedGenres = genreIds.length > 0 ? genreIds : null;
            d.mediaBarMode = view.querySelector('#DefaultMediaBarMode').value || null;
            d.mediaBarContentType = view.querySelector('#DefaultMediaBarContentType').value || null;
            d.mediaBarItemCount = getNullableIntInput(view, '#DefaultMediaBarItemCount');
            d.mediaBarAutoAdvance = getNullableBoolSelect(view, '#DefaultMediaBarAutoAdvance');
            d.mediaBarIntervalMs = getNullableIntInput(view, '#DefaultMediaBarIntervalMs');
            d.mediaBarRowPeek = getNullableBoolSelect(view, '#DefaultMediaBarRowPeek');
            d.mediaBarTrailerPreview = getNullableBoolSelect(view, '#DefaultMediaBarTrailerPreview');
            d.mediaBarTrailerAudio = getNullableBoolSelect(view, '#DefaultMediaBarTrailerAudio');
            d.trailerVolume = getNullableRangeInput(view, '#DefaultTrailerVolume');
            d.mediaBarTrailerCaptions = getNullableBoolSelect(view, '#DefaultMediaBarTrailerCaptions');
            var mediaPreviewMode = view.querySelector('#DefaultMediaPreviewMode').value;
            // Older apps only read the on and off switch, so it's written alongside the mode.
            d.episodePreviewEnabled = mediaPreviewMode ? mediaPreviewMode !== 'off' : null;
            d.mediaPreviewMode = mediaPreviewMode || null;
            d.previewAudioEnabled = getNullableBoolSelect(view, '#DefaultPreviewAudioEnabled');
            d.previewVolume = getNullableRangeInput(view, '#DefaultPreviewVolume');
            d.seasonalSurprise = view.querySelector('#DefaultSeasonalSurprise').value || null;
            d.seasonalDensity = view.querySelector('#DefaultSeasonalDensity').value || null;
            d.seasonalFollower = getNullableBoolSelect(view, '#DefaultSeasonalFollower');
            d.seasonalRowEnabled = getNullableBoolSelect(view, '#DefaultSeasonalRowEnabled');
            d.seasonalRowCountry = view.querySelector('#DefaultSeasonalRowCountry').value || null;
            d.seasonalRowHiddenHolidays = readSeasonalHiddenHolidays(view);

            // Clients store this one as text, unlike the millisecond fields below.
            d.resumeSubtractDuration = view.querySelector('#DefaultResumeSubtractDuration').value || null;
            d.unpauseRewindDuration = getNullableIntInput(view, '#DefaultUnpauseRewindDuration');
            d.skipBackLength = getNullableIntInput(view, '#DefaultSkipBackLength');
            d.skipForwardLength = getNullableIntInput(view, '#DefaultSkipForwardLength');

            d.defaultAudioLanguage = view.querySelector('#DefaultDefaultAudioLanguage').value.trim() || null;
            d.fallbackAudioLanguage = view.querySelector('#DefaultFallbackAudioLanguage').value.trim() || null;
            d.preferDefaultAudioTrack = getNullableBoolSelect(view, '#DefaultPreferDefaultAudioTrack');
            d.preferAudioDescription = getNullableBoolSelect(view, '#DefaultPreferAudioDescription');

            d.subtitleMode = view.querySelector('#DefaultSubtitleMode').value || null;
            d.defaultSubtitleLanguage = view.querySelector('#DefaultDefaultSubtitleLanguage').value.trim() || null;
            d.fallbackSubtitleLanguage = view.querySelector('#DefaultFallbackSubtitleLanguage').value.trim() || null;
            d.preferSdhSubtitles = getNullableBoolSelect(view, '#DefaultPreferSdhSubtitles');
            d.preferTextSubtitles = getNullableBoolSelect(view, '#DefaultPreferTextSubtitles');
            d.preferExternalSubtitles = getNullableBoolSelect(view, '#DefaultPreferExternalSubtitles');

            d.cinemaModeEnabled = getNullableBoolSelect(view, '#DefaultCinemaModeEnabled');
            d.cinemaModeSkipEnabled = getNullableBoolSelect(view, '#DefaultCinemaModeSkipEnabled');
            d.autoplayNextEpisode = getNullableBoolSelect(view, '#DefaultAutoplayNextEpisode');
            d.nextUpTimeout = getNullableIntInput(view, '#DefaultNextUpTimeout');
            d.stillWatchingBehavior = view.querySelector('#DefaultStillWatchingBehavior').value || null;
            d.mediaSegmentActions = getSegmentActionsValue(view);
            d.mediaSegmentCountdown = view.querySelector('#DefaultMediaSegmentCountdown').value || null;
            d.mediaSegmentAutoHide = view.querySelector('#DefaultMediaSegmentAutoHide').value || null;
            d.replaceSkipOutroWithNextUp = getNullableBoolSelect(view, '#DefaultReplaceSkipOutroWithNextUp');
            d.cinemaModeEpisodesEnabled = getNullableBoolSelect(view, '#DefaultCinemaModeEpisodesEnabled');

            if (view.querySelector('#DefaultCollectionPicker .adminCollectionCb')) {
                var collectionIds = Array.prototype.slice.call(view.querySelectorAll('#DefaultCollectionPicker .adminCollectionCb:checked')).map(function (cb) { return cb.dataset.id; });
                d.mediaBarCollectionIds = collectionIds.length > 0 ? collectionIds : null;
            }
            if (view.querySelector('#DefaultLibraryPicker .adminLibraryCb')) {
                var libraryIds = Array.prototype.slice.call(view.querySelectorAll('#DefaultLibraryPicker .adminLibraryCb:checked')).map(function (cb) { return cb.dataset.id; });
                d.mediaBarLibraryIds = libraryIds.length > 0 ? libraryIds : null;
            }

            d.homeRowsStyle = view.querySelector('#DefaultHomeRowsStyle').value || null;
            d.modernCardsOnMyMediaRow = getNullableBoolSelect(view, '#DefaultModernCardsOnMyMediaRow');
            d.fullScreenRows = getNullableBoolSelect(view, '#DefaultFullScreenRows');
            d.homeRowInfoOverlay = getNullableBoolSelect(view, '#DefaultHomeRowInfoOverlay');
            d.classicHomeRowsPadding = getNullableRangeInput(view, '#DefaultClassicHomeRowsPadding');
            d.modernHomeRowsPadding = getNullableRangeInput(view, '#DefaultModernHomeRowsPadding');
            d.posterSize = view.querySelector('#DefaultPosterSize').value || null;
            d.homeImageTypeContinueWatching = view.querySelector('#DefaultHomeImageTypeContinueWatching').value || null;
            d.homeRowImageTypes = getHomeRowImageTypesValue(view);
            d.displayFavoritesRows = getNullableBoolSelect(view, '#DefaultDisplayFavoritesRows');
            d.favoritesRowSortBy = view.querySelector('#DefaultFavoritesRowSortBy').value || null;
            d.displayCollectionsRows = getNullableBoolSelect(view, '#DefaultDisplayCollectionsRows');
            d.collectionsRowSortBy = view.querySelector('#DefaultCollectionsRowSortBy').value || null;
            d.displayGenresRows = getNullableBoolSelect(view, '#DefaultDisplayGenresRows');
            d.genresRowSortBy = view.querySelector('#DefaultGenresRowSortBy').value || null;
            d.displayPlaylistsRows = getNullableBoolSelect(view, '#DefaultDisplayPlaylistsRows');
            d.playlistsRowSortBy = view.querySelector('#DefaultPlaylistsRowSortBy').value || null;
            d.displayAudioRows = getNullableBoolSelect(view, '#DefaultDisplayAudioRows');
            d.audioRowsSortBy = view.querySelector('#DefaultAudioRowsSortBy').value || null;
            d.displayRewatchRow = getNullableBoolSelect(view, '#DefaultDisplayRewatchRow');
            d.rewatchSortBy = view.querySelector('#DefaultRewatchSortBy').value || null;
            d.displaySinceYouWatchedRows = getNullableBoolSelect(view, '#DefaultDisplaySinceYouWatchedRows');
            d.homeImageUseSeriesImage = getNullableBoolSelect(view, '#DefaultHomeImageUseSeriesImage');
            var homeSections = getHomeSectionsValue(view);
            d.homeSections = homeSections;
            d.homeRowOrder = getHomeRowOrderValue(view, homeSections);
            d.mergeContinueWatchingNextUp = getNullableBoolSelect(view, '#DefaultMergeContinueWatchingNextUp');
            d.continueWatchingDefaultAction = view.querySelector('#DefaultContinueWatchingDefaultAction').value || null;
            d.nextUpMaxDays = getNullableIntInput(view, '#DefaultNextUpMaxDays');

            d.enableMultiServerLibraries = getNullableBoolSelect(view, '#DefaultEnableMultiServerLibraries');
            d.mergeRecentRowsByType = getNullableBoolSelect(view, '#DefaultMergeRecentRowsByType');
            d.recentlyReleasedSeriesType = view.querySelector('#DefaultRecentlyReleasedSeriesType').value || null;
            d.groupItemsIntoCollections = getNullableBoolSelect(view, '#DefaultGroupItemsIntoCollections');
            d.showMediaDetailsOnLibraryPage = getNullableBoolSelect(view, '#DefaultShowMediaDetailsOnLibraryPage');
            d.libraryCardTitles = getNullableBoolSelect(view, '#DefaultLibraryCardTitles');
            d.useDetailedSubHeadings = getNullableBoolSelect(view, '#DefaultUseDetailedSubHeadings');
            d.hideBackdropsInLibraries = getNullableBoolSelect(view, '#DefaultHideBackdropsInLibraries');

            d.showShuffleButton = getNullableBoolSelect(view, '#DefaultShowShuffleButton');
            d.showGenresButton = getNullableBoolSelect(view, '#DefaultShowGenresButton');
            d.showFavoritesButton = getNullableBoolSelect(view, '#DefaultShowFavoritesButton');
            d.showLibrariesInToolbar = getNullableBoolSelect(view, '#DefaultShowLibrariesInToolbar');
            d.showLibrariesIndividually = getNullableBoolSelect(view, '#DefaultShowLibrariesIndividually');
            d.showSyncPlayButton = getNullableBoolSelect(view, '#DefaultShowSyncPlayButton');
            d.syncPlayEnabled = getNullableBoolSelect(view, '#DefaultSyncPlayEnabled');

            d.mdblistEnabled = getNullableBoolSelect(view, '#DefaultMdblistEnabled');
            d.tmdbEpisodeRatingsEnabled = getNullableBoolSelect(view, '#DefaultTmdbEpisodeRatingsEnabled');
            d.mdblistShowRatingBadges = getNullableBoolSelect(view, '#DefaultMdblistShowRatingBadges');
            d.mdblistShowRatingNames = getNullableBoolSelect(view, '#DefaultMdblistShowRatingNames');
            d.mdblistRatingSources = getRatingSourcesValue(view);
            d.homeRowsMdblistEnabled = getNullableBoolSelect(view, '#DefaultHomeRowsMdblistEnabled');
            d.homeRowsMdblistRatingSources = getRatingSourcesValue(view, '#DefaultHomeRowsMdblistRatingSourcesList');
            d.homeRowsTmdbEpisodeRatingsEnabled = getNullableBoolSelect(view, '#DefaultHomeRowsTmdbEpisodeRatingsEnabled');
            d.homeRowsMdblistShowRatingNames = getNullableBoolSelect(view, '#DefaultHomeRowsMdblistShowRatingNames');
            d.homeRowsMdblistShowRatingBadges = getNullableBoolSelect(view, '#DefaultHomeRowsMdblistShowRatingBadges');
            d.mediaBarMdblistEnabled = getNullableBoolSelect(view, '#DefaultMediaBarMdblistEnabled');
            d.mediaBarMdblistRatingSources = getRatingSourcesValue(view, '#DefaultMediaBarMdblistRatingSourcesList');
            d.mediaBarMdblistShowRatingNames = getNullableBoolSelect(view, '#DefaultMediaBarMdblistShowRatingNames');
            d.mediaBarMdblistShowRatingBadges = getNullableBoolSelect(view, '#DefaultMediaBarMdblistShowRatingBadges');
            d.seerrBlockNsfw = getNullableBoolSelect(view, '#DefaultSeerrBlockNsfw');
            d.seerrShowMissingCollectionItems = getNullableBoolSelect(view, '#DefaultSeerrShowMissingCollectionItems');
            d.showSeerrAvailabilityBadges = getNullableBoolSelect(view, '#DefaultShowSeerrAvailabilityBadges');
            // seerrRows carries more than the ordering, so merge rather than replace.
            var seerrRows = Object.assign({}, d.seerrRows || {});
            seerrRows.rowOrder = getSeerrDiscoveryRowOrder(view);
            d.seerrRows = seerrRows;
            d.seerrRowsSortBy = view.querySelector('#DefaultSeerrRowsSortBy').value || null;
            d.seerrRowsSortOrder = view.querySelector('#DefaultSeerrRowsSortOrder').value || null;

            d.confirmExit = getNullableBoolSelect(view, '#DefaultConfirmExit');
            d.keyboardStyle = view.querySelector('#DefaultKeyboardStyle').value || null;
            d.updateNotificationsEnabled = getNullableBoolSelect(view, '#DefaultUpdateNotificationsEnabled');
            d.hideDetailsMediaDescription = getNullableBoolSelect(view, '#DefaultHideDetailsMediaDescription');
            d.detailTrailersExternal = getNullableBoolSelect(view, '#DefaultDetailTrailersExternal');
            d.detailUseSeriesThumbnails = getNullableBoolSelect(view, '#DefaultDetailUseSeriesThumbnails');
            d.personPageGroupItems = getNullableBoolSelect(view, '#DefaultPersonPageGroupItems');
            d.homeRowsImageTypeOverride = getNullableBoolSelect(view, '#DefaultHomeRowsImageTypeOverride');
            d.hideHomeMediaDescription = getNullableBoolSelect(view, '#DefaultHideHomeMediaDescription');
            d.hideHomeWatchProgress = getNullableBoolSelect(view, '#DefaultHideHomeWatchProgress');
            d.hideHomeUnwatchedCount = getNullableBoolSelect(view, '#DefaultHideHomeUnwatchedCount');
            d.collectionsRowShowEpisodes = getNullableBoolSelect(view, '#DefaultCollectionsRowShowEpisodes');
            d.playlistsRowShowEpisodes = getNullableBoolSelect(view, '#DefaultPlaylistsRowShowEpisodes');
            d.displayStudiosRows = getNullableBoolSelect(view, '#DefaultDisplayStudiosRows');
            d.rewatchIncludeMovies = getNullableBoolSelect(view, '#DefaultRewatchIncludeMovies');
            d.rewatchIncludeShows = getNullableBoolSelect(view, '#DefaultRewatchIncludeShows');
            d.rewatchIncludeCollections = getNullableBoolSelect(view, '#DefaultRewatchIncludeCollections');
            d.sinceYouWatchedIncludeWatched = getNullableBoolSelect(view, '#DefaultSinceYouWatchedIncludeWatched');
            d.sinceYouWatched1Enabled = getNullableBoolSelect(view, '#DefaultSinceYouWatched1Enabled');
            d.sinceYouWatched2Enabled = getNullableBoolSelect(view, '#DefaultSinceYouWatched2Enabled');
            d.sinceYouWatched3Enabled = getNullableBoolSelect(view, '#DefaultSinceYouWatched3Enabled');
            d.sinceYouWatched4Enabled = getNullableBoolSelect(view, '#DefaultSinceYouWatched4Enabled');
            d.sinceYouWatched5Enabled = getNullableBoolSelect(view, '#DefaultSinceYouWatched5Enabled');
            d.displayAudioLatest = getNullableBoolSelect(view, '#DefaultDisplayAudioLatest');
            d.displayAudioLastPlayed = getNullableBoolSelect(view, '#DefaultDisplayAudioLastPlayed');
            d.displayAudioFavorites = getNullableBoolSelect(view, '#DefaultDisplayAudioFavorites');
            d.displayAudioPlaylists = getNullableBoolSelect(view, '#DefaultDisplayAudioPlaylists');
            d.displayAudioAlbumArtists = getNullableBoolSelect(view, '#DefaultDisplayAudioAlbumArtists');
            d.displayAudioArtists = getNullableBoolSelect(view, '#DefaultDisplayAudioArtists');
            d.displayAudioAlbums = getNullableBoolSelect(view, '#DefaultDisplayAudioAlbums');
            d.imdbRowsSortBy = view.querySelector('#DefaultImdbRowsSortBy').value || null;
            d.imdbRowsSortOrder = view.querySelector('#DefaultImdbRowsSortOrder').value || null;
            d.imdbTop250MoviesEnabled = getNullableBoolSelect(view, '#DefaultImdbTop250MoviesEnabled');
            d.imdbTop250TvShowsEnabled = getNullableBoolSelect(view, '#DefaultImdbTop250TvShowsEnabled');
            d.imdbMostPopularMoviesEnabled = getNullableBoolSelect(view, '#DefaultImdbMostPopularMoviesEnabled');
            d.imdbMostPopularTvShowsEnabled = getNullableBoolSelect(view, '#DefaultImdbMostPopularTvShowsEnabled');
            d.imdbTopEnglishMoviesEnabled = getNullableBoolSelect(view, '#DefaultImdbTopEnglishMoviesEnabled');
            d.imdbLowestRatedMoviesEnabled = getNullableBoolSelect(view, '#DefaultImdbLowestRatedMoviesEnabled');
            d.tmdbRowsSortBy = view.querySelector('#DefaultTmdbRowsSortBy').value || null;
            d.tmdbRowsSortOrder = view.querySelector('#DefaultTmdbRowsSortOrder').value || null;
            d.tmdbTrendingAllWeeklyEnabled = getNullableBoolSelect(view, '#DefaultTmdbTrendingAllWeeklyEnabled');
            d.tmdbTrendingMovieDailyEnabled = getNullableBoolSelect(view, '#DefaultTmdbTrendingMovieDailyEnabled');
            d.tmdbTrendingMovieWeeklyEnabled = getNullableBoolSelect(view, '#DefaultTmdbTrendingMovieWeeklyEnabled');
            d.tmdbTrendingTvDailyEnabled = getNullableBoolSelect(view, '#DefaultTmdbTrendingTvDailyEnabled');
            d.tmdbTrendingTvWeeklyEnabled = getNullableBoolSelect(view, '#DefaultTmdbTrendingTvWeeklyEnabled');
            d.tmdbPopularMoviesEnabled = getNullableBoolSelect(view, '#DefaultTmdbPopularMoviesEnabled');
            d.tmdbPopularTvEnabled = getNullableBoolSelect(view, '#DefaultTmdbPopularTvEnabled');
            d.tmdbTopRatedMoviesEnabled = getNullableBoolSelect(view, '#DefaultTmdbTopRatedMoviesEnabled');
            d.tmdbTopRatedTvEnabled = getNullableBoolSelect(view, '#DefaultTmdbTopRatedTvEnabled');
            d.tmdbNowPlayingMoviesEnabled = getNullableBoolSelect(view, '#DefaultTmdbNowPlayingMoviesEnabled');
            d.tmdbUpcomingMoviesEnabled = getNullableBoolSelect(view, '#DefaultTmdbUpcomingMoviesEnabled');
            d.tmdbAiringTodayTvEnabled = getNullableBoolSelect(view, '#DefaultTmdbAiringTodayTvEnabled');
            d.tmdbOnTheAirTvEnabled = getNullableBoolSelect(view, '#DefaultTmdbOnTheAirTvEnabled');
            d.enableRadarrCalendar = getNullableBoolSelect(view, '#DefaultEnableRadarrCalendar');
            d.enableSonarrCalendar = getNullableBoolSelect(view, '#DefaultEnableSonarrCalendar');
            d.mergeRadarrSonarrCalendars = getNullableBoolSelect(view, '#DefaultMergeRadarrSonarrCalendars');
            d.radarrCalendarShowCinema = getNullableBoolSelect(view, '#DefaultRadarrCalendarShowCinema');
            d.radarrCalendarShowDigital = getNullableBoolSelect(view, '#DefaultRadarrCalendarShowDigital');
            d.radarrCalendarShowPhysical = getNullableBoolSelect(view, '#DefaultRadarrCalendarShowPhysical');
            d.radarrCalendarShowDate = getNullableBoolSelect(view, '#DefaultRadarrCalendarShowDate');
            d.sonarrCalendarShowDate = getNullableBoolSelect(view, '#DefaultSonarrCalendarShowDate');
            d.sonarrCalendarShowEpisodeInfo = getNullableBoolSelect(view, '#DefaultSonarrCalendarShowEpisodeInfo');
            d.personalRatingStyle = view.querySelector('#DefaultPersonalRatingStyle').value || null;
            d.personPageSortOption = view.querySelector('#DefaultPersonPageSortOption').value || null;
            d.shuffleContentType = view.querySelector('#DefaultShuffleContentType').value || null;
            d.homeRowsImageType = view.querySelector('#DefaultHomeRowsImageType').value || null;
            d.mediaTypeBadgeBehavior = view.querySelector('#DefaultMediaTypeBadgeBehavior').value || null;
            d.audioRowsSortOrder = view.querySelector('#DefaultAudioRowsSortOrder').value || null;
            d.collectionsRowSortOrder = view.querySelector('#DefaultCollectionsRowSortOrder').value || null;
            d.favoritesRowSortOrder = view.querySelector('#DefaultFavoritesRowSortOrder').value || null;
            d.genresRowSortOrder = view.querySelector('#DefaultGenresRowSortOrder').value || null;
            d.genresRowItemFilter = view.querySelector('#DefaultGenresRowItemFilter').value || null;
            d.playlistsRowSortOrder = view.querySelector('#DefaultPlaylistsRowSortOrder').value || null;
            d.studiosRowSortBy = view.querySelector('#DefaultStudiosRowSortBy').value || null;
            d.studiosRowSortOrder = view.querySelector('#DefaultStudiosRowSortOrder').value || null;
            d.sinceYouWatchedSource = view.querySelector('#DefaultSinceYouWatchedSource').value || null;
            d.sinceYouWatchedSourceType = view.querySelector('#DefaultSinceYouWatchedSourceType').value || null;
            d.sinceYouWatchedSourceItem = view.querySelector('#DefaultSinceYouWatchedSourceItem').value || null;
            d.libraryStyle = view.querySelector('#DefaultLibraryStyle').value || null;
            d.bookLibraryStyle = view.querySelector('#DefaultBookLibraryStyle').value || null;
            d.musicLibraryStyle = view.querySelector('#DefaultMusicLibraryStyle').value || null;
            d.libraryPosterSize = view.querySelector('#DefaultLibraryPosterSize').value || null;
            d.playlistPosterSize = view.querySelector('#DefaultPlaylistPosterSize').value || null;
            d.favoritesViewStyle = view.querySelector('#DefaultFavoritesViewStyle').value || null;
            d.allGenresImageType = view.querySelector('#DefaultAllGenresImageType').value || null;
            d.audioSortOption = view.querySelector('#DefaultAudioSortOption').value || null;
            d.liveTvChannelSortBy = view.querySelector('#DefaultLiveTvChannelSortBy').value || null;
            d.epgMobileView = view.querySelector('#DefaultEpgMobileView').value || null;
            d.mediaBarOverlayColor = view.querySelector('#DefaultMediaBarOverlayColor').value || null;
            d.mediaBarOpacity = getNullableRangeInput(view, '#DefaultMediaBarOpacity');
            var osdVal = getButtonPickerValue(view, '#DefaultOsdButtonsList');
            d.osdButtonOrderTv = osdVal.order;
            d.osdButtonOrderMobile = osdVal.order;
            d.osdButtonOrderDesktop = osdVal.order;
            d.hiddenOsdButtonsTv = osdVal.hidden;
            d.hiddenOsdButtonsMobile = osdVal.hidden;
            d.hiddenOsdButtonsDesktop = osdVal.hidden;

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

    function loadAnimeMarkerDiagnostics(view) {
        var status = view.querySelector('#AnimeMarkerDiagStatus');
        if (!status) return;

        status.textContent = 'Checking every series against the AnimeFillerList catalog...';

        ApiClient.getJSON(ApiClient.getUrl('Moonfin/AnimeMarkers/Diagnostics')).then(function (info) {
            if (!info.catalogLoaded) {
                status.textContent = info.error || 'The AnimeFillerList catalog could not be loaded.';
                return;
            }

            var lines = [
                info.seriesMatched + ' of ' + info.seriesConsidered + ' series matched a show (the catalog holds ' + info.catalogShows + ')',
                info.showsCached + ' shows cached, ' + info.showsPending + ' waiting for the next sync',
                info.episodesClassified + ' episodes classified, ' + info.episodesFlagged + ' of them filler or mixed'
            ];

            if (info.unmatchedCount > 0) {
                lines.push(info.unmatchedCount + ' series matched nothing, for example: ' + (info.unmatched || []).slice(0, 8).join(', '));
            }

            status.innerHTML = lines.map(function (line) { return esc(line); }).join('<br>');
        }, function () {
            status.textContent = 'Could not read the anime marker diagnostics.';
        });
    }

    var ANIME_MARKER_COLORS = {
        Filler: '#e5534b',
        Mixed: '#d29922',
        AnimeCanon: '#3fb950',
        MangaCanon: '#58a6ff'
    };

    var ANIME_MARKER_LABELS = {
        Filler: 'Filler',
        Mixed: 'Mixed',
        AnimeCanon: 'Anime canon',
        MangaCanon: 'Manga canon'
    };

    // Finds the typed series through Emby's own item search. Resolves to null, with the reason
    // on the status line, when no name was typed or nothing matched.
    function findTypedAnimeMarkerSeries(view, busyMessage) {
        var status = view.querySelector('#AnimeMarkerPreviewStatus');
        var name = (view.querySelector('#AnimeMarkerPreviewName').value || '').trim();
        if (!name) {
            status.textContent = 'Type a series name first.';
            return Promise.resolve(null);
        }

        status.textContent = busyMessage(name);

        return ApiClient.getItems(ApiClient.getCurrentUserId(), {
            IncludeItemTypes: 'Series',
            Recursive: true,
            SearchTerm: name,
            Limit: 1
        }).then(function (result) {
            var series = ((result && result.Items) || [])[0] || null;
            if (!series) status.textContent = 'No series called "' + name + '" is in your library.';
            return series;
        });
    }

    function renderAnimeMarkerPreview(view, info) {
        var result = view.querySelector('#AnimeMarkerPreviewResult');
        var status = view.querySelector('#AnimeMarkerPreviewStatus');
        if (!result || !status) return;

        result.innerHTML = '';

        if (!info.matched) {
            status.textContent = info.series + ' did not match any show on AnimeFillerList. ' + (info.hint || '');
            return;
        }

        if (info.pending) {
            status.textContent = info.series + ' matched "' + info.title + '". ' + (info.hint || '');
            return;
        }

        status.innerHTML =
            esc(info.series) + ' matched <strong>' + esc(info.title) + '</strong> (' + esc(info.slug) + ')<br>' +
            'The site lists ' + info.showEpisodes + ' episodes and you have ' + info.libraryEpisodes +
            (info.libraryFiles > info.libraryEpisodes
                ? ' (' + info.libraryFiles + ' files, so some episodes have more than one copy)'
                : '') +
            ', of which ' + info.markedEpisodes + ' got a marker (' + info.fillerEpisodes + ' filler).<br>' +
            'Your library numbers this series <strong>' + esc(info.numbering) + '</strong>' +
            (info.recapKnown ? ', and recap flags were looked up.' : ', and recap flags were not looked up.') +
            '<br>' + describeAnimeMarkerMatching(info);

        var rows = info.episodes || [];
        if (rows.length === 0) return;

        var html = '<table style="width:100%;border-collapse:collapse;font-size:0.85em;">' +
            '<thead><tr style="text-align:left;color:rgba(128,128,128,0.7);">' +
            '<th style="padding:4px 8px;">Episode</th><th style="padding:4px 8px;">Abs</th>' +
            '<th style="padding:4px 8px;">Title</th><th style="padding:4px 8px;">Marker</th>' +
            '</tr></thead><tbody>';

        for (var i = 0; i < rows.length; i++) {
            var row = rows[i];
            var color = ANIME_MARKER_COLORS[row.kind] || 'rgba(128,128,128,0.6)';
            var label = ANIME_MARKER_LABELS[row.kind] || 'no data';
            if (row.recap) {
                label += ' + recap';
            }

            html += '<tr style="border-top:1px solid rgba(128,128,128,0.15);">' +
                '<td style="padding:4px 8px;white-space:nowrap;color:rgba(128,128,128,0.9);">S' +
                String(row.season) + 'E' + String(row.index) + '</td>' +
                '<td style="padding:4px 8px;color:rgba(128,128,128,0.7);">' + String(row.absolute) +
                (row.siteNumber != null && row.siteNumber !== row.absolute ? ' &rarr; #' + String(row.siteNumber) : '') + '</td>' +
                '<td style="padding:4px 8px;">' + esc(row.name || '') + '</td>' +
                '<td style="padding:4px 8px;white-space:nowrap;"><span style="display:inline-block;padding:1px 8px;border-radius:10px;font-size:0.9em;background:' +
                color + '33;color:' + color + ';">' + esc(label) + '</span></td>' +
                '</tr>';
        }

        result.innerHTML = html + '</tbody></table>';
    }

    function describeAnimeMarkerMatching(info) {
        if (info.matchedBy === 'airdate') {
            return 'Episodes were paired by <strong>air date</strong>, because only ' + info.numberAgreements + ' of ' +
                info.datedEpisodes + ' dated episodes aired when the site says their number did. The arrow shows the site&#39;s number.';
        }
        if (info.airDatesKnown === false) {
            return 'Episodes were paired by <strong>number</strong>. This table was cached before air dates were read, so use Fetch now to let them check the numbering.';
        }
        return 'Episodes were paired by <strong>number</strong>' +
            (info.datedEpisodes > 0 ? ' (' + info.numberAgreements + ' of ' + info.datedEpisodes + ' dated episodes agree with the site&#39;s air dates).' : '.');
    }

    function previewAnimeMarkers(view) {
        var status = view.querySelector('#AnimeMarkerPreviewStatus');
        view.querySelector('#AnimeMarkerPreviewResult').innerHTML = '';

        findTypedAnimeMarkerSeries(view, function (name) {
            return 'Looking up "' + name + '"...';
        }).then(function (series) {
            if (!series) return;

            return ApiClient.getJSON(ApiClient.getUrl('Moonfin/AnimeMarkers/Preview', { seriesId: series.Id }))
                .then(function (info) { renderAnimeMarkerPreview(view, info); });
        }).catch(function () {
            status.textContent = 'Could not build the preview.';
        });
    }

    function fetchAnimeMarkersNow(view) {
        var status = view.querySelector('#AnimeMarkerPreviewStatus');
        var btn = view.querySelector('#AnimeMarkerFetchBtn');
        btn.disabled = true;

        findTypedAnimeMarkerSeries(view, function (name) {
            return 'Fetching "' + name + '" from AnimeFillerList, this takes about ten seconds...';
        }).then(function (series) {
            if (!series) return;

            return ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('Moonfin/AnimeMarkers/Refresh', { seriesId: series.Id }),
                dataType: 'json'
            }).then(function (info) {
                if (!info.matched) {
                    status.textContent = 'That series does not match any show on AnimeFillerList.';
                    return;
                }

                if (!info.fetched) {
                    status.textContent = 'Matched "' + info.slug + '" but the page could not be read. Try again shortly.';
                    return;
                }

                status.textContent = 'Fetched ' + info.episodes + ' episodes for "' + info.slug + '". Building the preview...';
                previewAnimeMarkers(view);
            });
        }).catch(function () {
            status.textContent = 'Could not fetch that series.';
        }).finally(function () {
            btn.disabled = false;
        });
    }

    function loadAnimeMarkerLog(view) {
        var status = view.querySelector('#AnimeMarkerLogStatus');
        var output = view.querySelector('#AnimeMarkerLogOutput');
        if (!status || !output) return;

        status.textContent = 'Reading the marker log...';

        ApiClient.getJSON(ApiClient.getUrl('Moonfin/AnimeMarkers/Log', { lines: 300 })).then(function (info) {
            var lines = info.lines || [];
            if (lines.length === 0) {
                output.textContent = '';
                status.textContent = info.enabled
                    ? 'Logging is on but nothing has been recorded yet. Open a series in the app, then press Show log again.'
                    : 'Logging is off. Tick "Write a marker debug log", save, then reproduce the problem.';
                return;
            }

            output.textContent = lines.join('\n');
            output.scrollTop = output.scrollHeight;
            status.textContent = lines.length + ' lines from ' + info.path;
        }, function () {
            status.textContent = 'Could not read the marker log.';
        });
    }

    function clearAnimeMarkerLog(view) {
        var status = view.querySelector('#AnimeMarkerLogStatus');
        var output = view.querySelector('#AnimeMarkerLogOutput');
        if (!status) return;

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('Moonfin/AnimeMarkers/ClearLog'),
            dataType: 'json'
        }).then(function () {
            if (output) output.textContent = '';
            status.textContent = 'Log cleared. Reproduce the problem, then press Show log.';
        }, function () {
            status.textContent = 'Could not clear the marker log.';
        });
    }

    function clearAnimeMarkerCache(view) {
        var status = view.querySelector('#AnimeMarkerDiagStatus');
        if (!status) return;

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('Moonfin/AnimeMarkers/ClearCache'),
            dataType: 'json'
        }).then(function (result) {
            status.textContent = 'Cleared ' + ((result && result.cleared) || 0) + ' cached shows. Run the Anime Markers Sync task to fetch them again.';
        }, function () {
            status.textContent = 'Could not clear the anime marker cache.';
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

    function loadAdminPins(view) {
        var list = view.querySelector('#AdminPinList');
        if (!list) return;

        var result = view.querySelector('#AdminPinResult');
        function say(text, isError) {
            result.style.display = text ? '' : 'none';
            result.textContent = text || '';
            result.style.color = isError ? '#d9534f' : '';
        }

        var serverUrl = ApiClient.serverAddress ? ApiClient.serverAddress() : '';
        function send(method, id, body) {
            return fetch(serverUrl + '/Moonfin/Users/' + encodeURIComponent(id) + '/Pin', {
                method: method,
                headers: moonfinAuthHeaders(),
                body: body ? JSON.stringify(body) : undefined
            }).then(parseJsonResponse);
        }

        fetch(serverUrl + '/Moonfin/Admin/Pins', { method: 'GET', headers: moonfinAuthHeaders() })
            .then(parseJsonResponse)
            .then(function (users) {
                list.innerHTML = '';
                (Array.isArray(users) ? users : []).forEach(function (user) {
                    var row = document.createElement('div');
                    row.style.margin = '0 0 1.2em';

                    var title = document.createElement('div');
                    title.style.fontWeight = '600';
                    row.appendChild(title);

                    var input = document.createElement('input');
                    input.type = 'password';
                    input.maxLength = 4;
                    input.setAttribute('inputmode', 'numeric');
                    input.setAttribute('autocomplete', 'off');
                    input.placeholder = '4 digits';
                    input.style.width = '6em';
                    input.style.marginRight = '0.6em';
                    row.appendChild(input);

                    var setBtn = document.createElement('button');
                    setBtn.type = 'button';
                    setBtn.setAttribute('is', 'emby-button');
                    setBtn.className = 'raised';
                    setBtn.style.marginRight = '0.6em';
                    row.appendChild(setBtn);

                    var removeBtn = document.createElement('button');
                    removeBtn.type = 'button';
                    removeBtn.setAttribute('is', 'emby-button');
                    removeBtn.className = 'raised';
                    removeBtn.textContent = 'Remove PIN';
                    row.appendChild(removeBtn);

                    function show(hasPin) {
                        title.textContent = user.name + (user.isAdministrator ? ' (administrator)' : '') + ': ' + (hasPin ? 'PIN set' : 'no PIN');
                        setBtn.textContent = hasPin ? 'Change PIN' : 'Set PIN';
                        removeBtn.style.display = hasPin ? '' : 'none';
                    }

                    function done(promise, message, hasPin) {
                        setBtn.disabled = true;
                        removeBtn.disabled = true;
                        return promise.then(function () {
                            input.value = '';
                            show(hasPin);
                            say(message, false);
                        }, function () {
                            say('Couldn\'t save the PIN for ' + user.name + '.', true);
                        }).then(function () {
                            setBtn.disabled = false;
                            removeBtn.disabled = false;
                        });
                    }

                    setBtn.addEventListener('click', function () {
                        if (!/^[0-9]{4}$/.test(input.value)) {
                            say('A PIN is exactly four digits.', true);
                            return;
                        }
                        done(send('PUT', user.id, { pin: input.value }), 'Saved the PIN for ' + user.name + '.', true);
                    });

                    removeBtn.addEventListener('click', function () {
                        done(send('DELETE', user.id), 'Removed the PIN for ' + user.name + '.', false);
                    });

                    show(user.hasPin);
                    list.appendChild(row);
                });
                say('', false);
            })
            .catch(function () {
                say('Couldn\'t load the users.', true);
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

        var animeDiagBtn = view.querySelector('#AnimeMarkerDiagBtn');
        if (animeDiagBtn) animeDiagBtn.addEventListener('click', function () { loadAnimeMarkerDiagnostics(view); });

        var animeClearBtn = view.querySelector('#AnimeMarkerClearBtn');
        if (animeClearBtn) animeClearBtn.addEventListener('click', function () { clearAnimeMarkerCache(view); });

        var animePreviewBtn = view.querySelector('#AnimeMarkerPreviewBtn');
        if (animePreviewBtn) animePreviewBtn.addEventListener('click', function () { previewAnimeMarkers(view); });

        var animeFetchBtn = view.querySelector('#AnimeMarkerFetchBtn');
        if (animeFetchBtn) animeFetchBtn.addEventListener('click', function () { fetchAnimeMarkersNow(view); });

        var animePreviewInput = view.querySelector('#AnimeMarkerPreviewName');
        if (animePreviewInput) {
            animePreviewInput.addEventListener('keydown', function (event) {
                if (event.key === 'Enter') {
                    event.preventDefault();
                    previewAnimeMarkers(view);
                }
            });
        }

        var animeLogBtn = view.querySelector('#AnimeMarkerLogBtn');
        if (animeLogBtn) animeLogBtn.addEventListener('click', function () { loadAnimeMarkerLog(view); });

        var animeClearLogBtn = view.querySelector('#AnimeMarkerClearLogBtn');
        if (animeClearLogBtn) animeClearLogBtn.addEventListener('click', function () { clearAnimeMarkerLog(view); });
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
        loadAdminPins(view);
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
