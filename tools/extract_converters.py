#!/usr/bin/env python3
"""Lift each mDataConvert scaling formula out of the decompiled source.

Every analogue register is a raw count that only means something once multiplied by a
model-specific scale factor. The arithmetic lives in mDataConvert, so take it from there
rather than inferring it from observed values.
"""
import json, re, sys
from pathlib import Path

SRC = Path(sys.argv[1] if len(sys.argv) > 1
           else "reference/decompiled/SP_LINK/SP_LINK/mDataConvert.cs")
SIG = re.compile(r'internal static \w[\w<>?\[\]]* fn(Convert\w+)\(([^)]*)\)')

lines = SRC.read_text(encoding="utf-8", errors="replace").split("\n")
out = {}
for i, line in enumerate(lines):
    m = SIG.search(line)
    if not m:
        continue
    depth = 0
    for j in range(i, len(lines)):
        depth += lines[j].count('{') - lines[j].count('}')
        if j > i and depth <= 0:
            body = "\n".join(lines[i + 1:j])
            break
    else:
        continue
    # The scaling is whichever arithmetic touches a divisor; keep every distinct one.
    maths = sorted({re.sub(r'\s+', ' ', x).strip()
                    for x in re.findall(r'new decimal\(([^;]{0,400}?)\)\s*;', body)}
                   | {re.sub(r'\s+', ' ', x).strip()
                      for x in re.findall(r'=\s*([^;=]*?/\s*[\d.]+[^;]{0,200});', body)})
    out[m.group(1)] = {
        "params": [p.strip() for p in m.group(2).split(",") if p.strip()],
        "maths": [x for x in maths if any(c in x for c in "*/")][:3],
        "lines": j - i,
    }

json.dump(out, sys.stdout, indent=2)
print(f"\n{len(out)} converters", file=sys.stderr)
