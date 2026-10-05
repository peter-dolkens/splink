#!/usr/bin/env python3
"""Lift every SP LINK display field out of mDataDisplay.cs.

Each tab routine takes the raw register block as `ValueArray` and pushes it into named
WinForms controls through mDataConvert helpers. The control name, the word indices and the
converter together say what a register means, so this reads them straight out of the
decompiled source rather than having anyone guess.

Emits JSON: {routine: [{control, words, converters}]}.
"""
import json, re, sys
from pathlib import Path

SRC = Path(sys.argv[1] if len(sys.argv) > 1
           else "reference/decompiled/SP_LINK/SP_LINK/mDataDisplay.cs")
ROUTINE = re.compile(r'public static void (subDisplayData_\w+)\s*\(')
CONTROL = re.compile(r'^\s*(?:TextBox|Label|PictureBox|CheckBox|ComboBox)\s+(\w+)\s*=\s*fclsMain2?\.(\w+);')


def routines(lines):
    """Yield (name, body) for each routine, using brace depth to find the end."""
    for i, line in enumerate(lines):
        m = ROUTINE.search(line)
        if not m:
            continue
        depth = 0
        for j in range(i, len(lines)):
            depth += lines[j].count('{') - lines[j].count('}')
            if j > i and depth <= 0:
                yield m.group(1), lines[i:j]
                break


def fields(body):
    """Split a routine on each control it touches and collect that control's indices."""
    current, out = None, []
    for line in body:
        m = CONTROL.match(line)
        if m:
            current = {"control": m.group(2), "lines": []}
            out.append(current)
        elif current is not None:
            current["lines"].append(line)
    for f in out:
        text = "\n".join(f.pop("lines"))
        f["words"] = sorted({int(x) for x in re.findall(r'ValueArray\[(\d+)\]', text)})
        f["converters"] = sorted({c for c in re.findall(r'mDataConvert\.fn(\w+)', text)})
    return [f for f in out if f["words"]]


lines = SRC.read_text(encoding="utf-8", errors="replace").split("\n")
result = {name: fs for name, body in routines(lines) if (fs := fields(body))}
json.dump(result, sys.stdout, indent=2)
print(f"\n{len(result)} routines, {sum(len(v) for v in result.values())} fields", file=sys.stderr)
