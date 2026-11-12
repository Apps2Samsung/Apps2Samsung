// Install links (#1066): /install?app=<name|package id|app id>[&file=<file>] and /source?url=<https>.
// The page hands the same query to apps2samsung:// at once; when no app took it after 2 seconds,
// it shows the downloads. It checks the link the way the app does (Core InstallLink) and never
// fetches anything: the app looks the name up in its own catalog and the user's sources.
"use strict";

(function () {
  var MAX_VALUE = 200;
  var page = document.getElementById("page");
  var kind = page.getAttribute("data-kind");
  var params = new URLSearchParams(location.search);

  function el(tag, attrs, text) {
    var node = document.createElement(tag);
    if (attrs)
      Object.keys(attrs).forEach(function (k) {
        if (k === "class") node.className = attrs[k];
        else node.setAttribute(k, attrs[k]);
      });
    if (text != null) node.textContent = text;
    return node;
  }

  // One value per key, trimmed, 1..200 characters, no control characters; else null.
  function value(key) {
    var all = params.getAll(key);
    if (all.length !== 1) return null;
    var v = all[0].trim();
    if (!v || v.length > MAX_VALUE || /[\u0000-\u001f\u007f]/.test(v)) return null;
    return v;
  }

  function httpsUrl(text) {
    if (!text) return null;
    try {
      var u = new URL(text);
      if (u.protocol !== "https:" || !u.hostname || u.username || u.password) return null;
      return u.href;
    } catch (e) {
      return null;
    }
  }

  function render(nodes) {
    page.textContent = "";
    nodes.forEach(function (n) {
      page.appendChild(n);
    });
  }

  function broken() {
    document.title = "Link not complete: Apps2Samsung";
    render([
      el("h1", null, "This link is not complete"),
      el("p", { class: "muted" }, "It does not name an app or a source Apps2Samsung can open. Ask whoever shared it for the full link."),
      el("a", { class: "button primary", href: "../" }, "Go to apps2samsung.com"),
    ]);
  }

  var scheme;
  var head = [];
  if (kind === "install") {
    var app = value("app");
    var hasFile = params.has("file");
    var file = hasFile ? value("file") : null;
    if (!app || (hasFile && !file)) return broken();
    scheme = "apps2samsung://install?app=" + encodeURIComponent(app) + (file ? "&file=" + encodeURIComponent(file) : "");
    document.title = app + ": open in Apps2Samsung";
    head.push(el("div", { class: "tile", "aria-hidden": "true" }, Array.from(app)[0].toUpperCase()));
    head.push(el("h1", null, "Opening " + app + " in Apps2Samsung…"));
    head.push(el("p", { class: "muted" }, "Your browser may ask whether to open Apps2Samsung. Say yes, then pick the TV in the app. Nothing installs by itself."));
  } else {
    var url = httpsUrl(value("url"));
    if (!url) return broken();
    scheme = "apps2samsung://source?url=" + encodeURIComponent(url);
    head.push(el("h1", null, "Adding a source to Apps2Samsung…"));
    head.push(el("p", { class: "muted" }, "This link adds someone's own list of apps next to the official catalog:"));
    head.push(el("code", null, url));
    head.push(el("p", { class: "warn" }, "Apps2Samsung does not check the apps in a source. The app shows the source's name and key before it adds anything; add it only if you trust who made it."));
  }

  var fallback = el("div", { class: "fallback", id: "fallback" });
  fallback.hidden = true;
  fallback.appendChild(el("hr"));
  fallback.appendChild(el("p", null, "Nothing opened?"));
  var downloads = el("div", { class: "downloads" });
  var release = "https://github.com/Apps2Samsung/Apps2Samsung/releases/latest";
  [
    ["Windows", release],
    ["macOS", release],
    ["Linux", release],
    ["Android", "../#download"],
    ["iPhone", "../#download"],
  ].forEach(function (d) {
    downloads.appendChild(el("a", { href: d[1] }, d[0]));
  });
  fallback.appendChild(downloads);
  var again = el("a", { class: "button", href: scheme }, "Try again");
  fallback.appendChild(again);
  var cli =
    kind === "install"
      ? 'apps2samsung-cli install "' + location.href + '" --tv <ip>'
      : "apps2samsung-cli sources add " + location.href;
  fallback.appendChild(el("p", { class: "small" }, "On the command line:"));
  fallback.appendChild(el("code", null, cli));
  if (kind === "install")
    fallback.appendChild(el("p", { class: "small" }, "With TV-Store on the TV, you can also search for the app there."));

  render(head.concat([fallback]));

  // The app took the link when the page lost focus or went to the background.
  var opened = false;
  function left() {
    opened = true;
  }
  window.addEventListener("blur", left);
  // Back on the page (a browser prompt closed, or the user came back from the app): the downloads may help.
  window.addEventListener("focus", function () {
    if (opened) fallback.hidden = false;
  });
  document.addEventListener("visibilitychange", function () {
    if (document.hidden) left();
  });

  function open() {
    opened = false;
    fallback.hidden = true;
    location.href = scheme;
    setTimeout(function () {
      if (!opened) fallback.hidden = false;
    }, 2000);
  }
  again.addEventListener("click", function (e) {
    e.preventDefault();
    open();
  });
  open();
})();
