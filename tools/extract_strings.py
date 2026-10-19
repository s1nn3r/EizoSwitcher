"""Lists all localizable strings (English keys) from L.T("de", "en", ...) calls.

usage: python extract_strings.py [--check]
  without option: prints {english: german} as JSON (template for new translations)
  --check:        reports missing / unused keys in src/EizoSwitcher.Core/Lang/*.json
"""
import json, re, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
LANG = ROOT / "src" / "EizoSwitcher.Core" / "Lang"


def parse_literal(src, i):
    """Parses a C# regular string literal starting at src[i] == '"'. Returns (value, end index)."""
    assert src[i] == '"'
    out, i = [], i + 1
    while src[i] != '"':
        if src[i] == "\\":
            nxt = src[i + 1]
            out.append({"n": "\n", "t": "\t", '"': '"', "\\": "\\", "'": "'"}.get(nxt, nxt))
            i += 2
        else:
            out.append(src[i])
            i += 1
    return "".join(out), i + 1


def parse_concat(src, i):
    """"a" + "b" + ... -> "ab"; returns None if the argument is not a pure literal."""
    parts = []
    while True:
        while src[i].isspace():
            i += 1
        if src[i] != '"':
            return None, i
        value, i = parse_literal(src, i)
        parts.append(value)
        j = i
        while src[j].isspace():
            j += 1
        if src[j] != "+":
            return "".join(parts), i
        i = j + 1


def extract():
    pairs = {}
    for f in (ROOT / "src").rglob("*.cs"):
        if "obj" in f.parts or "bin" in f.parts:
            continue
        src = f.read_text(encoding="utf-8")
        for m in re.finditer(r"\bL\.T\(", src):
            de, i = parse_concat(src, m.end())
            if de is None:
                continue
            while src[i].isspace():
                i += 1
            if src[i] != ",":
                continue
            en, i = parse_concat(src, i + 1)
            if en is not None:
                pairs[en] = de
    return dict(sorted(pairs.items()))


if __name__ == "__main__":
    pairs = extract()
    if "--check" in sys.argv:
        bad = 0
        for f in sorted(LANG.glob("*.json")):
            table = json.loads(f.read_text(encoding="utf-8"))
            missing = [k for k in pairs if k not in table]
            unused = [k for k in table if k not in pairs]
            print(f"{f.name}: {len(table)} entries, {len(missing)} missing, {len(unused)} unused")
            for k in missing:
                print("   missing:", k)
            bad += len(missing)
        sys.exit(1 if bad else 0)
    sys.stdout.reconfigure(encoding="utf-8")
    print(json.dumps(pairs, ensure_ascii=False, indent=2))
