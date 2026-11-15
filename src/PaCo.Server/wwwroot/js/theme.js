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
    var labels = { auto: "Auto", light: "Hell", dark: "Dunkel" };
    var order = ["auto", "light", "dark"];
    function show(btn, mode) {
        btn.setAttribute("data-mode", mode);
        btn.setAttribute("title", "Farbschema: " + labels[mode]);
        var label = btn.querySelector(".theme-label");
        if (label) label.textContent = labels[mode]; else btn.textContent = labels[mode];
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
