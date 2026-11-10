// Stands in for the parts of Jellyfin's dashboard that configPage.html calls,
// so the page runs on Silo and on the Plex companion. configPage.html stays an
// exact copy of Jellyfin/backend/Pages/configPage.html. This file finds its
// sections by title, changes the Home rows builder through its globals, and
// adds a card after Copy Settings that updates the Actions user lists, so
// check those after copying in a newer page.
(function () {
    'use strict';

    var hostName = (window.moonbaseHost && window.moonbaseHost.name) || 'Silo';

    // Sections in hidden are left out and sections in unsupported get a note.
    // Both still load and save their settings.
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
                'Active Downloads',
                'Calendar',
                'Watch Parties',
                'Live rooms',
                'PINs'
            ],
            hiddenFields: ['DefaultWatchPartyService', 'DefaultShowFriendsButton', 'DefaultWatchPartyChatOverlay', 'DefaultWatchPartyAlerts', 'DefaultShowLiveTvButton', 'DefaultLiveTvChannelSortBy', 'DefaultEpgMobileView', 'DefaultSeerrOffNote',
                'DefaultShowCalendarButton', 'DefaultCalendarStyle', 'DefaultCalendarWeekStart', 'DefaultCalendarScope', 'DefaultCalendarShowEpisodes', 'DefaultCalendarShowMovies', 'DefaultCalendarLibraryPicker', 'DefaultCalendarIncludeUnmonitored', 'DefaultCalendarShowSpecials', 'DefaultCalendarShowBadges', 'DefaultCalendarColorBy', 'DefaultCalendarImageType', 'DefaultCalendarSpoilerSafe', 'DefaultCalendarSelectAction', 'DefaultCalendarRequestButton', 'DefaultCalendarReminders', 'DefaultCalendarRowSeeAll'],
            unsupported: {
                'IMDb Charts': 'Not supported on Silo yet. These settings do nothing on Silo for now.',
                'Anime Filler & Recap Markers': 'Not supported on Silo yet. These settings do nothing on Silo for now.',
                'Retro Games (EmulatorJS)': 'Not supported on Silo yet. These settings do nothing on Silo for now.',
                'MDBList Integration': 'Ratings work on Silo. The official MDBList lists aren’t supported on Silo yet.',
                'Uploaded Themes': 'Moonbase on Silo keeps up to 5 themes. Apps pick up a new or changed theme the next time they sign in.',
                'Smart Search': 'Not supported on Silo yet. These settings do nothing on Silo for now.'
            },
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
            hidden: ['Active Downloads'],
            hiddenFields: ['DefaultWatchPartyService', 'DefaultShowFriendsButton', 'EnableSocketRelay'],
            unsupported: {
                'Connecting to this server': 'Moonbase for Plex serves Moonfin Web at /Moonfin/Web/ on its own port, 32500 by default. With Default Server URL empty, the app offers the Plex address Moonbase uses.',
                'Seerr Integration': 'The Seerr proxy isn’t part of Moonbase for Plex. These settings do nothing on Plex.',
                'IMDb Charts': 'Not available on Plex. These settings do nothing on Plex.',
                'Recommendations': 'Not available on Plex. These settings do nothing on Plex.',
                'Anime Filler & Recap Markers': 'Not available on Plex. These settings do nothing on Plex.',
                'Retro Games (EmulatorJS)': 'Not available on Plex. These settings do nothing on Plex.',
                'Uploaded Themes': 'Moonbase for Plex keeps up to 5 themes. Apps pick up a new or changed theme the next time they sign in.',
                'Web Plugin Status': 'Plex Web can’t load Moonfin add-ons, so this doesn’t apply on Plex.',
                'MDBList Integration': 'Ratings work on Plex. The official MDBList lists don’t.',
                'Smart Search': 'Works on Plex for your library. Titles you don’t have don’t show up, since the Seerr proxy isn’t part of Moonbase for Plex. The index rebuilds a minute after Moonbase starts and every night at 3:00.',
                'Calendar': 'Moonbase for Plex reads the calendar straight from Sonarr and Radarr, so set their addresses and API keys below. Reminders aren’t available on Plex.'
            },
            arr: true,
            liveTv: true,
            ownRequests: false,
            serverRows: false
        }
    };
    var host = hosts[hostName] || hosts.Silo;

    // The page is served at <base>/admin and the routes live under <base>/Moonfin.
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
            var draft = draftOffered ? null : readDraft();
            if (draft) {
                notice(host.restored);
            }
            draftOffered = true;
            if (draft) {
                lastConfig = draft;
                fillArrFields(draft);
                fillLogUploadField(draft);
                return Promise.resolve(clone(draft));
            }
            return this.getJSON(getUrl('Moonfin/Admin/Configuration')).then(function (config) {
                lastConfig = config;
                fillArrFields(config);
                fillLogUploadField(config);
                return clone(config);
            }, function (error) {
                // Saving reads the configuration first. Once the launch cookie
                // has run out, carry on from the copy already loaded so the
                // save fails on its own request and keeps the changes.
                var status = error && error.status;
                if (status === 401 && lastConfig) return clone(lastConfig);
                // The page has no failure handler of its own, so its loading
                // overlay would stay up over the settings.
                Dashboard.hideLoadingMsg();
                if (status !== 401) toast(hostName + ' didn’t load the settings' + (status ? ' (status ' + status + ').' : '.'));
                throw error;
            });
        },
        updatePluginConfiguration: function (id, config) {
            readArrFields(config);
            readLogUploadField(config);
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
                listedUsers = users || [];
                fillRemoveUserOptions();
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
        // Artwork needs the server's own token, which the page never holds,
        // so the poster label test draws on a blank poster.
        getImageUrl: function () {
            return '';
        },
        // Only the poster label test can search, for movies and episodes on a
        // host that reads its files. Every other list stays empty.
        getItems: function (userId, query) {
            var term = query && query.SearchTerm;
            if (!term || !/Movie|Episode/.test(query.IncludeItemTypes || '')) {
                return Promise.resolve({ Items: [], TotalRecordCount: 0 });
            }
            return this.getJSON(getUrl('Moonfin/PosterBadges/Search', { term: term }));
        }
    };

    var lastConfig = null;
    var draftOffered = false;

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
        el.setAttribute('role', 'status');
        document.body.appendChild(el);
        // Filled once it's on the page, so screen readers announce it.
        setTimeout(function () {
            el.textContent = text;
            el.classList.add('is-visible');
        }, 10);
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
        el.setAttribute('role', warn ? 'alert' : 'status');
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

    // Silo's own Home rows by section type, since Moonbase can't read a
    // server's section ids. Continue watching and next up are left out because
    // Moonfin builds those itself.
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

    // Moonbase for Plex has no Seerr proxy to reach Sonarr and Radarr through,
    // so the Calendar section gets their addresses and keys. A saved key comes
    // back as a mask, and saving the mask keeps the key.
    var arrInputs = ['SonarrUrl', 'SonarrApiKey', 'RadarrUrl', 'RadarrApiKey'];
    var arrFilled = false;

    function addArrFields(page) {
        var title = Array.prototype.find.call(page.querySelectorAll('.sectionTitle'), function (t) {
            return t.textContent.trim() === 'Calendar';
        });
        if (!title) return;
        var section = title.closest('.verticalSection');
        // The calendar doesn't go through Seerr here, so its settings don't wait for the Seerr switch.
        for (var k = FIELD_DEPENDENCIES.length - 1; k >= 0; k--) {
            if (FIELD_DEPENDENCIES[k].fields.indexOf('CalendarAdminsOnly') >= 0) FIELD_DEPENDENCIES.splice(k, 1);
        }
        var block = document.createElement('div');
        block.innerHTML = '<h4 style="margin: 1.5em 0 0.5em; font-size: 0.95em; color: #00a4dc;">Sonarr and Radarr</h4>'
            + arrInput('SonarrUrl', 'Sonarr URL', 'url', 'http://192.168.1.10:8989')
            + arrInput('SonarrApiKey', 'Sonarr API Key', 'password', 'From Settings > General in Sonarr')
            + arrInput('RadarrUrl', 'Radarr URL', 'url', 'http://192.168.1.10:7878')
            + arrInput('RadarrApiKey', 'Radarr API Key', 'password', 'From Settings > General in Radarr')
            + '<div class="fieldDescription">Moonbase reads each calendar from the address you give, so it has to be reachable from Moonbase, not from the apps. A saved key shows as dots. Type a new one to replace it, or clear the field to remove it.</div>';
        section.appendChild(block);
    }

    function arrInput(id, label, type, placeholder) {
        return '<div class="inputContainer"><label class="inputLabel inputLabelUnfocused" for="' + id + '">' + label + '</label>'
            + '<input type="' + type + '" id="' + id + '" is="emby-input" autocomplete="off" placeholder="' + placeholder + '" /></div>';
    }

    // Saving reads the configuration again, so only the first read fills the
    // fields, or it would undo what the admin typed.
    function fillArrFields(config) {
        if (!host.arr || arrFilled) return;
        arrFilled = true;
        arrInputs.forEach(function (id) {
            var input = document.getElementById(id);
            if (input) input.value = config[id] || '';
        });
    }

    function readArrFields(config) {
        if (!host.arr) return;
        arrInputs.forEach(function (id) {
            var input = document.getElementById(id);
            if (input) config[id] = input.value.trim() || null;
        });
    }

    // Jellyfin keeps app reports itself, so its page has no switch for them.
    // Moonbase takes them here, so the Help tab gets one.
    var logUploadFilled = false;

    function addLogUploadField(page) {
        var help = page.querySelector('.moonfinTabPanel[data-tab="help"]');
        if (!help) return;
        var card = document.createElement('div');
        card.className = 'verticalSection';
        card.innerHTML = '<h3 class="sectionTitle">Diagnostics</h3>'
            + '<div class="checkboxContainer checkboxContainer-withDescription">'
            + '<label class="emby-checkbox-label"><input type="checkbox" id="EnableClientLogUpload" is="emby-checkbox" /><span>Enable client log upload</span></label>'
            + '<div class="fieldDescription"></div></div>';
        card.querySelector('.fieldDescription').textContent = 'Lets Moonfin apps send diagnostic and crash reports to Moonbase, which writes them to its log. This is what makes the app’s Send report to server button work. Reports have server addresses, IP addresses and access tokens stripped out before they leave the device.';
        help.insertBefore(card, help.firstChild);
    }

    function fillLogUploadField(config) {
        var box = document.getElementById('EnableClientLogUpload');
        if (!box || logUploadFilled) return;
        logUploadFilled = true;
        box.checked = config.EnableClientLogUpload !== false;
    }

    function readLogUploadField(config) {
        var box = document.getElementById('EnableClientLogUpload');
        if (box) config.EnableClientLogUpload = box.checked;
    }

    // Both texts describe Jellyfin's SyncPlay, which neither host has.
    var watchPartyNotes = {
        WatchPartiesNote: 'Chat over the video and party alerts are each user’s own settings. Their defaults are under User defaults > Playback > Watch Parties.',
        WatchPartyRoomsNote: 'Rooms live in memory, so a restart of Moonbase ends them.'
    };

    document.addEventListener('DOMContentLoaded', function () {
        var page = document.getElementById('MoonfinConfigPage');
        if (host.arr) addArrFields(page);
        addLogUploadField(page);
        Object.keys(watchPartyNotes).forEach(function (id) {
            var note = document.getElementById(id);
            if (note) note.textContent = watchPartyNotes[id];
        });
        upgrade(document);
        renameServer(page);
        adaptSections(page);
        try {
            adaptHomeRows();
        } catch (e) {
            // The page's globals changed: the builder keeps the Jellyfin rows.
            console.error('Moonbase: adapting the Home rows builder failed', e);
        }
        new MutationObserver(function (records) {
            records.forEach(function (record) { upgrade(record.target); });
        }).observe(page, { childList: true, subtree: true });

        addRemoveUserCard(page);
        page.dispatchEvent(new CustomEvent('pageshow', { bubbles: true }));
    });

    // Neither host tells Moonbase when an account or profile is deleted, so the
    // Actions tab gets a way to remove what Moonbase kept for one.
    var listedUsers = [];

    function addRemoveUserCard(page) {
        var after = page.querySelector('#CopySettingsSection');
        if (!after) return;
        var card = document.createElement('div');
        card.className = 'verticalSection';
        card.innerHTML = '<h3 class="sectionTitle">Remove A User’s Data</h3>'
            + '<div class="fieldDescription" style="margin-bottom:0.8em;"></div>'
            + '<div class="selectContainer"><label class="selectLabel" for="MoonbaseRemoveUser">User</label><select is="emby-select" id="MoonbaseRemoveUser"></select></div>'
            + '<button is="emby-button" type="button" class="raised"><span>Remove Selected User’s Data</span></button>'
            + '<div class="fieldDescription" style="display:none; margin-top:0.8em;"></div>';
        card.querySelector('.fieldDescription').textContent = 'Remove everything Moonbase keeps for an account, on every profile, or for one profile. Use it for an account or profile deleted from ' + hostName + ', since ' + hostName + ' doesn’t tell Moonbase about that. If they sign in again, they start from the defaults.';
        after.after(card);
        var select = card.querySelector('select');
        var button = card.querySelector('button');
        var result = card.lastElementChild;
        function show(text, failed) {
            result.style.display = '';
            result.style.color = failed ? '#d9534f' : '#52b54b';
            result.textContent = text;
        }
        button.addEventListener('click', function () {
            var option = select.selectedOptions[0];
            if (!option || !select.value) {
                show('Pick a user first.', true);
                return;
            }
            var name = option.textContent;
            var target = select.value;
            var oneProfile = target.indexOf('~') !== -1;
            if (!window.confirm('Remove all Moonbase data for ' + name + '?\n\nThis permanently erases their synced settings ' + (oneProfile ? 'on this profile.' : 'on every profile.'))) return;
            button.disabled = true;
            send('DELETE', getUrl('Moonfin/Admin/Users/' + encodeURIComponent(target))).then(json).then(function (body) {
                listedUsers = listedUsers.filter(function (user) {
                    if (!oneProfile) return user.Id !== target;
                    var before = (user.Profiles || []).length;
                    user.Profiles = (user.Profiles || []).filter(function (p) { return p.Id !== target; });
                    // Its last profile gone, the account has nothing left.
                    return !before || user.Profiles.length > 0;
                });
                fillRemoveUserOptions();
                // The page's own user lists offer the same accounts.
                actionsState.users = listedUsers;
                actionsState.targets = actionTargets(listedUsers);
                show('Removed ' + name + ' (' + body.profilesRemoved + ' profile' + (body.profilesRemoved === 1 ? '' : 's') + ').');
            }, function (error) {
                show('Moonbase didn’t remove the data' + (error && error.status ? ' (status ' + error.status + ').' : '.'), true);
            }).finally(function () {
                button.disabled = false;
            });
        });
    }

    // Listed the way the page lists them on Apply and Copy: an account with
    // several profiles once for all of them and once for each.
    function fillRemoveUserOptions() {
        var select = document.getElementById('MoonbaseRemoveUser');
        if (!select) return;
        var current = select.value;
        select.innerHTML = '<option value="">Pick a user</option>';
        actionTargets(listedUsers).forEach(function (target) {
            var option = document.createElement('option');
            option.value = target.Id;
            option.textContent = target.Name;
            select.appendChild(option);
        });
        select.value = current;
    }
})();
