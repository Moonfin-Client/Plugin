// Moonfin's adapter for EmulatorJS. EmulatorJS remains the mapping and persistence authority.
(function () {
  'use strict';

  // Match the original inline script: invalid launch URLs stop before any bridge globals exist.
  if (!window.EJS_pathtodata) {
    return;
  }

  var GAMEPAD_REGISTRATION_MAX_ATTEMPTS = 20;
  var GAMEPAD_REGISTRATION_RETRY_DELAY_MS = 100;
  var FOCUS_AREA_ROW = 'row';
  var FOCUS_AREA_TAB = 'tab';
  var FOCUS_AREA_GAMEPAD = 'gamepad';
  var FOCUS_AREA_FOOTER = 'footer';
  var postToHost;
  var applyRegisteredGamepads;
  var installMoonfinGamepad;
  // Exposed below as window.moonfinEscapeHandler so the behaviour is testable
  // without a DOM event system.
  var moonfinEscapeHandler;

  // Host transport and the version-sensitive EmulatorJS contract.
  (function () {
    var moonfinContractChecked = false;

    postToHost = function (message) {
      try {
        if (window.parent && window.parent !== window) {
          // Wildcard target origin is acceptable only because every message posted here today
          // (moonfin-ready, moonfin-controls-closed, moonfin-emulator-contract-violation,
          // gamepad button state) is non-sensitive telemetry/control, and there is no matching
          // window.addEventListener('message', ...) anywhere in this file, so there is no
          // receive-side gap either. If a future message ever carries something sensitive (e.g.
          // a save-state payload), it must not inherit this wildcard silently - restrict the
          // target origin at that point.
          window.parent.postMessage(message, '*');
        }
      } catch (e) { /* ignore */ }

      // flutter_inappwebview handler (registered by the Flutter host).
      try {
        if (window.flutter_inappwebview) {
          window.flutter_inappwebview.callHandler('moonfinPlayer', message);
        }
      } catch (e) { /* ignore */ }
    };

    // Escape belongs to the host, not to EmulatorJS.
    //
    // While a game runs the WebView owns focus, so a keyboard Escape is
    // delivered to this page rather than to the Flutter host. EmulatorJS acts
    // on it itself and tears the session down, which the host never learns
    // about -- so its save-on-exit never runs and the player loses everything
    // since the last save. Capture phase, on window, so this sees the key
    // before any EmulatorJS handler and can stop it propagating; the host is
    // asked for its menu instead, matching what Back does on every other input
    // device. Nothing else is intercepted: game keys must still reach the core.
    moonfinEscapeHandler = function (event) {
      if (event.key !== 'Escape' && event.keyCode !== 27) { return; }
      if (event.preventDefault) { event.preventDefault(); }
      if (event.stopPropagation) { event.stopPropagation(); }
      if (event.stopImmediatePropagation) { event.stopImmediatePropagation(); }
      postToHost({ type: 'moonfin-menu-request' });
    };
    window.moonfinEscapeHandler = moonfinEscapeHandler;
    if (window.addEventListener) {
      window.addEventListener('keydown', moonfinEscapeHandler, true);
    }

    // Guards against a silent EmulatorJS version bump. The gamepad/controller-mapping
    // navigation adapter below (installMoonfinGamepad, moonfinControlInput, and friends)
    // deliberately reaches into EmulatorJS's private instance state instead of maintaining a
    // second mapping model, and every individual access is already wrapped in its own
    // try/catch that fails silently (returns false/undefined) rather than throwing -- which
    // means an upstream rename degrades to an unresponsive controller-config screen with no
    // diagnostic. This runs once, at ready-time, and reports (but never throws for) each
    // internal the adapter depends on that is missing, so a version bump is loud instead of
    // silent. See CoresService.ExpectedEmulatorJsVersion (server-side) for the EmulatorJS
    // release this was last verified against. The Flutter host now listens for
    // 'moonfin-emulator-contract-violation' (game_emulator_screen.dart) and logs it for
    // diagnosis; it deliberately has no other user-visible effect.
    function moonfinAssertEmulatorContract(emu) {
      if (moonfinContractChecked) { return; }
      moonfinContractChecked = true;
      try {
        var missing = [];
        function check(name, value) {
          if (typeof value === 'undefined' || value === null) {
            missing.push(name);
          }
        }
        check('emu', emu);
        if (!emu) {
          missing.forEach(function (name) {
            postToHost({ type: 'moonfin-emulator-contract-violation', missing: name });
          });
          return;
        }
        check('emu.gamepad', emu.gamepad);
        check('emu.gamepad.gamepads', emu.gamepad && emu.gamepad.gamepads);
        check('emu.gamepadSelection', emu.gamepadSelection);
        check('emu.gamepadLabels', emu.gamepadLabels);
        check('emu.controls', emu.controls);
        check('emu.controlPopup', emu.controlPopup);
        check('emu.controlMenu', emu.controlMenu);
        check('emu.checkGamepadInputs', emu.checkGamepadInputs);
        check('emu.saveSettings', emu.saveSettings);
        check('emu.gamepadEvent', emu.gamepadEvent);
        check('emu.keyChange', emu.keyChange);
        check('emu.gameManager', emu.gameManager);
        check('emu.getSettingValue', emu.getSettingValue);
        check('emu.changeSettingOption', emu.changeSettingOption);
        // DOM selectors the navigation adapter queries for (visibleRows, footerButtons, tabs,
        // highlight, changeGamepad). EmulatorJS builds this markup once at init (controlMenu
        // merely toggles display:none later), so these should already be present at ready-time
        // even though the menu has never been opened.
        if (emu.controlMenu) {
          check('.ejs_control_bar', emu.controlMenu.querySelector('.ejs_control_bar'));
          check('.ejs_control_player_bar > li', emu.controlMenu.querySelector('.ejs_control_player_bar > li'));
          check(':scope > .ejs_button', emu.controlMenu.querySelector(':scope > .ejs_button'));
          check('.ejs_gamepad_dropdown', emu.controlMenu.querySelector('.ejs_gamepad_dropdown'));
          check('.ejs_control_selected', emu.controlMenu.querySelector('.ejs_control_selected'));
        } else {
          missing.push('.ejs_control_bar', '.ejs_control_player_bar > li', ':scope > .ejs_button', '.ejs_gamepad_dropdown', '.ejs_control_selected');
        }
        missing.forEach(function (name) {
          postToHost({ type: 'moonfin-emulator-contract-violation', missing: name });
        });
      } catch (e) { /* Contract reporting must never break emulator start. */ }
    }

    window.EJS_ready = function () {
      moonfinAssertEmulatorContract(window.EJS_emulator);
      applyRegisteredGamepads();
      postToHost({ type: 'moonfin-ready' });
    };
  })();

  // Android gamepad/keyboard registration and forwarding.
  //
  // Android System WebView does not provide navigator.getGamepads(). Represent its native
  // controller as one EmulatorJS gamepad instead of keeping a second Moonfin mapping table.
  // EmulatorJS then remains responsible for both mapping and saved settings.
  (function () {
    var registeredGamepads = [];
    var registrationAttempts = 0;

    installMoonfinGamepad = function (emu, device, activatePlayer) {
      try {
        if (!device || !device.id || !emu || !emu.gamepadSelection || !emu.gamepad || !emu.gamepad.gamepads) {
          return false;
        }
        var id = 'moonfin-' + device.id;
        var selectionKey = id + '_' + id;
        emu.gamepad.gamepads[id] = {
          id: id,
          index: id
        };
        if (activatePlayer && !emu.moonfinActiveGamepad) {
          emu.gamepadSelection[0] = selectionKey;
          emu.moonfinActiveGamepad = selectionKey;
        }
        if (emu.gamepadLabels) {
          emu.gamepadLabels.forEach(function (select, player) {
            var option = select.querySelector('option[value="' + selectionKey + '"]');
            if (!option) {
              option = document.createElement('option');
              option.value = selectionKey;
              option.textContent = device.name || 'Android gamepad';
              select.appendChild(option);
            }
            select.value = emu.gamepadSelection[player] || 'notconnected';
          });
        }
        return true;
      } catch (e) { return false; }
    };

    applyRegisteredGamepads = function () {
      try {
        var emu = window.EJS_emulator;
        if (!emu || !emu.gamepadSelection || !emu.gamepad || !emu.gamepad.gamepads) {
          if (registrationAttempts++ < GAMEPAD_REGISTRATION_MAX_ATTEMPTS) {
            window.setTimeout(applyRegisteredGamepads, GAMEPAD_REGISTRATION_RETRY_DELAY_MS);
          }
          return false;
        }
        registeredGamepads.forEach(function (device) {
          installMoonfinGamepad(emu, device, registeredGamepads.length === 1);
        });
        return true;
      } catch (e) {
        if (registrationAttempts++ < GAMEPAD_REGISTRATION_MAX_ATTEMPTS) {
          window.setTimeout(applyRegisteredGamepads, GAMEPAD_REGISTRATION_RETRY_DELAY_MS);
        }
        return false;
      }
    };

    window.moonfinRegisterGamepads = function (devices) {
      registeredGamepads = Array.isArray(devices) ? devices : [];
      registrationAttempts = 0;
      return applyRegisteredGamepads();
    };
    window.moonfinGamepadInput = function (label, pressed, device) {
      try {
        var emu = window.EJS_emulator;
        // TV remotes are navigation-only and therefore never registered as players.
        if (!installMoonfinGamepad(emu, device, pressed)) {
          return;
        }
        var id = 'moonfin-' + device.id;
        emu.gamepadEvent({
          type: pressed ? 'buttondown' : 'buttonup',
          gamepadIndex: id,
          label: label,
          index: label,
          value: pressed ? 1 : 0,
          oldValue: pressed ? 0 : 1
        });
      } catch (e) { /* ignore */ }
    };
    window.moonfinKeyboardInput = function (keyCode) {
      try {
        var emu = window.EJS_emulator;
        if (emu && emu.keyChange) {
          emu.keyChange({ keyCode: keyCode, repeat: false, type: 'keydown' });
        }
      } catch (e) { /* ignore */ }
    };
  })();

  // Controls-menu navigation, binding capture, persistence, and host close notifications.
  (function () {
    function visibleRows(emu) {
      return Array.prototype.slice.call(emu.controlMenu.querySelectorAll('.ejs_control_bar'))
        .filter(function (row) { return row.getClientRects().length > 0; });
    }

    function footerButtons(emu) {
      return Array.prototype.slice.call(emu.controlMenu.querySelectorAll(':scope > .ejs_button'));
    }

    function tabs(emu) {
      return Array.prototype.slice.call(emu.controlMenu.querySelectorAll('.ejs_control_player_bar > li'));
    }
    function focus(emu) {
      if (!emu.moonfinControlFocus) {
        emu.moonfinControlFocus = { area: FOCUS_AREA_ROW, index: 0, column: 0 };
      }
      return emu.moonfinControlFocus;
    }
    function selectPlayer(emu, index) {
      var playerTabs = tabs(emu);
      if (!playerTabs.length) {
        return;
      }
      index = (index + playerTabs.length) % playerTabs.length;
      var link = playerTabs[index].querySelector('a');
      // EmulatorJS attaches player selection to click (control rows use mousedown).
      if (link) {
        link.click();
      }
      var current = focus(emu);
      current.index = index;
      current.area = FOCUS_AREA_TAB;
    }
    function highlight(emu) {
      var current = focus(emu);
      var rows = visibleRows(emu);
      var playerTabs = tabs(emu);
      var footer = footerButtons(emu);
      if (current.area === FOCUS_AREA_ROW) {
        current.index = Math.max(0, Math.min(rows.length - 1, current.index));
      } else if (current.area === FOCUS_AREA_TAB) {
        current.index = Math.max(0, Math.min(playerTabs.length - 1, current.index));
      } else if (current.area === FOCUS_AREA_FOOTER) {
        current.index = Math.max(0, Math.min(footer.length - 1, current.index));
      }
      rows.forEach(function (row, index) {
        row.style.outline = current.area === FOCUS_AREA_ROW && index === current.index ? '3px solid #3f8cff' : '';
        row.style.outlineOffset = '3px';
        Array.prototype.slice.call(row.querySelectorAll('input')).forEach(function (input, column) {
          input.style.outline = current.area === FOCUS_AREA_ROW && index === current.index && column === current.column ? '2px solid #3f8cff' : '';
        });
      });
      playerTabs.forEach(function (tab, index) {
        tab.style.outline = current.area === FOCUS_AREA_TAB && index === current.index ? '3px solid #3f8cff' : '';
      });
      var selector = emu.controlMenu.querySelector('.ejs_gamepad_dropdown');
      if (selector) {
        selector.style.outline = current.area === FOCUS_AREA_GAMEPAD ? '3px solid #3f8cff' : '';
      }
      footer.forEach(function (button, index) {
        button.style.outline = current.area === FOCUS_AREA_FOOTER && index === current.index ? '3px solid #3f8cff' : '';
      });
      var target = current.area === FOCUS_AREA_ROW ? rows[current.index] :
        current.area === FOCUS_AREA_TAB ? playerTabs[current.index] :
        current.area === FOCUS_AREA_GAMEPAD ? selector : footer[current.index];
      if (target) {
        target.scrollIntoView({ block: 'nearest', inline: 'nearest' });
      }
    }
    function openControlRow(emu) {
      var current = focus(emu);
      var row = visibleRows(emu)[current.index];
      if (!row) {
        return;
      }
      // The upstream mousedown handler opens its keyboard-capture popup.
      row.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));
      if (emu.controlPopup) {
        emu.controlPopup.innerText = '[ ' + row.getAttribute('data-label') + ' ]\n' +
          (current.column === 0 ? 'Press Gamepad' : 'Press a physical keyboard key');
      }
    }
    function clearDuplicateGamepadBinding(emu, player, button, label) {
      var controls = emu.controls && emu.controls[player];
      if (!controls) {
        return;
      }
      controls.forEach(function (binding, index) {
        if (index !== button && binding && binding.value2 === label) {
          binding.value2 = '';
        }
      });
    }
    function clearCurrentControl(emu) {
      var popup = emu.controlPopup;
      if (!popup) { return; }
      var button = Number(popup.getAttribute('button-num'));
      var player = Number(popup.getAttribute('player-num'));
      if (!Number.isFinite(button) || !Number.isFinite(player)) {
        return;
      }
      if (!emu.controls[player]) {
        emu.controls[player] = [];
      }
      if (!emu.controls[player][button]) {
        emu.controls[player][button] = {};
      }
      emu.controls[player][button].value2 = '';
      popup.parentElement.parentElement.setAttribute('hidden', '');
      emu.checkGamepadInputs();
      emu.saveSettings();
    }
    function changeGamepad(emu, delta) {
      var selector = emu.controlMenu.querySelector('.ejs_gamepad_dropdown');
      if (!selector || !selector.options.length) {
        return;
      }
      selector.selectedIndex = (selector.selectedIndex + delta + selector.options.length) % selector.options.length;
      selector.dispatchEvent(new Event('change', { bubbles: true }));
    }

    window.moonfinControlInput = function (label, pressed, device) {
      try {
        var emu = window.EJS_emulator;
        if (!emu || !emu.controlMenu || emu.controlMenu.style.display === 'none') {
          return;
        }
        if (!pressed) {
          return;
        }
        var popup = emu.controlPopup && emu.controlPopup.parentElement.parentElement;
        if (label === 'BACK') {
          if (popup && popup.getAttribute('hidden') === null) {
            popup.setAttribute('hidden', '');
          } else {
            emu.controlMenu.style.display = 'none';
          }
          if (emu.controlMenu.style.display === 'none') {
            postToHost({ type: 'moonfin-controls-closed', reason: 'back' });
          }
          return;
        }
        if (popup && popup.getAttribute('hidden') === null) {
          // A TV remote is not a gamepad player; confirm invokes the normal Clear action.
          if (!device) {
            if (label === 'BUTTON_2') {
              clearCurrentControl(emu);
            }
            return;
          }
          if (!installMoonfinGamepad(emu, device)) {
            return;
          }
          var id = 'moonfin-' + device.id;
          var button = Number(emu.controlPopup.getAttribute('button-num'));
          var player = Number(emu.controlPopup.getAttribute('player-num'));
          emu.gamepadEvent({
            type: 'buttondown',
            gamepadIndex: id,
            label: label,
            index: label,
            value: 1,
            oldValue: 0
          });
          if (Number.isFinite(button) && Number.isFinite(player)) {
            clearDuplicateGamepadBinding(emu, player, button, label);
            emu.checkGamepadInputs();
            emu.saveSettings();
          }
          return;
        }
        var current = focus(emu);
        var rows = visibleRows(emu);
        var playerTabs = tabs(emu);
        var footer = footerButtons(emu);
        if (label === 'DPAD_UP') {
          if (current.area === FOCUS_AREA_ROW) {
            if (current.index > 0) {
              current.index--;
            } else {
              current.area = FOCUS_AREA_GAMEPAD;
            }
          } else if (current.area === FOCUS_AREA_GAMEPAD) {
            current.area = FOCUS_AREA_TAB;
            current.index = playerTabs.findIndex(function (tab) {
              return tab.classList.contains('ejs_control_selected');
            });
            if (current.index < 0) {
              current.index = 0;
            }
          } else if (current.area === FOCUS_AREA_FOOTER) {
            current.area = FOCUS_AREA_ROW;
            current.index = Math.max(0, rows.length - 1);
          }
        } else if (label === 'DPAD_DOWN') {
          if (current.area === FOCUS_AREA_TAB) {
            current.area = FOCUS_AREA_GAMEPAD;
          } else if (current.area === FOCUS_AREA_GAMEPAD) {
            current.area = FOCUS_AREA_ROW;
            current.index = 0;
          } else if (current.area === FOCUS_AREA_ROW) {
            if (current.index < rows.length - 1) {
              current.index++;
            } else {
              current.area = FOCUS_AREA_FOOTER;
              current.index = 0;
            }
          }
        } else if (label === 'DPAD_LEFT') {
          if (current.area === FOCUS_AREA_TAB) {
            selectPlayer(emu, current.index - 1);
          } else if (current.area === FOCUS_AREA_GAMEPAD) {
            changeGamepad(emu, -1);
          } else if (current.area === FOCUS_AREA_ROW) {
            current.column = 0;
          } else if (current.area === FOCUS_AREA_FOOTER && footer.length) {
            current.index = (current.index + footer.length - 1) % footer.length;
          }
        } else if (label === 'DPAD_RIGHT') {
          if (current.area === FOCUS_AREA_TAB) {
            selectPlayer(emu, current.index + 1);
          } else if (current.area === FOCUS_AREA_GAMEPAD) {
            changeGamepad(emu, 1);
          } else if (current.area === FOCUS_AREA_ROW) {
            current.column = 1;
          } else if (current.area === FOCUS_AREA_FOOTER && footer.length) {
            current.index = (current.index + 1) % footer.length;
          }
        } else if (label === 'BUTTON_2') {
          if (current.area === FOCUS_AREA_TAB) {
            selectPlayer(emu, current.index);
          } else if (current.area === FOCUS_AREA_ROW) {
            openControlRow(emu);
          } else if (current.area === FOCUS_AREA_FOOTER && footer[current.index]) {
            footer[current.index].click();
          }
        }
        highlight(emu);
        // Footer actions (notably Close) can synchronously hide the upstream menu.
        if (emu.controlMenu.style.display === 'none') {
          postToHost({ type: 'moonfin-controls-closed', reason: 'close' });
        }
      } catch (e) { /* ignore */ }
    };
    // Versioned capability contract for the Android host. Older player pages do not advertise
    // it, so newer clients leave the menu closed instead of routing controller input into a
    // dialog they cannot drive.
    window.moonfinControlsApiVersion = 1;
    window.moonfinOpenControls = function () {
      try {
        var emu = window.EJS_emulator;
        if (emu && emu.controlMenu) {
          emu.controlMenu.style.display = '';
          emu.moonfinControlFocus = { area: FOCUS_AREA_ROW, index: 0, column: 0 };
          highlight(emu);
          return emu.controlMenu.style.display !== 'none';
        }
      } catch (e) { /* ignore */ }
      return false;
    };
    window.moonfinControlsOpen = function () {
      try {
        var emu = window.EJS_emulator;
        return !!(emu && emu.controlMenu && emu.controlMenu.style.display !== 'none');
      } catch (e) {
        return false;
      }
    };
  })();

  // Save-state and core-option host bridge.
  (function () {
    function uint8ToBase64(bytes) {
      var binary = '';
      for (var i = 0; i < bytes.byteLength; i++) {
        binary += String.fromCharCode(bytes[i]);
      }
      return btoa(binary);
    }

    function base64ToUint8(b64) {
      var binary = atob(b64);
      var bytes = new Uint8Array(binary.length);
      for (var i = 0; i < binary.length; i++) {
        bytes[i] = binary.charCodeAt(i);
      }
      return bytes;
    }

    // Returns the current save state as a base64 string, or null on failure.
    window.moonfinGetState = function () {
      try {
        var emu = window.EJS_emulator;
        if (!emu || !emu.gameManager) {
          return null;
        }
        var state = emu.gameManager.getState();
        return state ? uint8ToBase64(state) : null;
      } catch (e) {
        return null;
      }
    };

    window.moonfinLoadState = function (b64) {
      try {
        var emu = window.EJS_emulator;
        if (emu && emu.gameManager && b64) {
          emu.gameManager.loadState(base64ToUint8(b64));
        }
      } catch (e) { /* ignore */ }
    };

    window.moonfinRestart = function () {
      try {
        var emu = window.EJS_emulator;
        if (emu && emu.gameManager) {
          emu.gameManager.restart();
        }
      } catch (e) { /* ignore */ }
    };

    window.moonfinFastForward = function (on) {
      try {
        var emu = window.EJS_emulator;
        if (emu && emu.gameManager && emu.gameManager.toggleFastForward) {
          emu.gameManager.toggleFastForward(on ? 1 : 0);
        }
      } catch (e) { /* ignore */ }
    };

    window.moonfinPause = function (paused) {
      try {
        var emu = window.EJS_emulator;
        if (emu && emu.gameManager && emu.gameManager.toggleMainLoop) {
          emu.gameManager.toggleMainLoop(paused ? 0 : 1);
        }
      } catch (e) { /* ignore */ }
    };

    // EmulatorJS's stored settings (control remaps + options), for cloud sync on exit.
    window.moonfinGetSettings = function () {
      try {
        return localStorage.getItem('ejs-settings');
      } catch (e) {
        return null;
      }
    };
    window.moonfinGetOptions = function () {
      try {
        var emu = window.EJS_emulator;
        if (!emu || !emu.gameManager || !emu.gameManager.getCoreOptions) {
          return '[]';
        }
        var raw = emu.gameManager.getCoreOptions();
        if (!raw) {
          return '[]';
        }
        var out = [];
        raw.split('\n').forEach(function (line) {
          if (!line) {
            return;
          }
          var parts = line.split('; ');
          if (parts.length < 2) {
            return;
          }
          var id = parts[0].split('|')[0];
          var choices = parts[1].split('|');
          if (choices.length <= 1) {
            return;
          }
          out.push({
            id: id,
            label: id.replace(/_/g, ' ').replace(/.+\-(.+)/, '$1'),
            choices: choices.map(function (choice) {
              return { value: choice, label: choice.replace('(Default) ', '') };
            }),
            current: emu.getSettingValue ? emu.getSettingValue(id) : null
          });
        });
        [
          { id: 'shader', label: 'Shader', choices: ['disabled', '2xScaleHQ', '4xScaleHQ', 'crt-aperture', 'crt-easymode', 'crt-geom', 'crt-mattias', 'sabr', 'bicubic'] },
          { id: 'fps', label: 'FPS counter', choices: ['show', 'hide'] },
          { id: 'vsync', label: 'VSync', choices: ['enabled', 'disabled'] },
          { id: 'ff-ratio', label: 'Fast-forward ratio', choices: ['1.5', '2.0', '2.5', '3.0', '4.0', '5.0', '6.0', '8.0', 'unlimited'] },
          { id: 'sm-ratio', label: 'Slow-motion ratio', choices: ['1.5', '2.0', '2.5', '3.0', '4.0', '5.0'] },
          { id: 'save-state-slot', label: 'Save state slot', choices: ['1', '2', '3', '4', '5', '6', '7', '8', '9'] }
        ].forEach(function (setting) {
          var current = emu.getSettingValue ? emu.getSettingValue(setting.id) : null;
          if (current == null) {
            return;
          }
          out.push({
            id: setting.id,
            label: setting.label,
            choices: setting.choices.map(function (choice) {
              return { value: choice, label: choice };
            }),
            current: current
          });
        });
        return JSON.stringify(out);
      } catch (e) { return '[]'; }
    };
    window.moonfinSetOption = function (id, value) {
      try {
        var emu = window.EJS_emulator;
        if (emu && emu.changeSettingOption) {
          emu.changeSettingOption(id, value);
        }
      } catch (e) { /* ignore */ }
    };
  })();

  // Forward hardware-gamepad buttons to the Flutter host on platforms whose WebView exposes the
  // Gamepad API (iOS / desktop). Self-gating: does nothing on Android, where getGamepads() is
  // empty and the native channel provides input instead.
  (function () {
    var previous = {};
    function poll() {
      try {
        var pads = navigator.getGamepads ? navigator.getGamepads() : [];
        var pad = null;
        for (var i = 0; i < pads.length; i++) {
          if (pads[i]) {
            pad = pads[i];
            break;
          }
        }
        if (pad) {
          for (var button = 0; button < pad.buttons.length; button++) {
            var pressed = !!pad.buttons[button].pressed;
            if (previous[button] !== pressed) {
              previous[button] = pressed;
              postToHost({ type: 'gamepad', index: button, pressed: pressed });
            }
          }
        }
      } catch (e) { /* ignore */ }
      requestAnimationFrame(poll);
    }
    requestAnimationFrame(poll);
  })();
})();
