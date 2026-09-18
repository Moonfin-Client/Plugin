(function () {
  "use strict";

  function currentApiClient() {
    return (
      window.ApiClient ||
      (window.connectionManager && window.connectionManager.currentApiClient &&
        window.connectionManager.currentApiClient()) ||
      null
    );
  }

  function callIfFunction(target, member) {
    if (!target) return null;
    try {
      var value = target[member];
      if (typeof value === "function") {
        return value.call(target);
      }
      return value;
    } catch (e) {
      return null;
    }
  }

  function normalizeServerAddress(value) {
    if (!value || typeof value !== "string") {
      return "";
    }

    try {
      var parsed = new URL(value, window.location.origin);
      var pathname = (parsed.pathname || "").replace(/\/$/, "");
      return parsed.origin + pathname;
    } catch (e) {
      return value.replace(/\/$/, "");
    }
  }

  function readServerAddress(api) {
    var direct = callIfFunction(api, "serverAddress");
    if (typeof direct === "string" && direct) {
      return normalizeServerAddress(direct);
    }

    var info = callIfFunction(api, "serverInfo");
    if (info && typeof info === "object") {
      var fromInfo =
        info.ManualAddress ||
        info.manualAddress ||
        info.Address ||
        info.address ||
        info.Url ||
        info.url;
      if (typeof fromInfo === "string" && fromInfo) {
        return normalizeServerAddress(fromInfo);
      }
    }

    return "";
  }

  function readAccessToken(api) {
    var token = callIfFunction(api, "accessToken");
    if (typeof token === "string" && token) {
      return token;
    }

    var info = callIfFunction(api, "serverInfo");
    if (info && typeof info === "object") {
      var fromInfo =
        info.AccessToken || info.accessToken || info.Token || info.token;
      if (typeof fromInfo === "string" && fromInfo) {
        return fromInfo;
      }
    }

    return "";
  }

  function readUserId(api) {
    var direct =
      callIfFunction(api, "getCurrentUserId") || callIfFunction(api, "userId");
    if (typeof direct === "string" && direct) {
      return direct;
    }

    var currentUser = callIfFunction(api, "currentUser");
    if (currentUser && typeof currentUser === "object") {
      var fromCurrent =
        currentUser.Id || currentUser.id || currentUser.UserId || currentUser.userId;
      if (typeof fromCurrent === "string" && fromCurrent) {
        return fromCurrent;
      }
    }

    var info = callIfFunction(api, "serverInfo");
    if (info && typeof info === "object") {
      var fromInfo =
        info.UserId || info.userId || info.CurrentUserId || info.currentUserId;
      if (typeof fromInfo === "string" && fromInfo) {
        return fromInfo;
      }
    }

    return "";
  }

  function persistBootstrapCredentials() {
    var api = currentApiClient();
    if (!api) return;

    var payload = {
      serverAddress: readServerAddress(api),
      accessToken: readAccessToken(api),
      userId: readUserId(api),
      source: "jellyfin_loader",
      timestamp: Date.now(),
    };

    if (!payload.serverAddress || !payload.accessToken || !payload.userId) {
      return;
    }

    var raw;
    try {
      raw = JSON.stringify(payload);
    } catch (e) {
      return;
    }

    try {
      window.sessionStorage.setItem("moonfin_bootstrap_credentials", raw);
    } catch (e) {}

    try {
      window.localStorage.setItem("moonfin_bootstrap_credentials", raw);
    } catch (e) {}
  }

  function resolveMoonfinBase() {
    try {
      var api = currentApiClient();
      if (api && typeof api.serverAddress === "function") {
        var server = api.serverAddress() || "";
        if (server) {
          var parsed = new URL(server, window.location.origin);
          var prefix = (parsed.pathname || "").replace(/\/$/, "");
          return prefix + "/Moonfin";
        }
      }
    } catch (e) {}

    var path = window.location.pathname || "";
    var webIdx = path.toLowerCase().lastIndexOf("/web/");
    if (webIdx >= 0) {
      return path.substring(0, webIdx) + "/Moonfin";
    }

    return "/Moonfin";
  }

  function isElementVisible(el) {
    if (!el) return false;
    if (el.offsetParent !== null) return true;
    var rect = el.getBoundingClientRect();
    return rect.width > 0 && rect.height > 0;
  }

  function createMoonfinButton() {
    var moonfinBase = resolveMoonfinBase();
    var btn = document.createElement("button");
    btn.type = "button";
    btn.className =
      "headerButton headerButtonRight headerMoonfinButton MuiButtonBase-root MuiIconButton-root MuiIconButton-colorInherit MuiIconButton-sizeLarge";
    btn.title = "Open Moonfin";
    btn.setAttribute("aria-label", "Open Moonfin");
    btn.innerHTML =
      '<img src="' +
      moonfinBase +
      '/Assets/icon.png" style="width:24px;height:24px;border-radius:4px;vertical-align:middle;display:block" alt="Moonfin">';
    btn.addEventListener("click", function (e) {
      e.preventDefault();
      e.stopPropagation();
      persistBootstrapCredentials();
      window.location.href = moonfinBase + "/Web/";
    });
    return btn;
  }

  function injectIntoToolbar(toolbar) {
    if (!toolbar || !isElementVisible(toolbar)) return false;

    var btn = toolbar.querySelector(".headerMoonfinButton");
    var userBtn = toolbar.querySelector(
      'button[aria-controls="app-user-menu"], button[aria-label*="UserMenu"], button[aria-label*="User"], .MuiAvatar-root'
    );

    if (userBtn) {
      var userBox = userBtn.closest(".MuiToolbar-root > *");
      if (userBox && userBox !== toolbar && userBox.parentNode === toolbar) {
        if (userBox.previousElementSibling === btn) {
          return true;
        }
        if (!btn) btn = createMoonfinButton();
        toolbar.insertBefore(btn, userBox);
        return true;
      }
      if (userBtn.parentNode) {
        if (userBtn.previousElementSibling === btn) {
          return true;
        }
        if (!btn) btn = createMoonfinButton();
        userBtn.parentNode.insertBefore(btn, userBtn);
        return true;
      }
    }

    if (btn) return true;
    btn = createMoonfinButton();

    var buttonsContainer = toolbar.querySelector(
      '.MuiBox-root[style*="justify-content: flex-end"], div[style*="flex-grow: 1"]'
    );
    if (buttonsContainer) {
      buttonsContainer.appendChild(btn);
    } else {
      toolbar.appendChild(btn);
    }
    return true;
  }

  function injectIntoLegacyHeader(headerRight) {
    if (!headerRight || !isElementVisible(headerRight)) return false;

    var btn = headerRight.querySelector(".headerMoonfinButton");
    var userBtn = headerRight.querySelector(
      ".headerUserButton, .headerButton-user"
    );

    if (userBtn && userBtn.parentNode === headerRight) {
      if (userBtn.previousElementSibling === btn) {
        return true;
      }
      if (!btn) btn = createMoonfinButton();
      headerRight.insertBefore(btn, userBtn);
      return true;
    }

    if (btn) return true;
    btn = createMoonfinButton();

    var syncBtn = headerRight.querySelector(".headerSyncButton");
    if (syncBtn && syncBtn.parentNode === headerRight) {
      headerRight.insertBefore(btn, syncBtn);
    } else {
      headerRight.appendChild(btn);
    }
    return true;
  }

  function injectHeaderButton() {
    var injected = false;

    var toolbars = document.querySelectorAll(".MuiToolbar-root");
    for (var i = 0; i < toolbars.length; i++) {
      if (injectIntoToolbar(toolbars[i])) {
        injected = true;
      }
    }

    var legacyHeaders = document.querySelectorAll(
      ".skinHeader:not(.osdHeader) .headerRight, .headerRight"
    );
    for (var j = 0; j < legacyHeaders.length; j++) {
      if (injectIntoLegacyHeader(legacyHeaders[j])) {
        injected = true;
      }
    }

    if (!injected && !document.querySelector(".headerMoonfinButton:not([style*='display: none'])")) {
      var anyUserBtn = document.querySelector(
        ".headerUserButton, button[aria-controls='app-user-menu'], button[aria-label*='UserMenu']"
      );
      if (anyUserBtn && isElementVisible(anyUserBtn) && anyUserBtn.parentNode) {
        if (!anyUserBtn.parentNode.querySelector(".headerMoonfinButton")) {
          var fallbackBtn = createMoonfinButton();
          anyUserBtn.parentNode.insertBefore(fallbackBtn, anyUserBtn);
        }
      }
    }
  }

  if (document.readyState === "complete") {
    setTimeout(injectHeaderButton, 50);
  } else {
    window.addEventListener("load", function () {
      setTimeout(injectHeaderButton, 50);
    });
  }

  try {
    var observer = new MutationObserver(function () {
      injectHeaderButton();
    });
    observer.observe(document.body || document.documentElement, {
      childList: true,
      subtree: true,
    });
  } catch (e) {}

  setInterval(injectHeaderButton, 1000);
})();
