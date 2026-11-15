#!/usr/bin/env python3
"""
Checks translation files against the key lists (tools/i18n/keys/*.json) without building.

  python tools/i18n/check.py               → all catalogs, all languages
  python tools/i18n/check.py fr ja client  → only these languages / catalogs
"""
import json, os, re, sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
DIRS = {"shared": "src/PaCo.Shared/I18n", "client": "src/PaCo.Client/I18n", "server": "src/PaCo.Server/I18n"}
LANGS = ["bg", "cs", "da", "el", "en", "es", "et", "fi", "fr", "ga", "hr", "hu", "it", "lt", "lv", "mt", "nl", "pl", "pt",
         "ro", "sk", "sl", "sv", "ca", "is", "lb", "nb", "sq", "sr", "bs", "mk", "uk", "ru", "tr", "zh-Hans", "zh-Hant", "ja", "ko"]
CATEGORIES = {"SelfHarm", "Drugs", "Violence", "Sexual", "Bullying", "Grooming", "Extremism"}
PH = re.compile(r"\{(\d+)(?:[,:][^}]*)?\}")


def placeholders(s):
    return set(PH.findall(s.replace("{{", "").replace("}}", "")))


def main(args):
    langs = [a for a in args if a in LANGS] or LANGS
    cats = [a for a in args if a in DIRS] or list(DIRS)
    bad = 0
    for cat in cats:
        keys_path = os.path.join(ROOT, "tools/i18n/keys", cat + ".json")
        if not os.path.exists(keys_path):
            continue
        keys = json.load(open(keys_path, encoding="utf-8"))
        for lang in langs:
            path = os.path.join(ROOT, DIRS[cat], f"{cat}.{lang}.json")
            if not os.path.exists(path):
                print(f"{cat}.{lang}: MISSING FILE"); bad += 1; continue
            try:
                table = json.load(open(path, encoding="utf-8"))
            except Exception as e:
                print(f"{cat}.{lang}: INVALID JSON {e}"); bad += 1; continue
            missing = [k for k in keys if not str(table.get(k, "")).strip()]
            extra = [k for k in table if k not in keys]
            ph = [k for k in keys if k in table and placeholders(k) != placeholders(table[k])]
            fmt = [k for k in keys if k in table and re.search(r"(?<!\{)\{(?!\d+(?:[,:][^}]*)?\})(?!\{)", table[k].replace("{{", "").replace("}}", ""))]
            if missing or extra or ph or fmt:
                bad += 1
                print(f"{cat}.{lang}: {len(missing)} missing, {len(extra)} unknown keys, {len(ph)} placeholder mismatches, {len(fmt)} stray braces")
                for k in (missing + extra + ph + fmt)[:5]:
                    print("   ", ascii(k)[:120])
    if "shared" in cats:
        for lang in langs + ["de"]:
            path = os.path.join(ROOT, DIRS["shared"], f"keywords.{lang}.json")
            if not os.path.exists(path):
                print(f"keywords.{lang}: MISSING FILE"); bad += 1; continue
            kw = json.load(open(path, encoding="utf-8"))
            if set(kw) - CATEGORIES or len(kw.get("SelfHarm", [])) < 5:
                print(f"keywords.{lang}: bad categories or < 5 SelfHarm keywords"); bad += 1
    print("OK" if bad == 0 else f"{bad} problem(s)")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
