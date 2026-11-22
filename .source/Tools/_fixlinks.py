import posixpath, re
from pathlib import Path

LINK = re.compile(r"\]\(([^)\s]+?\.md)(#[^)]*)?\)")
d = Path("docs/_archive/implemented")

for f in sorted(d.glob("*.md")):
    text = f.read_text(encoding="utf-8")

    def repl(m):
        target, frag = m.group(1), m.group(2) or ""
        if "://" in target:
            return m.group(0)
        if (d / target).exists():
            return m.group(0)
        old = Path("docs") / target
        if old.exists():
            rel = posixpath.relpath(old.as_posix(), d.as_posix())
            return f"]({rel}{frag})"
        return m.group(0)

    new = LINK.sub(repl, text)
    if new != text:
        f.write_text(new, encoding="utf-8", newline="\n")
        print("fixed", f.name)
