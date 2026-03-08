// Moonfin Plugin Loader
// This file is injected into index.html by the File Transformation plugin
// It loads the main plugin.js and plugin.css files
(function () {
  "use strict";

  console.log("Moonfin loader starting...");

  // Get the base URL for the Moonfin plugin
  var baseUrl = "/Moonfin/Web/";
  var cacheBust = "?v=" + Date.now();

  // Load CSS
  var css = document.createElement("link");
  css.rel = "stylesheet";
  css.type = "text/css";
  css.href = baseUrl + "plugin.css" + cacheBust;
  document.head.appendChild(css);
  console.log("Moonfin CSS loaded");

  // Load JS
  var script = document.createElement("script");
  script.type = "text/javascript";
  script.src = baseUrl + "plugin.js" + cacheBust;
  script.onload = function () {
    console.log("Moonfin plugin.js loaded successfully");
  };
  script.onerror = function () {
    console.error("Failed to load Moonfin plugin.js");
  };
  document.head.appendChild(script);
})();
