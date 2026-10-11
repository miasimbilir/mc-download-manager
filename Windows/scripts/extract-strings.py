#!/usr/bin/env python3
"""Converts App/Resources/Localizable.xcstrings (the macOS app's string catalog) into
Windows/src/MCDM.Core/Strings.tr.json. printf specifiers become .NET placeholders:
"%lld of %@" -> "{0} of {1}". Run again whenever the upstream catalog changes."""
import json, re, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SPEC = re.compile(r"%(?:(\d+)\$)?(?:lld|ld|d|@|f|\.\d+f)")


def convert(s: str) -> str:
    n = 0

    def repl(m):
        nonlocal n
        if m.group(1):
            return "{%d}" % (int(m.group(1)) - 1)
        n += 1
        return "{%d}" % (n - 1)

    return SPEC.sub(repl, s.replace("{", "{{").replace("}", "}}")).replace("%%", "%")


def main():
    cat = json.loads((ROOT / "App/Resources/Localizable.xcstrings").read_text(encoding="utf-8"))
    out = {}
    for key, entry in cat["strings"].items():
        tr = entry.get("localizations", {}).get("tr", {}).get("stringUnit", {}).get("value")
        if tr:
            out[convert(key)] = convert(tr)
    dest = ROOT / "Windows/src/MCDM.Core/Strings.tr.json"
    dest.write_text(json.dumps(dict(sorted(out.items())), ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    print(f"{len(out)} strings -> {dest.relative_to(ROOT)}", file=sys.stderr)


if __name__ == "__main__":
    main()
