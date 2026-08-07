'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const messages = [];
const gamepadEvents = [];
const keyboardEvents = [];
const loadedStates = [];
const changedOptions = [];
let restarted = false;
let fastForward = null;
let mainLoop = null;
let settingsSaved = 0;

function element() {
  return {
    style: {},
    getClientRects: () => [1],
    querySelectorAll: () => [{ style: {} }, { style: {} }],
    querySelector: () => ({ click() {} }),
    dispatchEvent() {},
    scrollIntoView() {},
    click() {},
    getAttribute: () => 'Jump'
  };
}

const row = element();
const tab = element();
tab.classList = { contains: () => true };
const footer = element();
const selector = element();
selector.options = [{}, {}];
selector.selectedIndex = 0;
selector.dispatchEvent = () => {};

const popupContainer = {
  hidden: true,
  getAttribute(name) {
    return name === 'hidden' && this.hidden ? '' : null;
  },
  setAttribute(name) {
    if (name === 'hidden') { this.hidden = true; }
  }
};
const popup = {
  innerText: '',
  getAttribute(name) {
    return name === 'button-num' ? '0' : name === 'player-num' ? '0' : null;
  },
  parentElement: { parentElement: popupContainer }
};

const controlMenu = {
  style: { display: 'none' },
  querySelectorAll(query) {
    if (query === '.ejs_control_bar') { return [row]; }
    if (query === '.ejs_control_player_bar > li') { return [tab]; }
    if (query === ':scope > .ejs_button') { return [footer]; }
    return [];
  },
  querySelector(query) {
    if (query === '.ejs_gamepad_dropdown') { return selector; }
    if (query === '.ejs_control_selected') { return tab; }
    const matches = this.querySelectorAll(query);
    return matches.length ? matches[0] : null;
  }
};

const emulator = {
  gamepad: { gamepads: {} },
  gamepadSelection: [],
  gamepadLabels: [],
  controls: [[{ value2: '' }, { value2: 'BUTTON_1' }]],
  controlPopup: popup,
  controlMenu,
  checkGamepadInputs() {},
  saveSettings() { settingsSaved++; },
  gamepadEvent(event) { gamepadEvents.push(event); },
  keyChange(event) { keyboardEvents.push(event); },
  getSettingValue(id) { return id === 'fps' ? 'show' : null; },
  changeSettingOption(id, value) { changedOptions.push({ id, value }); },
  gameManager: {
    getState: () => new Uint8Array([1, 2, 3]),
    loadState: state => loadedStates.push(Array.from(state)),
    restart: () => { restarted = true; },
    toggleFastForward: value => { fastForward = value; },
    toggleMainLoop: value => { mainLoop = value; },
    getCoreOptions: () => 'video_filter; nearest|linear'
  }
};

const windowObject = {
  EJS_pathtodata: './data/',
  EJS_emulator: emulator,
  parent: { postMessage: message => messages.push(message) },
  setTimeout: callback => callback()
};

const context = {
  window: windowObject,
  navigator: { getGamepads: () => [] },
  requestAnimationFrame() {},
  localStorage: { getItem: key => key === 'ejs-settings' ? '{"saved":true}' : null },
  document: {
    createElement() {
      return { value: '', textContent: '' };
    }
  },
  MouseEvent: function MouseEvent(type, init) { this.type = type; this.init = init; },
  Event: function Event(type, init) { this.type = type; this.init = init; },
  Uint8Array,
  Number,
  JSON,
  Array,
  String,
  Math,
  btoa: value => Buffer.from(value, 'binary').toString('base64'),
  atob: value => Buffer.from(value, 'base64').toString('binary')
};

const bridgePath = path.resolve(__dirname, '../../backend/EmulatorJS/moonfin-bridge.js');
vm.runInNewContext(fs.readFileSync(bridgePath, 'utf8'), context, { filename: bridgePath });

const entryPoints = [
  'moonfinRegisterGamepads',
  'moonfinGamepadInput',
  'moonfinKeyboardInput',
  'moonfinControlInput',
  'moonfinGetState',
  'moonfinLoadState',
  'moonfinRestart',
  'moonfinFastForward',
  'moonfinPause',
  'moonfinGetSettings',
  'moonfinGetOptions',
  'moonfinSetOption',
  'moonfinOpenControls',
  'moonfinControlsOpen'
];
entryPoints.forEach(name => assert.equal(typeof windowObject[name], 'function', name));
assert.equal(windowObject.moonfinControlsApiVersion, 1);

windowObject.EJS_ready();
assert.equal(messages.some(message => message.type === 'moonfin-ready'), true);

assert.equal(windowObject.moonfinRegisterGamepads([{ id: 'pad-1', name: 'Pad' }]), true);
windowObject.moonfinGamepadInput('BUTTON_1', true, { id: 'pad-1', name: 'Pad' });
assert.equal(gamepadEvents.at(-1).type, 'buttondown');
assert.equal(gamepadEvents.at(-1).gamepadIndex, 'moonfin-pad-1');

windowObject.moonfinKeyboardInput(65);
assert.equal(JSON.stringify(keyboardEvents.at(-1)), '{"keyCode":65,"repeat":false,"type":"keydown"}');

assert.equal(windowObject.moonfinGetState(), 'AQID');
windowObject.moonfinLoadState('BAUG');
assert.deepEqual(loadedStates.at(-1), [4, 5, 6]);
windowObject.moonfinRestart();
windowObject.moonfinFastForward(true);
windowObject.moonfinPause(true);
assert.equal(restarted, true);
assert.equal(fastForward, 1);
assert.equal(mainLoop, 0);

assert.equal(windowObject.moonfinGetSettings(), '{"saved":true}');
const options = JSON.parse(windowObject.moonfinGetOptions());
assert.equal(options.some(option => option.id === 'video_filter'), true);
assert.equal(options.some(option => option.id === 'fps'), true);
windowObject.moonfinSetOption('fps', 'hide');
assert.deepEqual(changedOptions.at(-1), { id: 'fps', value: 'hide' });

assert.equal(windowObject.moonfinOpenControls(), true);
assert.equal(windowObject.moonfinControlsOpen(), true);
windowObject.moonfinControlInput('BACK', true, null);
assert.equal(windowObject.moonfinControlsOpen(), false);
assert.equal(messages.some(message => message.type === 'moonfin-controls-closed'), true);
assert.equal(settingsSaved, 0);

// Escape must be claimed by the host, not acted on by EmulatorJS. Left to
// EmulatorJS it ends the session behind the host's back, so the save-on-exit
// never runs. The handler must both report the request and stop the key.
let escapePrevented = 0;
let escapeStopped = 0;
const escapeEvent = {
  key: 'Escape',
  preventDefault: () => { escapePrevented += 1; },
  stopPropagation: () => { escapeStopped += 1; },
  stopImmediatePropagation: () => { escapeStopped += 1; }
};
assert.equal(typeof windowObject.moonfinEscapeHandler, 'function');
windowObject.moonfinEscapeHandler(escapeEvent);
assert.equal(messages.some(message => message.type === 'moonfin-menu-request'), true);
assert.equal(escapePrevented, 1);
assert.ok(escapeStopped >= 1);

// Every other key still belongs to the game.
const beforeOtherKey = messages.length;
windowObject.moonfinEscapeHandler({ key: 'a', keyCode: 65 });
assert.equal(messages.length, beforeOtherKey);

console.log('moonfin-bridge tests passed');
