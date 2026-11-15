#!/usr/bin/env python3
"""
Extracts translatable German source texts (catalog keys) from the PaCo sources.

  python tools/i18n/extract.py            → writes tools/i18n/keys/{shared,client,server}.json
  python tools/i18n/extract.py --missing  → lists keys missing per language file

Rules (same as tests/PaCo.Tests/I18nTests.cs):
  client:  L.T("…"), L.F("…", …), L.In(lang, "…", …)
  shared:  LocText.Of("…", …), SharedText.T/F(lang, "…"), Ui.S("…")
  server:  Ui.T("…"), Ui.F("…", …), Ui.In(lang, "…", …), @T("…"), @F("…", …), T("…") in Razor, ErrorMessage = "…"
  any:     every string literal between "// i18n-keys:start" and "// i18n-keys:end" (catalog given after start)
Adjacent literals joined with + are concatenated.
"""
import json, os, re, sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
LIT = r'"(?:[^"\\\n]|\\.)*"'
CONCAT = rf'{LIT}(?:\s*\+\s*{LIT})*'

PATTERNS = {
    "client": [rf'\bL\.(?:T|F)\(\s*({CONCAT})', rf'\bL\.In\([^,()]+,\s*({CONCAT})'],
    "shared": [rf'\bLocText\.Of\(\s*({CONCAT})', rf'\bSharedText\.(?:T|F)\([^,()]+,\s*({CONCAT})', rf'\bUi\.S\(\s*({CONCAT})'],
    "server": [rf'\bUi\.(?:T|F)\(\s*({CONCAT})', rf'\bUi\.In\([^,()]+,\s*({CONCAT})',
               rf'(?<![\w.])(?:T|F)\(\s*({CONCAT})', rf'\bErrorMessage\s*=\s*({CONCAT})'],
}
# Which projects each catalog is collected from.
SOURCES = {"client": ["src/PaCo.Client"], "shared": ["src/PaCo.Shared", "src/PaCo.Client", "src/PaCo.Server"], "server": ["src/PaCo.Server"]}
BLOCK = re.compile(r'//\s*i18n-keys:start\s+(\w+)(.*?)//\s*i18n-keys:end', re.S)


def unescape(lit):
    out, i, body = [], 0, lit[1:-1]
    while i < len(body):
        c = body[i]
        if c == "\\":
            n = body[i + 1]
            out.append({"n": "\n", "t": "\t", '"': '"', "\\": "\\", "r": "\r", "0": "\0"}.get(n, n))
            i += 2
        else:
            out.append(c)
            i += 1
    return "".join(out)


def join(concat):
    return "".join(unescape(m) for m in re.findall(LIT, concat))


def files(dirs):
    for d in dirs:
        for base, sub, names in os.walk(os.path.join(ROOT, d)):
            sub[:] = [s for s in sub if s not in ("bin", "obj", "Migrations", "lib")]
            for n in names:
                if n.endswith((".cs", ".cshtml")):
                    yield os.path.join(base, n)


def extract():
    keys = {k: set() for k in PATTERNS}
    for cat, dirs in SOURCES.items():
        for f in files(dirs):
            src = open(f, encoding="utf-8-sig").read()
            for p in PATTERNS[cat]:
                # Razor-only bare T(/F( pattern: only in .cshtml files.
                if p.startswith(r'(?<![\w.])') and not f.endswith(".cshtml"):
                    continue
                for m in re.finditer(p, src):
                    keys[cat].add(join(m.group(1)))
    for f in files(["src"]):
        src = open(f, encoding="utf-8-sig").read()
        for m in BLOCK.finditer(src):
            for lit in re.findall(CONCAT, m.group(2)):
                keys[m.group(1)].add(join(lit))
    for k in keys:
        keys[k].discard("")
    return {k: sorted(v) for k, v in keys.items()}


def main():
    keys = extract()
    out = os.path.join(ROOT, "tools", "i18n", "keys")
    os.makedirs(out, exist_ok=True)
    for cat, ks in keys.items():
        with open(os.path.join(out, cat + ".json"), "w", encoding="utf-8", newline="\n") as fh:
            json.dump(ks, fh, ensure_ascii=False, indent=1)
            fh.write("\n")
        print(f"{cat}: {len(ks)} keys")
    if "--missing" in sys.argv:
        dirs = {"shared": "src/PaCo.Shared/I18n", "client": "src/PaCo.Client/I18n", "server": "src/PaCo.Server/I18n"}
        for cat, d in dirs.items():
            d = os.path.join(ROOT, d)
            if not os.path.isdir(d):
                continue
            for n in sorted(os.listdir(d)):
                if n.startswith(cat + ".") and n.endswith(".json"):
                    have = json.load(open(os.path.join(d, n), encoding="utf-8"))
                    miss = [k for k in keys[cat] if k not in have]
                    if miss:
                        print(f"  {n}: {len(miss)} missing")


if __name__ == "__main__":
    main()
