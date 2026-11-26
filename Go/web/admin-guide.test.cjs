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
