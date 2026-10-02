# Rewrites private-use-area glyph characters (Segoe Fluent Icons) as readable escapes:
# C# files get the six-character escape form, XAML files get hex character entities.
# Usage: python tools/escape-glyphs.py
import pathlib

BACKSLASH = chr(92)
ROOT = pathlib.Path(__file__).resolve().parent.parent / "src"


def is_private_use(ch):
    return 0xE000 <= ord(ch) <= 0xF8FF


def rewrite(path, make_escape):
    text = path.read_text(encoding="utf-8")
    result = "".join(make_escape(ch) if is_private_use(ch) else ch for ch in text)
    if result != text:
        path.write_text(result, encoding="utf-8")
        return True
    return False


changed = []
for path in ROOT.rglob("*"):
    if "obj" in path.parts or "bin" in path.parts or not path.is_file():
        continue
    if path.suffix == ".cs":
        if rewrite(path, lambda ch: BACKSLASH + "u" + format(ord(ch), "04X")):
            changed.append(path.name)
    elif path.suffix == ".xaml":
        if rewrite(path, lambda ch: "&#x" + format(ord(ch), "04X") + ";"):
            changed.append(path.name)

print("Rewritten:", ", ".join(changed) if changed else "nothing")
