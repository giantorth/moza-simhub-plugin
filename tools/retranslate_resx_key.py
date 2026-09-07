#!/usr/bin/env python3
"""Rewrite the value of an EXISTING key across every MozaPlugin resx file.

tools/add_resx_strings.py is deliberately idempotent — it skips keys that are
already present — so it cannot reword one. This does that job, and only that:
the key must already exist in every locale (otherwise nothing is written and the
run fails), so it can never silently add a partial set.

Input JSON has the same shape as add_resx_strings.py:
    { "<Key>": { "en": "...", "de": "...", ... } }

Usage:
    python3 tools/retranslate_resx_key.py tools/<data>.json [--repo .] [--check]

--check reports what would change and exits non-zero if anything is stale.
"""
import json
import os
import re
import sys
from xml.sax.saxutils import escape

# Same map as add_resx_strings.py — keep the two in sync.
LOCALES = {
    "Strings.resx": "en",
    "Strings.de.resx": "de",
    "Strings.el.resx": "el",
    "Strings.es.resx": "es",
    "Strings.fr.resx": "fr",
    "Strings.it.resx": "it",
    "Strings.ko.resx": "ko",
    "Strings.nb.resx": "nb",
    "Strings.pt.resx": "pt",
    "Strings.qps-ploc.resx": "qps-ploc",
    "Strings.ru.resx": "ru",
    "Strings.vi.resx": "vi",
    "Strings.zh-Hans.resx": "zh-Hans",
}


def main(argv):
    args = [a for a in argv[1:] if not a.startswith("--")]
    flags = [a for a in argv[1:] if a.startswith("--")]
    if not args:
        print(__doc__)
        return 2

    data_path = args[0]
    repo = "."
    if "--repo" in argv:
        repo = argv[argv.index("--repo") + 1]
    check_only = "--check" in flags

    with open(data_path, encoding="utf-8") as fh:
        data = json.load(fh)

    res_dir = os.path.join(repo, "Resources")
    missing, changed = [], 0

    for filename, locale in LOCALES.items():
        path = os.path.join(res_dir, filename)
        with open(path, encoding="utf-8") as fh:
            text = fh.read()
        original = text

        for key, values in data.items():
            if locale not in values:
                missing.append(f"{key}: no '{locale}' value")
                continue
            # Match the whole <data> element for this key, whatever it holds.
            pattern = re.compile(
                r'<data name="%s"[^>]*>.*?</data>' % re.escape(key), re.DOTALL)
            if not pattern.search(text):
                missing.append(f"{key}: absent from {filename}")
                continue
            replacement = ('<data name="%s" xml:space="preserve"><value>%s</value></data>'
                           % (key, escape(values[locale])))
            text = pattern.sub(lambda _m: replacement, text, count=1)

        if text != original:
            changed += 1
            if not check_only:
                with open(path, "w", encoding="utf-8") as fh:
                    fh.write(text)
            print(f"{filename}: updated")

    if missing:
        for m in missing:
            print(f"ERROR {m}", file=sys.stderr)
        return 1
    if check_only:
        print(f"{changed} file(s) would change.")
        return 1 if changed else 0
    print(f"Done: {changed} file(s) updated.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
