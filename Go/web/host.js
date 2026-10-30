// Stands in for the parts of Jellyfin's dashboard that configPage.html calls,
// so the page from the Jellyfin plugin runs unchanged on Silo and on the Plex
// companion.
(function () {
    'use strict';

    var hostName = (window.moonbaseHost && window.moonbaseHost.name) || 'Silo';

    // What differs between hosts. Sections in hidden are for things the host
    // does itself or will never use, and sections in unsupported get a note
    // saying why they do nothing yet. Both still load and save their settings
    // unchanged.
    var hosts = {
        Silo: {
            example: 'silo.example.com',
            port: ':8090',
            webApp: 'Silo’s web app',
            restored: 'Restored the changes you hadn’t saved before your Silo session for this page ended. Press Save to keep them.',
            sessionEnded: 'Your Silo session for this page has ended. Open Moonbase again from Plugin Apps in Silo’s admin menu. Changes you tried to save are kept and come back when you do.',
            hidden: [
                'Connecting to this server',
                'Seerr Integration',
                'Recommendations',
                'Live TV Recordings',
                'Web Plugin Status',
                'Active Downloads'
            ],
            // Fields of the user defaults that only matter for those.
            hiddenFields: ['DefaultShowLiveTvButton', 'DefaultLiveTvChannelSortBy', 'DefaultEpgMobileView', 'DefaultSeerrOffNote'],
            unsupported: {
                'IMDb Charts': 'Not supported on Silo yet: Silo doesn’t let plugins read its library, which the charts are matched against. These settings do nothing on Silo for now.',
                'Anime Filler & Recap Markers': 'Not supported on Silo yet: Silo doesn’t let plugins read its library. These settings do nothing on Silo for now.',
                'Retro Games (EmulatorJS)': 'Not supported on Silo yet: Silo doesn’t let plugins read game files. These settings do nothing on Silo for now.',
                'MDBList Integration': 'Ratings work on Silo. The official MDBList lists aren’t supported on Silo yet, because Silo doesn’t let plugins read its library.',
                'PINs': 'Not supported on Silo yet: Moonbase doesn’t keep PINs on Silo. Apps use the PIN stored on the device.',
                'Uploaded Themes': 'Moonbase on Silo keeps up to 5 themes. Apps pick up a new or changed theme the next time they sign in.'
            },
            // Silo has no Live TV, takes requests itself rather than through
            // Seerr, and curates Home rows of its own.
            liveTv: false,
            ownRequests: true,
            serverRows: true
        },
        Plex: {
            example: 'plex.example.com',
            port: ':32400',
            webApp: 'Plex Web',
            restored: 'Restored the changes you hadn’t saved before your Moonbase sign-in ended. Press Save to keep them.',
            sessionEnded: 'You’re signed out of Moonbase. Sign in again with your Plex account. Changes you tried to save are kept and come back when you do.',
            signInUrl: 'admin/signin',
            hidden: [],
            hiddenFields: [],
            unsupported: {
                'Connecting to this server': 'Moonbase for Plex doesn’t host Moonfin web, so these settings do nothing on Plex.',
                'Seerr Integration': 'The Seerr proxy isn’t part of Moonbase for Plex. These settings do nothing on Plex.',
                'IMDb Charts': 'Not available on Plex. These settings do nothing on Plex.',
                'Recommendations': 'Not available on Plex. These settings do nothing on Plex.',
                'Anime Filler & Recap Markers': 'Not available on Plex. These settings do nothing on Plex.',
                'Retro Games (EmulatorJS)': 'Not available on Plex. These settings do nothing on Plex.',
                'Uploaded Themes': 'Moonbase for Plex keeps up to 5 themes. Apps pick up a new or changed theme the next time they sign in.',
                'Web Plugin Status': 'Plex Web can’t load Moonfin add-ons, so this doesn’t apply on Plex.',
                'Active Downloads': 'Not available on Plex.',
                'MDBList Integration': 'Ratings work on Plex. The official MDBList lists don’t.'
            },
            liveTv: true,
            ownRequests: false,
            serverRows: false
        }
    };
    var host = hosts[hostName] || hosts.Silo;

    // The page is served at <base>/admin and every Moonbase route lives under
    // <base>/Moonfin. On Silo <base> is this installation's plugin path, and on
    // the Plex companion it's the root.
    var base = location.origin + location.pathname.replace(/\/admin\/?$/, '');
    var draftKey = 'moonbaseConfigDraft:' + base;

    function getUrl(name, params) {
        var url = base + (name.charAt(0) === '/' ? '' : '/') + name;
        var query = new URLSearchParams();
        Object.keys(params || {}).forEach(function (k) {
            if (params[k] !== undefined && params[k] !== null) query.append(k, params[k]);
        });
        var qs = query.toString();
        return qs ? url + (url.indexOf('?') < 0 ? '?' : '&') + qs : url;
    }

    // These requests are signed with an HttpOnly cookie: the Plex companion's
    // sign-in, or Silo's five-minute launch cookie. Silo replaces that with a
    // fresh one on a request once it's halfway through, so a ping each minute
    // keeps the page signed in for as long as the admin's Silo login lasts.
    var nativeFetch = window.fetch.bind(window);
    var keepAlive = setInterval(function () {
        window.fetch(getUrl('Moonfin/Ping'), { cache: 'no-store' }).catch(function () { /* the next one tries again */ });
    }, 60 * 1000);
    window.fetch = function (input, init) {
        return nativeFetch(input, init).then(function (response) {
            if (response.status === 401) showSessionEnded();
            return response;
        });
    };

    function send(method, url, body, contentType) {
        var init = { method: method, headers: {} };
        if (body !== undefined && body !== null) {
            init.body = body;
            if (contentType) init.headers['Content-Type'] = contentType;
        }
        return window.fetch(url, init).then(function (response) {
            if (!response.ok) throw response;
            return response;
        });
    }

    function json(response) {
        return response.status === 204 ? null : response.json();
    }

    window.ApiClient = {
        serverAddress: function () { return base; },
        getUrl: getUrl,
        accessToken: function () { return ''; },
        getCurrentUserId: function () { return hostName.toLowerCase(); },
        ajax: function (request) {
            var body = request.data;
            var contentType = request.contentType;
            if (body !== undefined && typeof body !== 'string') {
                body = JSON.stringify(body);
                contentType = contentType || 'application/json';
            }
            return send(request.type || 'GET', request.url, body, contentType).then(function (response) {
                return request.dataType === 'json' ? json(response) : response;
            });
        },
        getJSON: function (url) {
            return send('GET', url).then(json);
        },
        getPluginConfiguration: function () {
            // Saving reads the configuration again, so only the first read
            // after opening the page may take the draft. It stays stored
            // until a save goes through.
            var draft = draftOffered ? null : (layerSwitch && layerSwitch.config) || readDraft();
            if (draft && !draftOffered && !layerSwitch) {
                notice(host.restored);
            }
            draftOffered = true;
            if (draft) {
                lastConfig = draft;
                return Promise.resolve(layerView(draft));
            }
            return this.getJSON(getUrl('Moonfin/Admin/Configuration')).then(function (config) {
                lastConfig = config;
                return layerView(config);
            }, function (error) {
                // Saving reads the configuration first. Once the launch cookie
                // has run out, carry on from the copy already loaded so the
                // save fails on its own request and keeps the changes.
                var status = error && error.status;
                if (status === 401 && lastConfig) return layerView(lastConfig);
                // The device switch is dropped with the save, or the next
                // Save would only reload.
                switchTo = null;
                // The page has no failure handler of its own, so its loading
                // overlay would stay up over the settings.
                Dashboard.hideLoadingMsg();
                if (status !== 401) toast(hostName + ' didn’t load the settings' + (status ? ' (status ' + status + ').' : '.'));
                throw error;
            });
        },
        updatePluginConfiguration: function (id, config) {
            config = layersOf(config);
            if (switchTo) {
                // A device picked in the defaults: the page fills its form
                // only when it loads, so it loads again with this copy.
                try {
                    sessionStorage.setItem(layerKey, JSON.stringify({ layer: switchTo, config: config }));
                    location.reload();
                    return new Promise(function () { /* the page reloads */ });
                } catch (e) {
                    switchTo = null;
                    Dashboard.hideLoadingMsg();
                    toast('Save first, then pick the device.');
                    return Promise.reject(e);
                }
            }
            return send('POST', getUrl('Moonfin/Admin/Configuration'), JSON.stringify(config), 'application/json').then(function () {
                clearDraft();
            }, function (error) {
                Dashboard.hideLoadingMsg();
                var status = error && error.status;
                if (status === 401) {
                    saveDraft(config);
                } else {
                    toast(hostName + ' didn’t save the settings' + (status ? ' (status ' + status + ').' : '.'));
                }
                throw new Error('Saving the Moonbase settings failed' + (status ? ' with status ' + status : ''));
            });
        },
        // Neither host lets Moonbase list its accounts, so this lists the ones
        // that have used Moonfin with this server.
        getUsers: function () {
            return this.getJSON(getUrl('Moonfin/Admin/Users')).then(function (users) {
                // After the page has filled its single-user list from them.
                setTimeout(function () { addProfileOptions(users); });
                return users;
            });
        },
        getUserViews: function () {
            return this.getJSON(getUrl('Moonfin/Whoami')).then(function (who) {
                var types = { movie: 'movies', tv: 'tvshows' };
                return {
                    Items: (who && who.libraries || []).map(function (l) {
                        return { Id: l.id, Name: l.name, CollectionType: types[l.mediaType] || l.mediaType };
                    })
                };
            });
        },
        // Neither host lets the page read library items.
        getItems: function () {
            return Promise.resolve({ Items: [], TotalRecordCount: 0 });
        }
    };

    var lastConfig = null;
    var draftOffered = false;

    // Admin defaults per device type, which only Moonbase for Silo and Plex
    // have. The page edits
    // DefaultUserSettings, so it's handed one layer at a time: the defaults
    // for all devices, or one device's on top of them. A device keeps only
    // what differs from all devices, in DefaultDeviceSettings.
    var layerKey = 'moonbaseDefaultsLayer:' + base;
    var layerSwitch = null;
    try {
        layerSwitch = JSON.parse(sessionStorage.getItem(layerKey) || 'null');
        sessionStorage.removeItem(layerKey);
    } catch (e) { /* starts on all devices */ }
    var devices = ['desktop', 'mobile', 'tv'];
    var layer = layerSwitch ? layerSwitch.layer : 'global';
    var switchTo = null;
    var layers = null;

    function defaultsFor(name) {
        var all = layers.global;
        if (name === 'global') return all;
        var own = layers[name];
        var view = Object.assign({}, all);
        // A device's home layout replaces the shared one whole, as on the server.
        if (own.homeSections != null || own.homeRowOrder != null) {
            delete view.homeSections;
            delete view.homeRowOrder;
        }
        return Object.assign(view, own);
    }

    // The configuration as the page sees it: the layer picked in the
    // defaults, with an uploaded default theme as "custom:<id>".
    function layerView(config) {
        if (!layers) {
            var saved = config.DefaultDeviceSettings || {};
            layers = { global: config.DefaultUserSettings || {} };
            devices.forEach(function (d) { layers[d] = saved[d] || {}; });
        }
        var view = clone(config);
        var defaults = view.DefaultUserSettings = clone(defaultsFor(layer));
        if (defaults.customThemeId) defaults.visualTheme = 'custom:' + defaults.customThemeId;
        fillUploadedThemes(config.UploadedThemes);
        return view;
    }

    // Takes the page's DefaultUserSettings back into the layer it showed and
    // returns the configuration to store.
    function layersOf(config) {
        var shown = config.DefaultUserSettings || {};
        var theme = shown.visualTheme || '';
        var custom = theme.indexOf('custom:') === 0;
        shown.customThemeId = custom ? theme.slice(7) : null;
        if (custom) shown.visualTheme = null;
        if (layer === 'global') {
            layers.global = shown;
        } else {
            var own = {};
            Object.keys(shown).forEach(function (k) {
                if (shown[k] != null && canonicalJson(shown[k]) !== canonicalJson(layers.global[k])) own[k] = shown[k];
            });
            if (own.homeSections != null || own.homeRowOrder != null) {
                own.homeSections = shown.homeSections;
                own.homeRowOrder = shown.homeRowOrder;
            }
            // A built-in theme for this device has to clear the uploaded one
            // set for all devices, or that one would still win.
            if (own.visualTheme && layers.global.customThemeId) own.customThemeId = '';
            layers[layer] = own;
        }
        config.DefaultUserSettings = layers.global;
        config.DefaultDeviceSettings = {};
        devices.forEach(function (d) { config.DefaultDeviceSettings[d] = layers[d]; });
        return config;
    }

    // Server JSON comes back with sorted keys, so compare that way.
    function canonicalJson(value) {
        if (Array.isArray(value)) return '[' + value.map(canonicalJson).join(',') + ']';
        if (value && typeof value === 'object') {
            return '{' + Object.keys(value).sort().map(function (k) {
                return JSON.stringify(k) + ':' + canonicalJson(value[k]);
            }).join(',') + '}';
        }
        return JSON.stringify(value == null ? null : value);
    }

    // Uploaded themes join the App Theme default as "custom:<id>". The list
    // is the one the configuration was loaded with.
    function fillUploadedThemes(items) {
        var select = document.getElementById('DefaultVisualTheme');
        if (!select) return;
        var group = document.getElementById('MoonbaseUploadedThemes');
        if (!group) {
            group = document.createElement('optgroup');
            group.id = 'MoonbaseUploadedThemes';
            group.label = 'Uploaded';
            select.appendChild(group);
        }
        // Saving reads the configuration again after the admin picked a theme.
        var current = select.value;
        group.innerHTML = '';
        (items || []).forEach(function (item) {
            var id = item.id || item.Id;
            if (!id) return;
            var option = document.createElement('option');
            option.value = 'custom:' + id;
            option.textContent = item.displayName || item.DisplayName || id;
            group.appendChild(option);
        });
        group.hidden = !group.children.length;
        select.value = current;
    }

    function addDefaultsLayerPicker(page) {
        var bar = page.querySelector('.moonfinTabPanel[data-tab="defaults"] .defaultsSubnavBar');
        var form = document.getElementById('MoonfinConfigForm');
        if (!bar || !form) return;
        var box = document.createElement('div');
        box.className = 'inputContainer';
        box.innerHTML = '<label class="inputLabel inputLabelUnfocused" for="MoonbaseDefaultsLayer">Defaults For</label>'
            + '<select id="MoonbaseDefaultsLayer" is="emby-select">'
            + '<option value="global">All devices</option><option value="desktop">Desktop</option>'
            + '<option value="mobile">Mobile</option><option value="tv">TV</option></select>'
            + '<div class="fieldDescription">A device starts out showing your all-devices values. Anything you change there applies to that device only; set it back to the all-devices value, or to Not set, to follow all devices again. Changes are kept when you switch, and Save stores every device. A user’s own settings still come first, then your defaults for their device, then your defaults for all devices.</div>';
        bar.before(box);
        upgrade(box);
        var select = box.querySelector('select');
        select.value = layer;
        select.addEventListener('change', function () {
            var next = select.value;
            select.value = layer;
            if (!form.reportValidity()) return;
            switchTo = next;
            form.requestSubmit();
        });
    }

    function clone(value) {
        return JSON.parse(JSON.stringify(value));
    }

    // A draft older than this is dropped, because someone may have changed
    // the settings in the meantime.
    var draftMaxAgeMs = 60 * 60 * 1000;

    function readDraft() {
        try {
            var draft = JSON.parse(sessionStorage.getItem(draftKey) || 'null');
            if (draft && Date.now() - draft.savedAt < draftMaxAgeMs) return draft.config;
            clearDraft();
        } catch (e) { /* no usable draft */ }
        return null;
    }
    function saveDraft(config) {
        try { sessionStorage.setItem(draftKey, JSON.stringify({ savedAt: Date.now(), config: config })); } catch (e) { /* the banner still explains */ }
    }
    function clearDraft() {
        try { sessionStorage.removeItem(draftKey); } catch (e) { /* nothing stored */ }
    }

    var loadingCount = 0;
    function loadingEl() {
        var el = document.getElementById('moonbaseLoading');
        if (!el) {
            el = document.createElement('div');
            el.id = 'moonbaseLoading';
            el.className = 'moonbaseLoading';
            el.innerHTML = '<div class="moonbaseSpinner" role="progressbar" aria-label="Loading"></div>';
            document.body.appendChild(el);
        }
        return el;
    }

    function toast(text) {
        var el = document.createElement('div');
        el.className = 'moonbaseToast';
        el.textContent = text;
        document.body.appendChild(el);
        setTimeout(function () { el.classList.add('is-visible'); }, 10);
        setTimeout(function () {
            el.classList.remove('is-visible');
            setTimeout(function () { el.remove(); }, 300);
        }, 3300);
    }

    window.Dashboard = {
        showLoadingMsg: function () {
            loadingCount++;
            loadingEl().classList.add('is-visible');
        },
        hideLoadingMsg: function () {
            loadingCount = Math.max(0, loadingCount - 1);
            if (!loadingCount) loadingEl().classList.remove('is-visible');
        },
        processPluginConfigurationUpdateResult: function () {
            loadingCount = 0;
            loadingEl().classList.remove('is-visible');
            toast('Settings saved.');
        }
    };

    function notice(text, warn, href) {
        var bar = document.querySelector('.moonbaseHostNotices');
        if (!bar) return;
        var el = document.createElement('div');
        el.className = 'moonbaseHostNotice' + (warn ? ' is-warning' : '');
        el.textContent = text;
        if (href) {
            var link = document.createElement('a');
            link.href = href;
            link.textContent = 'Sign in';
            el.appendChild(document.createTextNode(' '));
            el.appendChild(link);
        }
        bar.appendChild(el);
    }

    var sessionEndedShown = false;
    function showSessionEnded() {
        if (sessionEndedShown) return;
        sessionEndedShown = true;
        clearInterval(keepAlive);
        notice(host.sessionEnded, true, host.signInUrl && base + '/' + host.signInUrl);
        window.scrollTo(0, 0);
    }

    // Jellyfin's dashboard upgrades these elements when a page loads. The page's
    // CSS expects the upgraded markup.
    function upgrade(root) {
        root.querySelectorAll('input[is="emby-checkbox"]:not([data-embycheckbox])').forEach(function (input) {
            input.setAttribute('data-embycheckbox', 'true');
            input.classList.add('emby-checkbox');
            var label = input.closest('label');
            if (!label) return;
            label.classList.add('emby-checkbox-label');
            var outline = document.createElement('span');
            outline.className = 'checkboxOutline';
            outline.innerHTML = '<span class="material-icons checkboxIcon checkboxIcon-checked" aria-hidden="true"></span>';
            label.appendChild(outline);
        });
        [['input', 'emby-input'], ['select', 'emby-select'], ['button', 'emby-button'], ['textarea', 'emby-textarea']].forEach(function (pair) {
            root.querySelectorAll(pair[0] + '[is="' + pair[1] + '"]').forEach(function (el) { el.classList.add(pair[1]); });
        });
    }

    // The copied page names the server Jellyfin. Only text a person reads is
    // changed, never code or values.
    function renameServer(root) {
        var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
            acceptNode: function (node) {
                var tag = node.parentNode && node.parentNode.nodeName;
                return tag === 'SCRIPT' || tag === 'STYLE' ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT;
            }
        });
        var node;
        while ((node = walker.nextNode())) {
            if (/jellyfin/i.test(node.nodeValue)) node.nodeValue = swap(node.nodeValue);
        }
        root.querySelectorAll('[placeholder*="ellyfin"], [title*="ellyfin"]').forEach(function (el) {
            ['placeholder', 'title'].forEach(function (attr) {
                if (el.hasAttribute(attr)) el.setAttribute(attr, swap(el.getAttribute(attr)));
            });
        });
    }
    function swap(text) {
        return text
            .replace(/\s*\(Jellyfin \d+\+\)/g, '')
            .replace(/jellyfin\.example\.com/g, host.example)
            .replace(/:8096\b/g, host.port)
            .replace(/\bjellyfin-web\b/g, host.webApp)
            .replace(/\bJellyfin\b/g, hostName);
    }

    function adaptSections(root) {
        root.querySelectorAll('.sectionTitle').forEach(function (title) {
            var name = title.textContent.trim();
            if (host.hidden.indexOf(name) >= 0) {
                title.closest('.verticalSection').classList.add('moonbaseHidden');
                return;
            }
            var text = host.unsupported[name];
            if (!text) return;
            var note = document.createElement('div');
            note.className = 'moonbaseSiloNote';
            note.textContent = text;
            title.insertAdjacentElement('afterend', note);
        });
        host.hiddenFields.forEach(function (id) {
            var el = document.getElementById(id);
            if (el) (el.closest('.inputContainer, .checkboxContainer') || el).classList.add('moonbaseHidden');
        });
        if (!host.liveTv) {
            var liveTvHeading = document.getElementById('DefaultLiveTvChannelSortBy');
            liveTvHeading = liveTvHeading && liveTvHeading.closest('.inputContainer').previousElementSibling;
            if (liveTvHeading && liveTvHeading.tagName === 'H4') liveTvHeading.classList.add('moonbaseHidden');
        }

        // A tab left with nothing to show goes too.
        root.querySelectorAll('.moonfinTabPanel').forEach(function (panel) {
            if (panel.querySelector('.verticalSection:not(.moonbaseHidden)')) return;
            var tab = panel.getAttribute('data-tab');
            var nav = root.querySelector('.moonfinNavItem[data-tab="' + tab + '"]');
            if (nav) nav.classList.add('moonbaseHidden');
            try {
                if (localStorage.getItem('moonfinAdminActiveTab') === tab) localStorage.removeItem('moonfinAdminActiveTab');
            } catch (e) { /* the page opens on its first tab */ }
        });

        if (!host.ownRequests) return;
        // The host's own requests don't go through Moonbase, so they don't
        // rely on sync.
        root.querySelectorAll('.moonfinChip').forEach(function (chip) {
            if (chip.textContent.trim() === 'Seerr requests') chip.classList.add('moonbaseHidden');
        });
        root.querySelectorAll('.moonfinNavSub').forEach(function (sub) {
            sub.textContent = sub.textContent.replace('Seerr, ', '');
        });

        // The Seerr options of the user defaults drive the host's own requests.
        var defaults = root.querySelector('.moonfinTabPanel[data-tab="defaults"]');
        if (defaults) renameSeerr(defaults);
    }

    function renameSeerr(root) {
        var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
        var node;
        while ((node = walker.nextNode())) {
            if (node.nodeValue.indexOf('Seerr') >= 0) node.nodeValue = node.nodeValue.replace(/Seerr/g, 'Requests');
        }
    }

    // Silo's own Home rows, by section type. Moonbase can't read a server's
    // section ids, so a row names its type and the app shows that profile's
    // first Silo row of the type. Continue watching and next up are left out
    // because Moonfin builds those rows itself.
    var siloRows = [
        ['next_in_series', 'Next in series'],
        ['watchlist', 'Watchlist'],
        ['favorites', 'Favorites'],
        ['recently_added', 'Recently added'],
        ['recently_released', 'New releases'],
        ['new_to_library', 'New this month'],
        ['returning_shows', 'Returning shows'],
        ['trending_on_server', 'Trending on this server'],
        ['most_watched', 'Most watched'],
        ['trending_discover', 'Trending worldwide'],
        ['profile_activity_feed', 'What others just watched'],
        ['recommended_for_you', 'Recommended for you'],
        ['because_you_watched', 'Because you watched'],
        ['similar_users_liked', 'Profiles like you enjoyed'],
        ['taste_match', 'Top picks today'],
        ['mood_collection', 'Mood picks'],
        ['seasonal_themed', 'Seasonal picks'],
        ['editorial_spotlight', 'Spotlight'],
        ['format_showcase', '4K & HDR showcase'],
        ['hidden_gems', 'Hidden gems'],
        ['critically_acclaimed', 'Critically acclaimed'],
        ['forgotten_favorites', 'Forgotten favorites'],
        ['genre_roulette', 'Genre roulette'],
        ['random', 'Surprise me'],
        ['short_watches', 'Short & sweet'],
        ['anniversaries', 'Anniversaries'],
        ['collection', 'A collection'],
        ['custom_filter', 'Titles matching rules'],
        ['admin_curated_list', 'Editor’s picks']
    ];

    // The page's row catalog and helpers are globals of its script, so the
    // Home rows builder is adjusted through them.
    function adaptHomeRows() {
        if (!host.liveTv) {
            // Left out of the rows on offer only: a saved layout that has
            // one still loads and saves it.
            var liveTv = { livetv: true, onnow: true, activerecordings: true };
            var initAvailable = window.initializeHomeLayoutAvailableRows;
            window.initializeHomeLayoutAvailableRows = function () {
                initAvailable();
                HOME_LAYOUT_STATE.available.builtin = HOME_LAYOUT_STATE.available.builtin.filter(function (c) {
                    return !liveTv[c.section.type];
                });
            };
        }
        HOME_SECTION_DEFINITIONS.forEach(function (row) {
            if (!host.ownRequests) return;
            if (row.type === 'seerr_watchlist') row.label = 'Requests Watchlist';
            else row.label = row.label.replace(/^Seerr /, '');
        });
        for (var j = BOTTOM_NAVBAR_TABS.length - 1; j >= 0; j--) {
            if (!host.liveTv && BOTTOM_NAVBAR_TABS[j][0] === 'liveTv') BOTTOM_NAVBAR_TABS.splice(j, 1);
            else if (host.ownRequests) BOTTOM_NAVBAR_TABS[j][1] = BOTTOM_NAVBAR_TABS[j][1].replace('Seerr', 'Requests');
        }
        if (host.ownRequests) {
            HOME_LAYOUT_TABS.forEach(function (tab) {
                if (tab.id === 'seerr') tab.label = 'Requests';
            });
            var builtinCandidate = window.createBuiltinCandidate;
            window.createBuiltinCandidate = function (definition) {
                var candidate = builtinCandidate(definition);
                if (candidate.meta === 'Seerr') candidate.meta = 'Requests';
                return candidate;
            };
            // Requests are always on, so the request defaults don't wait for
            // the hidden Seerr switch.
            for (var k = FIELD_DEPENDENCIES.length - 1; k >= 0; k--) {
                if (FIELD_DEPENDENCIES[k].parent === 'SeerrEnabled') FIELD_DEPENDENCIES.splice(k, 1);
            }
        }
        var sectionMeta = window.homeSectionMeta;
        window.homeSectionMeta = function (section) {
            if (section && section.pluginSource === 'server') return hostName;
            var meta = sectionMeta(section);
            return host.ownRequests && meta === 'Seerr' ? 'Requests' : meta;
        };
        if (!host.serverRows) return;
        HOME_LAYOUT_TABS.splice(1, 0, { id: 'silo', label: hostName });
        HOME_LAYOUT_STATE.available.silo = [];

        var loadDynamicRows = window.loadHomeLayoutDynamicRows;
        window.loadHomeLayoutDynamicRows = function () {
            loadDynamicRows();
            setHomeLayoutAvailable('silo', siloRows.map(function (row) {
                return createDynamicCandidate('silo', 'server', 'type:' + row[0], '', row[1], hostName + ' row', '');
            }));
        };
    }

    document.addEventListener('DOMContentLoaded', function () {
        var page = document.getElementById('MoonfinConfigPage');
        upgrade(document);
        renameServer(page);
        adaptSections(page);
        try {
            adaptHomeRows();
        } catch (e) {
            // The page's globals changed: the builder keeps the Jellyfin rows.
            console.error('Moonbase: adapting the Home rows builder failed', e);
        }
        addDefaultsLayerPicker(page);
        // An upload or delete on the Themes tab redraws its list; the App
        // Theme default follows it.
        var renderThemes = window.renderAdminThemesList;
        window.renderAdminThemesList = function (items) {
            renderThemes(items);
            fillUploadedThemes(items);
        };
        new MutationObserver(function (records) {
            records.forEach(function (record) { upgrade(record.target); });
        }).observe(page, { childList: true, subtree: true });

        page.dispatchEvent(new CustomEvent('pageshow', { bubbles: true }));
        bindRemoveUserData();
    });

    // The single-user list becomes a menu of accounts.
    // An account picks its own profile; hovering it (or its arrow) shows its
    // other profiles, and all of its profiles together. The page still reads
    // the hidden select, so each choice is an option there too, whose text is
    // what the page's messages name.
    var listedUsers = [];

    function addProfileOptions(users) {
        var select = document.getElementById('SingleUserSelect');
        if (!select) return;
        listedUsers = users || [];
        var current = select.value;
        select.querySelectorAll('option[data-profile]').forEach(function (o) { o.remove(); });
        function addOption(value, text) {
            var option = document.createElement('option');
            option.value = value;
            option.dataset.profile = 'true';
            option.textContent = text;
            select.appendChild(option);
        }
        var entries = [];
        listedUsers.forEach(function (user) {
            var account = Array.prototype.find.call(select.options, function (o) { return o.value === user.Id; });
            if (!account) return;
            var profiles = user.Profiles || [];
            var primary = profiles.filter(function (p) { return p.Primary; })[0];
            var others = profiles.filter(function (p) { return p !== primary; });
            var entry = { label: user.Name, value: user.Id, choices: [] };
            account.textContent = user.Name;
            if (others.length && primary) {
                account.textContent = user.Name + ' (all profiles)';
                addOption(primary.Id, user.Name);
                entry.value = primary.Id;
                entry.choices.push({ label: 'All profiles', value: user.Id });
            }
            others.forEach(function (p) {
                addOption(p.Id, user.Name + ' \u203a ' + p.Name);
                entry.choices.push({ label: p.Name, value: p.Id });
            });
            entries.push(entry);
        });
        if (current) select.value = current;
        renderUserMenu(select, entries);
    }

    function renderUserMenu(select, entries) {
        var picker = document.getElementById('MoonbaseUserPicker');
        if (!picker) {
            picker = document.createElement('div');
            picker.id = 'MoonbaseUserPicker';
            picker.className = 'moonbaseUserPicker';
            picker.innerHTML = '<button type="button" class="emby-select moonbaseUserPickerButton" aria-haspopup="menu" aria-expanded="false"></button><div class="moonbaseUserMenu" role="menu"></div>';
            select.after(picker);
            select.classList.add('moonbaseHidden');
            var button = picker.firstChild;
            button.addEventListener('click', function () {
                var open = picker.classList.toggle('is-open');
                button.setAttribute('aria-expanded', String(open));
            });
            picker.addEventListener('click', function (e) {
                var choice = e.target.closest('[data-value]');
                if (!choice) return;
                select.value = choice.getAttribute('data-value');
                select.dispatchEvent(new Event('change', { bubbles: true }));
                close();
                showChoice();
            });
            document.addEventListener('click', function (e) {
                if (!picker.contains(e.target)) close();
            });
            picker.addEventListener('keydown', function (e) {
                if (e.key === 'Escape') { close(); button.focus(); }
            });
        }
        function close() {
            picker.classList.remove('is-open');
            picker.firstChild.setAttribute('aria-expanded', 'false');
        }
        function showChoice() {
            var option = select.selectedOptions[0];
            picker.firstChild.textContent = option ? option.textContent : 'Select a user';
            picker.querySelectorAll('[data-value]').forEach(function (b) {
                b.classList.toggle('is-selected', b.getAttribute('data-value') === select.value);
            });
        }
        var menu = picker.querySelector('.moonbaseUserMenu');
        menu.innerHTML = '';
        entries.forEach(function (entry) {
            var item = document.createElement('div');
            item.className = 'moonbaseUserMenuItem';
            var main = document.createElement('button');
            main.type = 'button';
            main.setAttribute('role', 'menuitem');
            main.setAttribute('data-value', entry.value);
            main.textContent = entry.label;
            item.appendChild(main);
            if (entry.choices.length) {
                var more = document.createElement('button');
                more.type = 'button';
                more.className = 'moonbaseUserMore';
                more.setAttribute('aria-label', 'Profiles of ' + entry.label);
                more.innerHTML = '<span class="material-icons" aria-hidden="true">chevron_right</span>';
                item.appendChild(more);
                var sub = document.createElement('div');
                sub.className = 'moonbaseUserSubmenu';
                sub.setAttribute('role', 'menu');
                entry.choices.forEach(function (choice) {
                    var b = document.createElement('button');
                    b.type = 'button';
                    b.setAttribute('role', 'menuitem');
                    b.setAttribute('data-value', choice.value);
                    b.textContent = choice.label;
                    sub.appendChild(b);
                });
                item.appendChild(sub);
            }
            menu.appendChild(item);
        });
        showChoice();
    }

    // Removes the stored settings of the account, or
    // the one profile, picked in the page's single-user list.
    function bindRemoveUserData() {
        var select = document.getElementById('SingleUserSelect');
        var after = document.getElementById('ApplySingleUserResult');
        if (!select || !after) return;
        var help = document.createElement('div');
        help.className = 'fieldDescription';
        help.style.margin = '1.6em 0 0.4em';
        help.textContent = 'Or remove everything Moonbase keeps for the selected user, on every profile, or for just the selected profile. Use it for an account or profile deleted from ' + hostName + ', since ' + hostName + ' doesn’t tell Moonbase about that. If they sign in again, they start from the defaults.';
        var button = document.createElement('button');
        button.type = 'button';
        button.className = 'raised emby-button';
        button.innerHTML = '<span>Remove Selected User’s Data</span>';
        var result = document.createElement('div');
        result.className = 'fieldDescription';
        result.style.cssText = 'display:none; margin-top:0.8em;';
        after.after(help, button, result);
        function show(text, failed) {
            result.style.display = '';
            result.style.color = failed ? '#d9534f' : '#52b54b';
            result.textContent = text;
        }
        button.addEventListener('click', function () {
            var option = select.selectedOptions[0];
            if (!option || !select.value) {
                show('Select a user first.', true);
                return;
            }
            var name = option.textContent;
            var target = select.value;
            var oneProfile = !!option.dataset.profile;
            if (!window.confirm('Remove all Moonbase data for ' + name + '?\n\nThis permanently erases their synced settings ' + (oneProfile ? 'on this profile.' : 'on every profile.'))) return;
            button.disabled = true;
            send('DELETE', getUrl('Moonfin/Admin/Users/' + encodeURIComponent(target))).then(json).then(function (body) {
                if (oneProfile) {
                    listedUsers.forEach(function (user) {
                        var before = (user.Profiles || []).length;
                        user.Profiles = (user.Profiles || []).filter(function (p) { return p.Id !== target; });
                        // Its last profile gone, the account has nothing left.
                        if (before && !user.Profiles.length) {
                            var account = Array.prototype.find.call(select.options, function (o) { return o.value === user.Id; });
                            if (account) account.remove();
                        }
                    });
                } else {
                    option.remove();
                }
                addProfileOptions(listedUsers);
                show('Removed ' + name + ' (' + body.profilesRemoved + ' profile' + (body.profilesRemoved === 1 ? '' : 's') + ').');
            }, function (error) {
                show('Moonbase didn’t remove the data' + (error && error.status ? ' (status ' + error.status + ').' : '.'), true);
            }).finally(function () {
                button.disabled = false;
            });
        });
    }
})();
