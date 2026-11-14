// PaCo – small helpers. No inline scripts are allowed (Content-Security-Policy).
(function () {
    "use strict";

    // Confirmation for dangerous buttons: <button data-confirm="Wirklich?">
    document.addEventListener("click", function (e) {
        var el = e.target.closest("[data-confirm]");
        if (el && !window.confirm(el.getAttribute("data-confirm"))) {
            e.preventDefault();
            e.stopPropagation();
        }
    }, true);

    // Copy buttons: <button data-copy="#elementId">
    document.addEventListener("click", function (e) {
        var el = e.target.closest("[data-copy]");
        if (!el) return;
        var src = document.querySelector(el.getAttribute("data-copy"));
        if (!src) return;
        var text = src.value !== undefined ? src.value : src.textContent;
        navigator.clipboard.writeText(text.trim()).then(function () {
            var old = el.textContent;
            el.textContent = "Kopiert ✓";
            setTimeout(function () { el.textContent = old; }, 2000);
        });
    });

    // Auto refresh (dashboard) unless the user is typing in a form.
    var auto = document.querySelector("[data-autorefresh]");
    if (auto) {
        var seconds = parseInt(auto.getAttribute("data-autorefresh"), 10) || 60;
        setInterval(function () {
            var a = document.activeElement;
            if (a && (a.tagName === "INPUT" || a.tagName === "SELECT" || a.tagName === "TEXTAREA")) return;
            if (document.querySelector(".alert-info .code-box")) return; // keep a fresh unlock code visible
            window.location.reload();
        }, seconds * 1000);
    }

    // Day-rule helpers on the rules page.
    document.addEventListener("click", function (e) {
        var btn = e.target.closest("[data-copy-day]");
        if (!btn) return;
        e.preventDefault();
        var from = parseInt(btn.getAttribute("data-copy-day"), 10);
        var targets = btn.getAttribute("data-targets").split(",").map(Number);
        var row = function (i) { return document.querySelector("[data-day-row='" + i + "']"); };
        var src = row(from);
        targets.forEach(function (t) {
            var dst = row(t);
            if (!dst || t === from) return;
            ["limit", "from", "to", "unlimited", "allday"].forEach(function (k) {
                var s = src.querySelector("[data-field='" + k + "']"), d = dst.querySelector("[data-field='" + k + "']");
                if (!s || !d) return;
                if (s.type === "checkbox") d.checked = s.checked; else d.value = s.value;
            });
        });
    });
})();
