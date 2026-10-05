#!/usr/bin/env python3
"""Generate SpProDisplayBlocks.g.cs from SP LINK's display routines.

Each tab routine pushes a raw register block into named controls through mDataConvert helpers.
The control name, the word indices and the converter together define what a register means, so
the whole table is lifted from the decompiled source rather than transcribed by hand.
"""
import json, re, sys
from pathlib import Path

SRC = Path("reference/decompiled/SP_LINK/SP_LINK/mDataDisplay.cs")
OUT = Path("src/SpLink.Protocol/SpProDisplayBlocks.g.cs")

# routine -> (block name, address, word count). Counts are the NoOfLocations constant plus one:
# SP LINK stores wordCount-1 in the request, so the constant is one short of the real length.
BLOCKS = {
    "subDisplayData_NowSubTab":                  ("Now",            41048,  85),
    "subDisplayData_TodaySubTab":                ("Today",          41135,  68),
    "subDisplayData_DCHistorySubTab":            ("DcHistory",      41255, 140),
    "subDisplayData_ACHistorySubTab":            ("AcHistory",      41418, 103),
    "subDisplayData_ACHistorySubTab2":           ("AcHistory2",     41838,  18),
    "subDisplayData_TechnicalDataSubTab":        ("Technical",      40981, 154),
    "subDisplayData_TechnicalDataSubTab2_Kaco":  ("AcCoupled",      41912,  61),
    "subDisplayData_TechnicalDataSubTab3_Charger": ("Charger",      41556,   4),
    "subDisplayData_RAndDOnlyDataSubTab":        ("Engineering",    40967,  47),
    "subDisplayData_Now_Today_Tech_KacoModelsCapacityAndOEM": ("AcCoupledModels", 41862, 26),
    "subDisplayData_NowNetworkPowerSubTab":      ("NetworkPower",   41963,  10),
    "subDisplayData_Now_Mppt_Target":            ("MpptTarget",     41169,   6),
    "subDisplayData_Bms":                        ("Bms",            41589,  20),
    "subDisplayData_BmsModules":                 ("BmsModules",     53248,  27),
    "subDisplayData_Now_Tech_MPPT":              ("Mppt",           56832, 176),
}

# Values SP LINK stashes in globals rather than pushing to a control, so the scrape cannot see
# them. The AC-coupled model codes identify the attached solar inverters, which is worth having.
EXTRA = {
    "AcCoupledModels": [
        ("KacoModel1", [19], "ConvertKacoModelNumberToString"),
        ("KacoModel2", [20], "ConvertKacoModelNumberToString"),
        ("KacoModel3", [21], "ConvertKacoModelNumberToString"),
        ("KacoModel4", [22], "ConvertKacoModelNumberToString"),
        ("KacoModel5", [23], "ConvertKacoModelNumberToString"),
    ],
}

ROUTINE = re.compile(r'public static void (subDisplayData_\w+)\s*\(')
# Two assignment shapes appear: a control hoisted into a local first, or addressed inline.
ASSIGN = re.compile(r'(?:^|\s)(?:fclsMain2?\.)?(tb\w+|lbl\w+)\.Text\s*=\s*(.+)$')
LOCAL = re.compile(r'^\s*(?:TextBox|Label|CheckBox|PictureBox)\s+(\w+)\s*=\s*fclsMain2?\.(\w+);')


def routines(lines):
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


# A control's name says what quantity it holds, so a converter that disagrees is the wrong branch.
# This resolves assignments the generator cannot otherwise choose between -- SP LINK reuses the
# AC-coupled controls for a network power meter when one is fitted, deciding at runtime.
AGREEMENT = [
    (("Energy", "kWh", "KWH"), "Energy"),
    (("Volts", "Voltage"),     "Voltage"),
    (("Current", "Amps"),      "Current"),
    (("Power",),               "Power"),
    (("Temperature", "Temp"),  "Temperature"),
    (("Hrs", "Hours"),         "Time"),
    (("Percent",),             "Percentage"),
    (("Freq",),                "Frequency"),
    (("Soc", "SOC"),           "SOC"),
]


def score(field):
    """+1 where the converter matches what the control's name claims, -1 where it contradicts it."""
    name, conv = field["name"], field["converter"]
    total = 0
    for tokens, expected in AGREEMENT:
        if any(tok in name for tok in tokens):
            total += 1 if expected in conv else -1
    return total


RAW: dict = {}


