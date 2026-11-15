// Loaded synchronously in <head> so the page never flashes in the wrong theme.
// Theme: "auto" (follows the device), "light" or "dark"; remembered per browser.
(function () {
    "use strict";
    var KEY = "paco-theme";
    function stored() { try { return localStorage.getItem(KEY) || "auto"; } catch (e) { return "auto"; } }
    function resolve(t) {
        if (t === "light" || t === "dark") return t;
        return window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
    }
    function apply(t) { document.documentElement.setAttribute("data-bs-theme", resolve(t)); }

    apply(stored());
    if (window.matchMedia) {
        window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", function () {
            if (stored() === "auto") apply("auto");
        });
    }

    // The icon comes from CSS ([data-mode]); the button only carries the text label.
    // Labels and the tooltip ("…: {0}") come from data-label-auto/-light/-dark and data-title in the user's language.
    var order = ["auto", "light", "dark"];
    function show(btn, mode) {
        var text = btn.getAttribute("data-label-" + mode) || mode;
        btn.setAttribute("data-mode", mode);
        btn.setAttribute("title", (btn.getAttribute("data-title") || "{0}").replace("{0}", function () { return text; }));
        var label = btn.querySelector(".theme-label");
        if (label) label.textContent = text; else btn.textContent = text;
    }
    document.addEventListener("DOMContentLoaded", function () {
        document.querySelectorAll("[data-theme-toggle]").forEach(function (btn) {
            show(btn, stored());
            btn.addEventListener("click", function () {
                var next = order[(order.indexOf(stored()) + 1) % order.length];
                try { localStorage.setItem(KEY, next); } catch (e) { /* private mode */ }
                apply(next);
                show(btn, next);
            });
        });
    });
})();
