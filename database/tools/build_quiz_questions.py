"""Bake the Miracle Quiz questions + choices into tvlistings_catalog.json.

Source: title_channel/title/quiz/msg_quiz.bmd (extracted from quiz.arc) — 186
QUIZ_NNN records (the question text) each paired with ANSWER_NNN (its 4 choices
in BMD order). The on-screen layout is COLUMN-MAJOR: BMD choice index 0=A, 1=C,
2=B, 3=D (verified vs live screenshots 2026-07-31). The mod maps letters itself;
this file stores the choices in BMD order.

Run:  python build_quiz_questions.py
Needs: BmdDecompiler (built) if the .msg decompile is missing.
"""
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent          # database/
QUIZ_BMD = ROOT / "extract/title_channel/title/quiz/msg_quiz.bmd"
QUIZ_MSG = ROOT / "extract/title_channel/title/quiz/msg_quiz.msg"
CATALOG = ROOT / "tvlistings_catalog.json"
DECOMPILER = ROOT / "tools/BmdDecompiler/bin/Release/net9.0/BmdDecompiler.dll"
DOTNET = r"C:\Program Files\dotnet\dotnet.exe"


def ensure_msg():
    if QUIZ_MSG.exists():
        return
    subprocess.run([DOTNET, str(DECOMPILER), str(QUIZ_BMD), str(QUIZ_MSG)], check=True)


def parse_blocks(text):
    """-> {name: [text lines]} — func codes and <new line> markers dropped."""
    blocks = {}
    name, lines = None, []
    for raw in text.splitlines():
        m = re.match(r"\[Message ([A-Z_0-9]+)\]", raw.strip())
        if m:
            if name:
                blocks[name] = lines
            name, lines = m.group(1), []
            continue
        s = raw.strip()
        if not s or s.startswith("page ") or s == "<new line>":
            continue
        s = re.sub(r"func_\d+_\d+\([^)]*\)", "", s).strip()
        if s:
            lines.append(s)
    if name:
        blocks[name] = lines
    return blocks


def main():
    ensure_msg()
    blocks = parse_blocks(QUIZ_MSG.read_text(encoding="utf-8"))
    entries = []
    n = 1
    while f"QUIZ_{n:03d}" in blocks:
        q = " ".join(blocks[f"QUIZ_{n:03d}"])
        c = blocks.get(f"ANSWER_{n:03d}", [])
        if len(c) != 4:
            print(f"WARN: ANSWER_{n:03d} has {len(c)} choices: {c}")
        entries.append({"q": q, "c": c})
        n += 1
    print(f"parsed {len(entries)} questions")

    catalog = json.loads(CATALOG.read_text(encoding="utf-8"))
    catalog["quiz_questions"] = entries
    CATALOG.write_text(json.dumps(catalog, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"wrote quiz_questions into {CATALOG}")


if __name__ == "__main__":
    main()