def fields(body):
    """Collect every (control, words, converter).

    A control is often assigned in several branches of a memory-map version test. SP LINK writes
    the highest-version branch first and falls back down the chain, so the FIRST assignment that
    references the register block is the current layout. Debug and "hide this reading" branches
    assign no register and are skipped automatically."""
    global RAW
    out, aliases = {}, {}
    for line in body:
        m = LOCAL.match(line)
        if m:
            aliases[m.group(1)] = m.group(2)
            continue
        m = ASSIGN.search(line)
        if not m:
            continue
        name, expr = aliases.get(m.group(1), m.group(1)), m.group(2)
        words = [int(x) for x in re.findall(r'ValueArray\[(\d+)\]', expr)]
        if not words:
            continue
        conv = re.search(r'mDataConvert\.fn(\w+)', expr)
        cand = {"name": name, "words": words, "converter": conv.group(1) if conv else "Raw"}
        out.setdefault(name, []).append(cand)
    RAW = out
    return [resolve(name, cands) for name, cands in out.items()]


FAMILY = re.compile(r'^(.*?)(\d+)$')


def resolve(name, cands):
    """Pick between branches SP LINK chooses at runtime.

    Name/converter agreement settles most of it. What is left is usually a numbered family --
    AcPowerKaco1..5 and the like -- laid out at a fixed stride, so the candidate that continues
    its siblings' arithmetic is the one in the same table as them.
    """
    best = max(score(c) for c in cands)
    cands = [c for c in cands if score(c) == best]
    if len(cands) == 1:
        return cands[0]

    m = FAMILY.match(name)
    if m:
        prefix, index = m.group(1), int(m.group(2))
        siblings = sorted((int(FAMILY.match(n).group(2)), c[0]["words"][0])
                          for n, c in RAW.items()
                          if len(c) == 1 and (mm := FAMILY.match(n)) and mm.group(1) == prefix)
        if len(siblings) >= 2:
            (i0, w0), (i1, w1) = siblings[0], siblings[1]
            stride = (w1 - w0) // (i1 - i0) if i1 != i0 else 0
            if stride:
                predicted = w0 + stride * (index - i0)
                for c in cands:
                    if c["words"][0] == predicted:
                        return c
    return cands[0]


def csharp_name(control):
    s = re.sub(r'^(tb|lbl)', '', control)
    return s[0].upper() + s[1:] if s else control


lines = SRC.read_text(encoding="utf-8", errors="replace").split("\n")
found = {name: fields(body) for name, body in routines(lines)}

rows = []
for routine, (block, addr, count) in BLOCKS.items():
    fs = [f for f in found.get(routine, []) if max(f["words"]) < count]
    dropped = len(found.get(routine, [])) - len(fs)
    if dropped:
        print(f"  {block}: dropped {dropped} field(s) indexing past {count} words", file=sys.stderr)
    fs += [{"name": n, "words": w, "converter": c} for n, w, c in EXTRA.get(block, [])]
    rows.append((block, addr, count, fs))

parts = ["""// <auto-generated/>
// Generated by tools/gen_display_blocks.py from SP LINK's display routines. Do not edit by hand.
#nullable enable
namespace SpLink.Protocol;

/// <summary>
/// Every register block SP LINK displays, with the word indices and converter each field uses.
/// Lifted mechanically from the decompiled source so the mapping is reproducible rather than
/// transcribed, and so a field nobody has decoded yet still carries its converter name.
/// </summary>
public static class SpProDisplayBlocks
{
    public sealed record Field(string Name, int[] Words, string Converter);

    public sealed record Block(string Name, uint Address, int WordCount, Field[] Fields);

    public static readonly Block[] All =
    ["""]
for block, addr, count, fs in rows:
    parts.append(f'        new("{block}", {addr}, {count},')
    parts.append("        [")
    for f in fs:
        words = ", ".join(str(x) for x in f["words"])
        parts.append(f'            new("{csharp_name(f["name"])}", [{words}], "{f["converter"]}"),')
    parts.append("        ]),")
parts.append("    ];")

CONV = Path("reference/decompiled/SP_LINK/SP_LINK/mDataConvert.cs").read_text(
    encoding="utf-8", errors="replace").split("\n")
start = next(i for i, l in enumerate(CONV) if "fnConvertKacoModelNumberToString(" in l and "internal static" in l)
depth, models = 0, []
for j in range(start, len(CONV)):
    depth += CONV[j].count("{") - CONV[j].count("}")
    m = re.search(r'case (\d+):', CONV[j])
    if m:
        nxt = re.search(r'return "([^"]+)"', CONV[j + 1] if j + 1 < len(CONV) else "")
        if nxt:
            models.append((int(m.group(1)), nxt.group(1)))
    if j > start and depth <= 0:
        break

parts.append("")
parts.append("    /// <summary>AC-coupled inverter model codes, as SP LINK names them.</summary>")
parts.append("    public static string? KacoModelName(ushort code) => code switch")
parts.append("    {")
for code, name in sorted(dict(models).items()):
    parts.append(f'        {code} => "{name}",')
parts.append("        0 => null,")
parts.append("        _ => null,")
parts.append("    };")
parts.append("}")
OUT.write_text("\n".join(parts) + "\n")

total = sum(len(f) for _, _, _, f in rows)
print(f"{len(rows)} blocks, {total} fields -> {OUT}", file=sys.stderr)
