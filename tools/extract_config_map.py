"""Derive the SP PRO configuration register map from SP LINK's decompiled subLoadArrayToSettings_* methods.

Each setting appears as a near-identical pair of lines:
    <Kind>ToUpdate = fclsMainN.<controlName>;
    subUpdate<Converter>(ref <Kind>ToUpdate, ArrayOf<Block>Values[0, <index>], HighLightChanges);
so the control name, converter and word index can be recovered by scanning for the call and
looking back for the most recent assignment of the control variable it references.
"""
import re, sys, json, pathlib

SRC = pathlib.Path("reference/decompiled/SP_LINK/SP_LINK/mConfig.cs")
BLOCKS = {
    "ArrayOfCommonValues": "Common",
    "ArrayOfAppTypeParameterValues": "AppType",
    "ArrayOfBattTypeParameterValues": "BattType",
    "ArrayOfSystemSchedulerValues": "Scheduler",
}
CALL = re.compile(r"(sub\w+)\((?P<args>[^;]*?)\);")
IDX = re.compile(r"(?P<arr>ArrayOf\w+)\[0, (?P<idx>\d+)\]")
ASSIGN = re.compile(r"^\s*(?:\w[\w<>\[\]]*\s+)?(?P<var>\w+ToUpdate)\s*=\s*fclsMain\d*\.(?P<ctl>\w+);")
PREFIXES = ("cb", "nud", "dtp", "tb", "lbl", "chk", "rb")

def clean(ctl):
    for p in sorted(PREFIXES, key=len, reverse=True):
        if ctl.startswith(p) and len(ctl) > len(p) and ctl[len(p)].isupper():
            return ctl[len(p):]
    return ctl

lines = SRC.read_text().splitlines()
recent = {}          # variable name -> most recently assigned control
found = {}           # block -> {index: {...}}
for raw in lines:
    m = ASSIGN.match(raw)
    if m:
        recent[m.group("var")] = m.group("ctl")
        continue
    call = CALL.search(raw)
    if not call:
        continue
    hit = IDX.search(call.group("args"))
    if not hit or hit.group("arr") not in BLOCKS:
        continue
    block = BLOCKS[hit.group("arr")]
    index = int(hit.group("idx"))
    refs = re.findall(r"ref (\w+ToUpdate)", call.group("args"))
    ctl = next((recent[r] for r in refs if r in recent), None)
    if ctl is None:
        continue
    found.setdefault(block, {}).setdefault(index, {
        "index": index, "name": clean(ctl), "control": ctl,
        "converter": call.group(1).replace("subUpdate", "").replace("Setting", ""),
    })

out = {b: [v for _, v in sorted(d.items())] for b, d in found.items()}
pathlib.Path("tools/config_map.json").write_text(json.dumps(out, indent=1))
for b, items in out.items():
    idxs = [i["index"] for i in items]
    print(f"{b:10s} {len(items):4d} settings, index {min(idxs)}..{max(idxs)}")
print("\nconverters used:")
kinds = {}
for items in out.values():
    for i in items:
        kinds[i["converter"]] = kinds.get(i["converter"], 0) + 1
for k, n in sorted(kinds.items(), key=lambda kv: -kv[1]):
    print(f"  {n:4d}  {k}")
