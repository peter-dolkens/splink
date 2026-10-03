"""Recover the value->label tables SP LINK uses to render configuration settings.

Each enum-style converter is a `subUpdate<Name>Setting` method containing one C# switch
expression over `NewValue` whose arms are `<n> => "<label>",`. Extracting those arms gives
us the same labels SP LINK shows, without transcribing them by hand.
"""
import re, json, pathlib

SRC = pathlib.Path("reference/decompiled/SP_LINK/SP_LINK/mConfig.cs")
text = SRC.read_text()
lines = text.splitlines()

FUNC = re.compile(r"^\t(?:internal|public|private)\s+static\s+\w[\w<>\[\]]*\s+subUpdate(?P<name>\w+?)Setting\(")
ARM = re.compile(r'^\s*(?P<val>\d+)u?\s*=>\s*"(?P<label>[^"]*)",?\s*$')
SWITCH = re.compile(r"NewValue switch")

tables, i = {}, 0
while i < len(lines):
    m = FUNC.match(lines[i])
    if not m:
        i += 1
        continue
    name = m.group("name")
    # Scan the method body only, stopping at the closing brace at method indentation.
    j, depth, seen_switch, arms = i + 1, 0, False, {}
    while j < len(lines):
        line = lines[j]
        if line == "\t}":
            break
        if SWITCH.search(line):
            seen_switch = True
        if seen_switch:
            a = ARM.match(line)
            if a:
                arms[int(a.group("val"))] = a.group("label")
        j += 1
    if arms:
        tables[name] = dict(sorted(arms.items()))
    i = j + 1

pathlib.Path("tools/config_enums.json").write_text(json.dumps(tables, indent=1))
print(f"{len(tables)} enum tables extracted\n")
for n, t in sorted(tables.items(), key=lambda kv: -len(kv[1]))[:14]:
    sample = ", ".join(f"{k}={v}" for k, v in list(t.items())[:4])
    print(f"  {n:34s} {len(t):3d} values   {sample}")
