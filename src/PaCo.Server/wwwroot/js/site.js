// PaCo – small helpers. No inline scripts are allowed (Content-Security-Policy).
(function () {
    "use strict";

    // UI texts come from data-txt-* attributes (element first, then <body>), rendered in the user's language.
    function txt(el, name, fallback) {
        var a = "data-txt-" + name;
        return (el && el.getAttribute(a)) || document.body.getAttribute(a) || fallback;
    }

    // Short "done" feedback on a button, then restore its content (keeps the icon markup).
    function flash(el, text) {
        if (el.hasAttribute("data-flashing")) return;
        var old = Array.prototype.slice.call(el.childNodes);
        el.setAttribute("data-flashing", "");
        el.replaceChildren(document.createTextNode(text));
        setTimeout(function () { el.replaceChildren.apply(el, old); el.removeAttribute("data-flashing"); }, 2000);
    }

    // Confirmation for dangerous buttons: <button data-confirm="…">
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
        var text = (src.value !== undefined ? src.value : src.textContent).trim();
        var done = function () { flash(el, txt(el, "copied", "✓")); };
        if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(text).then(done, function () { if (src.select) src.select(); });
        } else if (src.select) {
            src.select(); // older browsers: at least select it so the user can copy by hand
        }
    });

    // Share buttons (phone share sheet: WhatsApp, Signal, Mail …): <button data-share="#elementId" hidden>
    // Only shown where the browser supports it.
    document.querySelectorAll("[data-share]").forEach(function (el) {
        if (!navigator.share) return;
        el.hidden = false;
        el.addEventListener("click", function () {
            var src = document.querySelector(el.getAttribute("data-share"));
            if (!src) return;
            var text = (src.value !== undefined ? src.value : src.textContent).trim();
            var data = { title: el.getAttribute("data-share-title") || "PaCo" };
            if (/^https?:\/\//.test(text)) data.url = text; else data.text = text;
            navigator.share(data).catch(function () { /* cancelled */ });
        });
    });

    // Submit a form as soon as a select changes: <select data-autosubmit>
    document.addEventListener("change", function (e) {
        var el = e.target.closest("[data-autosubmit]");
        if (el && el.form) el.form.requestSubmit ? el.form.requestSubmit() : el.form.submit();
    });

    // Arrow keys on a select inside a dropdown menu (language) belong to the select, not to Bootstrap's menu navigation.
    document.querySelectorAll(".dropdown-menu select").forEach(function (s) {
        s.addEventListener("keydown", function (e) {
            if (e.key === "ArrowUp" || e.key === "ArrowDown") e.stopPropagation();
        });
    });

    // Auto refresh (dashboard, live pages) unless the user is busy on the page.
    var auto = document.querySelector("[data-autorefresh]");
    if (auto) {
        var seconds = parseInt(auto.getAttribute("data-autorefresh"), 10) || 60;
        setInterval(function () {
            if (document.hidden) return;
            var a = document.activeElement;
            if (a && (a.tagName === "INPUT" || a.tagName === "SELECT" || a.tagName === "TEXTAREA")) return;
            if (document.querySelector(".alert-info .code-box")) return; // keep a fresh unlock code visible
            if (document.querySelector(".dropdown-menu.show, .navbar-collapse.show, .navbar-collapse.collapsing")) return; // menu open
            window.location.reload();
        }, seconds * 1000);
    }

    // Weekly plan on the rules page: "unlimited" / "all day" grey out the fields they make irrelevant.
    function syncDay(row) {
        var get = function (k) { return row.querySelector("[data-field='" + k + "']"); };
        var unlimited = get("unlimited"), allday = get("allday");
        if (unlimited && get("limit")) get("limit").disabled = unlimited.checked;
        if (allday) ["from", "to"].forEach(function (k) { if (get(k)) get(k).disabled = allday.checked; });
    }
    document.querySelectorAll("[data-day-row]").forEach(syncDay);
    document.addEventListener("change", function (e) {
        var row = e.target.closest("[data-day-row]");
        if (row && e.target.matches("[data-field='unlimited'], [data-field='allday']")) syncDay(row);
    });

    // Day-rule helpers on the rules page: copy one day to others.
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
            syncDay(dst);
        });
        flash(btn, txt(btn, "day-copied", "✓"));
    });
})();
