'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const { test } = require('node:test');
const hostSource = fs.readFileSync(__dirname + '/host.js', 'utf8');
const page = fs.readFileSync(__dirname + '/configPage.html', 'utf8');
const embyPage = fs.readFileSync(__dirname + '/../../Emby/Emby.Plugins.Moonfin/Pages/moonfin.js', 'utf8');
const { setImmediate: nextTurn } = require('node:timers/promises');

function deferred() {
    let resolve, reject;
    const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
    return { promise, resolve, reject };
}
function pageSection(start, end) {
    const from = page.indexOf(start), to = page.indexOf(end, from);
    assert.ok(from >= 0 && to > from, 'missing dashboard section ' + start);
    return page.slice(from, to);
}

function browser(host, document, fetch, globals = {}) {
    const context = vm.createContext({
        document, fetch, URLSearchParams, TextEncoder, setInterval() {}, clearInterval() {}, scrollTo() {},
        location: { origin: 'https://moonbase.test', pathname: '/admin' }, ...globals,
    });
    context.window = context;
    if (host !== 'Jellyfin') {
        context.moonbaseHost = { name: host, arr: true };
        vm.runInContext(hostSource, context);
    }
    return context;
}

function picker(host, pins, responses) {
    const calls = [];
    const root = { innerHTML: '', inputs: [], querySelectorAll() { return this.inputs; } };
    const switcher = { dataset: {}, addEventListener(_, listener) { this.click = listener; } };
    const document = { addEventListener() {}, querySelector(selector) {
        if (selector === '#DefaultsProfileSwitch') return switcher;
        if (selector === '#DefaultPinnedGuideGroups') return root;
        return this;
    } };
    const context = browser(host, document, async url => {
        calls.push(url);
        const groups = responses.shift();
        if (groups instanceof Error) return { ok: false, status: 502 };
        return { ok: true, json: async () => ({ availableGuideGroups: groups }) };
    }, {
        defaultsState: { active: 'global', profiles: { global: { liveTvSavedFilters: pins }, tv: {} } },
        collectActiveDefaults() {}, snapshotGlobalLabels() {},
        ApiClient: { getUrl: path => path, ajax: async () => {
            const response = await responses.shift();
            return Array.isArray(response) ? { channels: [{ groups: response }] } : response;
        } },
        esc: value => String(value).replace(/&/g, '&amp;').replace(/"/g, '&quot;'),
    });
    function showPage() {
        return vm.runInContext('(function() {' + pageSection('var guideGroups =', '// Fills the User defaults form') +
            '\nfunction populateDefaults(d) { renderDefaultGuideGroups(d); }\n' +
            pageSection('function switchDefaultsProfile(name)', 'function userName(id)') + '\nreturn discoverDefaultGuideGroups; })()', context);
    }
    let discover = showPage();
    return { calls, root, reopen() { discover = showPage(); }, switchTo(profile) {
        switcher.click({ target: { closest: () => ({ dataset: { profile } }) } });
    }, async reload() {
        discover();
        await nextTurn();
        return root.innerHTML;
    }};
}

test('Jellyfin retries incomplete discovery and ignores a late reply after reopening', async () => {
    const old = deferred();
    const ui = picker('Jellyfin', [], [old.promise, { partial: true, channels: [] },
        { partial: true, channels: [{ groups: ['News'] }] }, { partial: false, channels: [] }, ['Sports']]);
    await ui.reload();
    ui.reopen();
    const partial = await ui.reload();
    assert.match(partial, /Reload to retry/);
    assert.doesNotMatch(partial, /No channel groups available/);
    const news = await ui.reload();
    assert.match(news, /group:News/);
    assert.match(news, /Reload to retry/);
    const empty = await ui.reload();
    assert.match(empty, /No channel groups available/);
    assert.doesNotMatch(empty, /Reload to retry/);
    await ui.reload();
    ui.switchTo('tv');
    ui.switchTo('global');
    assert.equal(checked(ui.root.innerHTML, 'group:Sports'), false);
    old.resolve(['Old']);
    await nextTurn();
    assert.equal(checked(ui.root.innerHTML, 'group:Sports'), false);
    assert.doesNotMatch(ui.root.innerHTML, /group:Old/);
});

test('Emby preserves saved groups during discovery and ignores replies from earlier visits', async () => {
    let reply;
    const context = vm.createContext({
        guideGroupsVisit: 0, availableDefaultGuideGroups: [], defaultGuideGroupsMessage: '', defaultGuideCategories: [],
        defaultsState: { active: 'global', profiles: { global: { liveTvSavedFilters: ['group:News'] } } },
        ApiClient: { getUrl: path => path, ajax: () => reply }, esc: s => s,
    });
    vm.runInContext(['renderDefaultGuideGroups', 'discoverDefaultGuideGroups']
        .map(name => pageFunction(embyPage, name)).join('\n'), context);
    function visit(response) {
        reply = response;
        const root = { innerHTML: '', querySelectorAll: () => [] };
        context.discoverDefaultGuideGroups({ querySelector: () => root }, ++context.guideGroupsVisit);
        return root;
    }
    const old = deferred(), offline = deferred();
    const abandoned = [visit(old.promise), visit(offline.promise)];
    const failed = visit(Promise.reject(Error('offline')));
    await nextTurn();
    assert.equal(checked(failed.innerHTML, 'group:News'), true);
    assert.match(failed.innerHTML, /Reload to retry/);
    const root = visit(Promise.resolve({ Items: [{ Tags: ['Sports'] }] }));
    await nextTurn();
    const latest = root.innerHTML;
    old.resolve({ Items: [{ Tags: ['Old'] }] });
    offline.reject(Error('late'));
    await nextTurn();
    assert.deepEqual(abandoned.map(view => view.innerHTML), ['', '']);
    assert.equal(root.innerHTML, latest);
    assert.equal(checked(latest, 'group:Sports'), false);
    assert.equal(checked(latest, 'group:News'), true);
    assert.doesNotMatch(latest, /retry|group:Old/i);
});

function checked(html, key) {
    const tag = (html.match(/<input[^>]*>/g) || []).find(s => s.includes('data-guide-filter="' + key + '"'));
    assert.ok(tag, 'missing ' + key);
    return tag.includes(' checked');
}

test('Plex guide picker uses provider groups and keeps edits; failure and Silo remain safe', async () => {
    const atLimit = 'group:' + 'é'.repeat(509);
    const tooLong = atLimit + 'x';
    const plex = picker('Plex', ['category:premiere', 'group:Legacy', atLimit, tooLong],
        [['Sports', 'Sports', 'News'], ['News']]);
    await plex.reload();
    assert.deepEqual(plex.calls, ['https://moonbase.test/Moonfin/Defaults?includeLiveTvGroups=true']);
    assert.equal(checked(plex.root.innerHTML, 'category:premiere'), true);
    assert.equal(checked(plex.root.innerHTML, 'group:Legacy'), true);
    assert.equal(checked(plex.root.innerHTML, atLimit), true);
    assert.ok(plex.root.innerHTML.includes('data-guide-filter="' + tooLong + '" disabled'));
    assert.equal((plex.root.innerHTML.match(/data-guide-filter="group:Sports"/g) || []).length, 1);
    plex.root.inputs = [
        { checked: false, getAttribute: () => 'category:premiere' },
        { checked: true, getAttribute: () => 'group:News' },
    ];
    await plex.reload();
    assert.equal(checked(plex.root.innerHTML, 'category:premiere'), false);
    assert.equal(checked(plex.root.innerHTML, 'group:News'), true);
    const failed = picker('Plex', [], [new Error('unavailable')]);
    await failed.reload();
    assert.match(failed.root.innerHTML, /Could not load groups\. Reload to retry\./);
    const silo = picker('Silo', ['category:movies'], []);
    await silo.reload();
    assert.equal(silo.calls.length, 0);
    assert.equal(checked(silo.root.innerHTML, 'category:movies'), true);
});

function pageFunction(source, name) {
    const match = source.match(new RegExp('^( *)function ' + name + '\\([^\\n]*\\) \\{[\\s\\S]*?^\\1\\}', 'm'));
    assert.ok(match, 'missing page function ' + name);
    return match[0];
}

for (const [host, source] of [['Jellyfin/Go', page], ['Emby', embyPage]]) {
    test(host + ' lets an admin copy hidden guide shortcuts as one global setting', () => {
        const emby = host === 'Emby';
        const key = 'liveTvSavedFilters';
        const hiddenOnly = { global: { [key]: [] }, tv: { [key]: [] }, liveTvSavedFilterExclusions: ['group:News'] };
        const copy = { source: hiddenOnly, off: {}, settings: 'pick', pickedRows: ['DefaultPinnedGuideGroups'] };
        const pinField = { id: 'DefaultPinnedGuideGroups', closest: () => null };
        const form = {
            querySelector: id => id === '#DefaultPinnedGuideGroups' ? pinField : null,
            getElementById: id => id === 'DefaultPinnedGuideGroups' ? pinField : null,
        };
        const context = vm.createContext({
            actionsState: { copy, apply: { off: {} } }, defaultsState: { profiles: hiddenOnly },
            DEFAULTS_PROFILE_NAMES: ['global', 'tv'], DEFAULTS_PROFILE_LABELS: { global: 'Global', tv: 'TV' },
            document: form,
        });
        const functions = ['profileEntries', 'countSetDefaults', 'isSetDefault', 'tickedEntries', 'buildSettingRows',
            'settingFieldId', 'settingFieldLabel', 'settingValueText', 'plural', 'pickedKeys'];
        const fields = source.slice(source.indexOf('var SETTING_FIELD_IDS'), source.indexOf('function settingFieldId'));
        vm.runInContext([fields, ...functions.map(name => pageFunction(source, name))].join('\n'), context);

        const rows = () => {
            const entries = context.tickedEntries('copy');
            return emby ? context.buildSettingRows(form, entries) : context.buildSettingRows(entries);
        };
        assert.equal(context.profileEntries('copy')[0].count, 1);
        assert.equal(context.profileEntries('copy')[1].on, false);
        assert.deepEqual(Array.from(rows()[0].values), ['Global: 0 pinned, 1 hidden']);
        assert.deepEqual(Array.from(emby ? context.pickedKeys(form, 'copy') : context.pickedKeys('copy')), [key]);
        assert.deepEqual(hiddenOnly.global[key], []);
        assert.equal(context.profileEntries('apply')[0].on, false);

        copy.source = { global: { [key]: ['group:Sports'], use24HourClock: false }, liveTvSavedFilterExclusions: ['group:News'] };
        assert.equal(context.profileEntries('copy')[0].count, 2);
        assert.deepEqual(Array.from(rows().find(row => row.keys.includes(key)).values), ['Global: 1 pinned, 1 hidden']);

        copy.source = { global: { [key]: [] }, liveTvSavedFilterExclusions: [] };
        assert.equal(rows().length, 0);
    });
}

function settingsPage(emby, defaultsState, api, form, globals = {}, loadDraft = '') {
    const source = emby ? embyPage : page;
    const submit = emby ? 'saveConfig' : 'submit';
    const code = [
        loadDraft, pageFunction(source, 'withGuidePinEdits'), emby ? pageFunction(source, 'camelKeysDeep') : '',
        pageFunction(source, submit), submit,
    ].join('\n');
    const context = {
        defaultsState, ApiClient: api, document: form, window: {}, DOMException, DEFAULTS_PROFILE_NAMES: ['global'],
        loading: { show() {} }, Dashboard: { processPluginConfigurationUpdateResult() {} }, PluginUniqueId: '', MoonfinConfig: {},
        collectActiveDefaults() {}, getPosterBadgeDefaults() {}, getPosterBadgeRules() {}, wholeNumberIn() {},
        reportConfigError() {}, renderDefaultGuideGroups() {},
    };
    Object.defineProperties(context, Object.getOwnPropertyDescriptors(globals));
    const save = vm.runInNewContext(code, context);
    return () => save(form);
}

async function openMoonbaseSettings(api) {
    const config = await api.getPluginConfiguration();
    const guideGroups = {}, defaultsState = { active: 'global', guideGroups, profiles: { global: config.DefaultUserSettings } };
    const loadDraft = pageSection('defaultsState.guidePinBase = config.', "defaultsState.active = 'global';");
    const save = settingsPage(false, defaultsState, api, { querySelector: () => ({ value: '' }), querySelectorAll: () => [] },
        { config, guideGroups, window: { moonbaseHost: { name: 'Plex' } } }, loadDraft);
    return { save, profile: defaultsState.profiles.global };
}

for (const direct of [false, true]) {
    test('Go restores a signed-out ' + (direct ? 'configuration' : 'dashboard') + ' save and queues its retry', async () => {
        const sonarr = { value: '' }, gate = deferred(), writes = [];
        let pins = ['group:News'], signedIn = false;
        const storage = new Map();
        const sessionStorage = { getItem: k => storage.get(k), setItem: (k, v) => storage.set(k, v), removeItem: k => storage.delete(k) };
        const document = { addEventListener() {}, querySelector() { return null; }, getElementById: id => id === 'SonarrUrl' ? sonarr : null };
        async function fetch(_, init) {
            if (init.method !== 'POST') return { ok: true, json: async () => ({ DefaultUserSettings: { liveTvSavedFilters: [...pins] } }) };
            if (!signedIn) return { ok: false, status: 401 };
            const config = JSON.parse(init.body);
            pins = config.DefaultUserSettings.liveTvSavedFilters;
            writes.push(config.SonarrUrl);
            if (writes.length === 1) await gate.promise;
            return { ok: true };
        }
        function open() {
            const tab = browser('Plex', document, fetch, { sessionStorage });
            tab.Dashboard.hideLoadingMsg = () => {};
            return tab.ApiClient;
        }
        const api = open();
        if (direct) {
            const draft = await api.getPluginConfiguration();
            draft.DefaultUserSettings.liveTvSavedFilters = ['category:movies'];
            await assert.rejects(api.updatePluginConfiguration('', draft), /401/);
            const restored = await open().getPluginConfiguration();
            assert.deepEqual(Array.from(restored.moonbaseDraftGuidePinBase), ['group:News']);
            assert.deepEqual(Array.from(restored.DefaultUserSettings.liveTvSavedFilters), ['category:movies']);
        } else {
            const editor = await openMoonbaseSettings(api);
            editor.profile.liveTvSavedFilters = ['category:movies'];
            pins = ['category:movies', 'group:Sports'];
            await assert.rejects(editor.save(), /401/);
        }
        pins = ['group:News', 'group:Music'];
        signedIn = true;
        const restored = await openMoonbaseSettings(open());
        assert.deepEqual(Array.from(restored.profile.liveTvSavedFilters), direct ? ['category:movies'] : ['category:movies', 'group:Sports']);
        const first = restored.save();
        await nextTurn();
        sonarr.value = 'https://submitted.example';
        const second = restored.save();
        await nextTurn();
        sonarr.value = 'https://unsaved.example';
        gate.resolve();
        await Promise.all([first, second]);
        assert.deepEqual(writes, [null, 'https://submitted.example']);
        assert.deepEqual(pins, ['group:Music', 'category:movies']);
    });
}

for (const host of ['Jellyfin/Go', 'Emby']) {
    test(host + ' keeps submitted edits separate from later changes in either tab', async () => {
        const emby = host === 'Emby';
        const state = { active: 'global', profiles: {}, guideGroups: {}, guidePinBase: ['group:News'] };
        let pins = ['group:Sports'], displayed, writes = [], ack, failRead = false, rejectWrite = false, visit = 0;
        let libraryIds, romm = '', address = '', reads = 0, failOnRead;
        const edit = (...values) => { state.profiles.global = { liveTvSavedFilters: values }; };
        const leave = () => { visit++; state.guideGroups = {}; };
        const form = {
            querySelector(selector) {
                if (selector === '.gameLibraryCb' && !libraryIds) return null;
                return { value: selector === '#GamesRommLibraryId' ? romm : selector === '#PublicServerUrl' ? address : '' };
            },
            querySelectorAll: selector => selector === '.gameLibraryCb:checked'
                ? (libraryIds || []).map(id => ({ getAttribute: () => id })) : [],
        };
        const api = {
            async getPluginConfiguration() {
                const read = ++reads;
                if (failRead || read === failOnRead) throw Error('read failed');
                const latest = !emby && read === 1 ? ['group:Old'] : pins;
                return { GameLibraryIds: ['arcade'], DefaultUserSettings: { [emby ? 'LiveTvSavedFilters' : 'liveTvSavedFilters']: latest } };
            },
            async updatePluginConfiguration(_, config) {
                if (rejectWrite) throw Error('write rejected');
                writes.push(config);
                pins = config.DefaultUserSettings.liveTvSavedFilters;
                if (ack) await ack.promise;
            },
        };
        const save = settingsPage(emby, state, api, form, {
            get guideGroupsVisit() { return visit; },
            renderDefaultGuideGroups(...args) { displayed = args.at(-1).liveTvSavedFilters; },
        });
        const expectPins = (...values) => assert.deepEqual(Array.from(pins), values);
        async function delaySave() {
            ack = deferred();
            const saving = save();
            await nextTurn();
            const gate = ack;
            ack = null;
            return { gate, saving };
        }

        edit('group:News'); // This tab has no edits. A newer read must win.
        await save();
        expectPins('group:Sports');
        assert.deepEqual(Array.from(writes.at(-1).GameLibraryIds), ['arcade']);
        if (!emby) failOnRead = reads + 2; // The host's refresh fails after the initial configuration loads.
        const loading = save();
        libraryIds = ['arcade'];
        romm = 'retro';
        await loading;
        assert.deepEqual(Array.from(writes.at(-1).GameLibraryIds), ['arcade']);
        assert.equal(writes.at(-1).GamesRommLibraryId, 'retro');
        libraryIds = [];
        romm = '';
        pins = null;
        await save();
        assert.equal(pins, null);
        assert.deepEqual(Array.from(writes.at(-1).GameLibraryIds), []);
        assert.equal(writes.at(-1).GamesRommLibraryId, null);

        for (const failure of ['none', 'read', 'ack']) {
            state.guidePinBase = pins = ['group:News'];
            edit('group:News');
            if (failure === 'read') pins = ['group:Movies'];
            const { gate, saving } = await delaySave();
            const first = failure === 'ack' ? assert.rejects(saving, /reply lost/) : saving;
            edit('group:Movies');
            address = 'https://submitted.example';
            const queued = save();
            await nextTurn();
            address = 'https://unsaved.example';
            pins = ['group:News', 'group:Music']; // The other tab restored News and added Music.
            edit('group:Movies', 'group:Kids');
            failRead = failure === 'read';
            if (failure === 'ack') gate.reject(Error('reply lost')); else gate.resolve();
            if (failRead) {
                await assert.rejects(queued, /read failed/);
                failRead = false;
                edit('group:Movies');
                await save();
            } else {
                await queued;
                assert.equal(writes.at(-1).PublicServerUrl, 'https://submitted.example');
                assert.deepEqual(Array.from(displayed), ['group:Music', 'group:Movies', 'group:Kids']);
            }
            await first;
            expectPins('group:Music', 'group:Movies');
        }

        edit('group:News');
        rejectWrite = true;
        await assert.rejects(save(), /write rejected/);
        expectPins('group:Music', 'group:Movies');
        rejectWrite = false;
        const lost = await delaySave();
        lost.gate.reject(Error('reply lost'));
        await assert.rejects(lost.saving, /reply lost/);
        expectPins('group:News');
        pins = [...pins, 'group:Weather'];
        edit('group:Movies');
        await save();
        expectPins('group:Weather', 'group:Movies');

        edit('group:News');
        const old = await delaySave();
        expectPins('group:News');
        leave();
        state.guidePinBase = pins = ['group:Sports'];
        edit('group:Sports');
        old.gate.resolve();
        await old.saving;
        edit();
        await save();
        expectPins();
        const count = writes.length;
        const pending = save();
        leave();
        await assert.rejects(pending, /page changed/);
        assert.equal(writes.length, count);
    });
}
