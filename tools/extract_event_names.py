"""Recover the event / mode / status label tables used when rendering logged records."""
import re, json, pathlib

SRC = pathlib.Path("reference/decompiled/SP_LINK/SP_LINK/mDataConvert.cs")
lines = SRC.read_text().splitlines()
WANT = {
    "fnConvertOperationalEventValueToString": "OperationalEvent",
    "fnConvertAlertEventValueToString": "AlertEvent",
    "fnConvertInverterModeValueToString": "OperationalMode",
    "fnConvertChargerStatusValueToString": "BatteryChargeMode",
    "fnConvertContactorStateValueToString": "ContactorState",
    "fnConvertGeneratorStatusValueToString": "GeneratorStatus",
    "fnConvertGeneratorStartedBySlashRunningReasonValueToString": "GeneratorReason",
}
ARM = re.compile(r'^\s*(?P<val>\d+)u?\s*=>\s*"(?P<label>[^"]*)",?\s*$')
CASE = re.compile(r'^\s*case (?P<val>\d+)u?:\s*$')
ASSIGN = re.compile(r'^\s*(?:\w+ = )?"(?P<label>[^"]*)";?\s*$')
RET = re.compile(r'^\s*return "(?P<label>[^"]*)";\s*$')

out = {}
for fn, name in WANT.items():
    start = next((i for i, l in enumerate(lines) if f" {fn}(" in l), None)
    if start is None:
        continue
    table, pending = {}, []
    i = start + 1
    while i < len(lines) and lines[i] != "\t}":
        line = lines[i]
        a = ARM.match(line)
        if a:
            table[int(a.group("val"))] = a.group("label")
            i += 1
            continue
        c = CASE.match(line)
        if c:
            pending.append(int(c.group("val")))
            i += 1
            continue
        if pending:
            m = RET.match(line) or ASSIGN.match(line)
            if m:
                for v in pending:
                    table[v] = m.group("label")
                pending = []
        i += 1
    if table:
        out[name] = dict(sorted(table.items()))

pathlib.Path("tools/event_names.json").write_text(json.dumps(out, indent=1))
for n, t in out.items():
    print(f"  {n:20s} {len(t):4d} labels   e.g. " + ", ".join(f"{k}={v}" for k, v in list(t.items())[:3]))
