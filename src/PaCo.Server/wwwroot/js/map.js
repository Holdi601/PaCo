// Location map (Leaflet + OpenStreetMap). Data comes from the data-points attribute (no inline script – CSP).
(function () {
    "use strict";
    // Clicking a coordinate in the list shows it on the Google map.
    document.addEventListener("click", function (e) {
        var a = e.target.closest("[data-gmap]");
        var frame = document.getElementById("gmap");
        if (!a || !frame) return;
        e.preventDefault();
        frame.src = a.getAttribute("data-gmap");
        frame.scrollIntoView({ behavior: "smooth", block: "center" });
    });

    // The OSM trail map lives in a <details>; Leaflet needs a visible container to size itself.
    var details = document.querySelector("details:has(#paco-map)");
    if (details && !details.open) {
        details.addEventListener("toggle", function once() { details.removeEventListener("toggle", once); init(); });
        return;
    }
    init();

    function init() {
    var el = document.getElementById("paco-map");
    if (!el || !window.L) return;
    var points = JSON.parse(el.getAttribute("data-points") || "[]");
    L.Icon.Default.imagePath = "/lib/leaflet/images/";
    var map = L.map(el, { scrollWheelZoom: false });
    L.tileLayer("https://tile.openstreetmap.org/{z}/{x}/{y}.png", {
        maxZoom: 19,
        attribution: "© OpenStreetMap-Mitwirkende",
        // The site sends no referrer; OSM's tile policy needs at least the origin.
        referrerPolicy: "strict-origin-when-cross-origin"
    }).addTo(map);

    if (!points.length) {
        map.setView([51.16, 10.45], 6);
        return;
    }
    var latlngs = points.map(function (p) { return [p.lat, p.lon]; });
    if (latlngs.length > 1) {
        L.polyline(latlngs, { color: "#4f7cff", weight: 3, opacity: 0.6 }).addTo(map);
        points.slice(0, -1).forEach(function (p) {
            L.circleMarker([p.lat, p.lon], { radius: 4, color: "#7c5cff", weight: 1, fillOpacity: 0.7 })
                .bindPopup(p.time + (p.ssid ? "<br>WLAN: " + escapeHtml(p.ssid) : "")).addTo(map);
        });
    }
    var last = points[points.length - 1];
    if (last.acc) L.circle([last.lat, last.lon], { radius: last.acc, color: "#22a565", weight: 1, fillOpacity: 0.12 }).addTo(map);
    L.marker([last.lat, last.lon]).bindPopup("<b>Zuletzt: " + last.time + "</b><br>Genauigkeit ±" + Math.round(last.acc || 0) + " m" +
        (last.ssid ? "<br>WLAN: " + escapeHtml(last.ssid) : "")).addTo(map).openPopup();
    if (latlngs.length > 1) map.fitBounds(latlngs, { padding: [30, 30], maxZoom: 16 });
    else map.setView([last.lat, last.lon], 16);
    }

    function escapeHtml(s) {
        return String(s).replace(/[&<>"']/g, function (c) {
            return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
        });
    }
})();
