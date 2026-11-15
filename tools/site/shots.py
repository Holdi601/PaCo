#!/usr/bin/env python3
"""
Screenshots for the project website (site/img/) from demo pages rendered by the test
WebFlowTests.RenderDemoPagesForProjectSite (made-up data only).

  set PACO_SHOTS=<folder>   &&  dotnet test tests/PaCo.Tests --filter RenderDemoPagesForProjectSite
  python tools/site/shots.py <folder>

Needs Microsoft Edge (headless). Writes site/img/<page>.<lang>.<desktop|phone>[.dark].png
"""
import os, re, subprocess, sys, tempfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
WWW = os.path.join(ROOT, "src", "PaCo.Server", "wwwroot").replace("\\", "/")
OUT = os.path.join(ROOT, "site", "img")
EDGE = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"

# page → which variants are worth showing on the website
JOBS = {
    "dashboard": [("desktop", "light"), ("phone", "light"), ("phone", "dark"), ("desktop", "dark")],
    "rules": [("desktop", "light"), ("phone", "light")],
    "stats": [("desktop", "light")],
    "devices": [("desktop", "light")],
    "alerts": [("phone", "light"), ("desktop", "light")],
    "log": [("desktop", "dark")],
    "notifications": [("phone", "light")],
    "login": [("desktop", "light"), ("phone", "dark")],
}
SIZES = {"desktop": ("1280,860", "1"), "phone": ("390,844", "2")}


def prepare(html, theme):
    html = re.sub(r'(href|src)="/(css|js|lib|favicon)([^"?]*)(\?[^"]*)?"', lambda m: f'{m.group(1)}="file:///{WWW}/{m.group(2)}{m.group(3)}"', html)
    html = re.sub(r'<script src="[^"]*theme\.js[^"]*"></script>', "", html)
    html = html.replace("<html ", f'<html data-bs-theme="{theme}" ', 1)
    # No auto-refresh / animations in a still picture.
    html = html.replace("</head>", "<style>*{animation:none!important;transition:none!important}</style></head>", 1)
    return html


def main(src):
    os.makedirs(OUT, exist_ok=True)
    tmp = tempfile.mkdtemp(prefix="paco-shots-")
    for page, variants in JOBS.items():
        for lang in ("de", "en"):
            path = os.path.join(src, f"{page}.{lang}.html")
            if not os.path.exists(path):
                print("missing", path); continue
            raw = open(path, encoding="utf-8").read()
            for device, theme in variants:
                f = os.path.join(tmp, f"{page}.{lang}.{device}.{theme}.html")
                open(f, "w", encoding="utf-8").write(prepare(raw, theme))
                name = f"{page}.{lang}.{device}" + (".dark" if theme == "dark" else "") + ".png"
                size, scale = SIZES[device]
                target = f
                if device == "phone":
                    # Headless Edge never makes the viewport narrower than ~490 px; an iframe gives the page a real
                    # 390 px phone viewport, and the screenshot is cropped to the window size.
                    w, h = size.split(",")
                    target = f + ".frame.html"
                    open(target, "w", encoding="utf-8").write(
                        f'<!doctype html><html><body style="margin:0"><iframe src="{os.path.basename(f)}" '
                        f'style="border:0;width:{w}px;height:{h}px;display:block"></iframe></body></html>')
                subprocess.run([EDGE, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--allow-file-access-from-files",
                                f"--window-size={size}", f"--force-device-scale-factor={scale}", "--virtual-time-budget=4000",
                                f"--screenshot={os.path.join(OUT, name)}", "file:///" + target.replace("\\", "/")],
                               check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=90)
                print(name)


if __name__ == "__main__":
    main(sys.argv[1])
