#!/usr/bin/env python3
"""Record every register word the bridge can reach, so unnamed ones can be identified later.

About a hundred populated words sit inside blocks SP LINK reads but never labels, and a short
sample cannot tell a slowly-rising counter from a duplicate of something already decoded. This
writes the raw words periodically so they can be correlated against the decoded quantities over
days rather than minutes.

Every word is logged, not just the unnamed ones: identifying an unknown means correlating it
against known values from the same instant, and that only works if both are in the same record.

It takes the words from the bridge's own periodic sweep rather than reading the inverter
itself. The bridge already reads all fifteen blocks on a timer and keeps the raw words, so
asking it again over /raw was reading the same 945 registers a second time -- which made this
the single largest consumer of inverter traffic. Records therefore appear at the bridge's sweep
cadence, and each is stamped with the moment the bridge actually read the registers rather than
the moment we noticed.

One gzipped JSON-lines file per day:

    {"at": "...", "blocks": {"Now": [...], "Technical": [...], ...}}

At 945 words a quarter-hour that is well under a hundred KB a day compressed.
"""
import datetime
import gzip
import json
import os
import signal
import sys
import time
import urllib.request

BRIDGE = os.environ.get("BRIDGE_URL", "http://localhost:8080")
OUT_DIR = os.environ.get("BLOCK_LOG_DIR", "/var/lib/splink/blocks")
# How often to ask the bridge whether it has swept again. This costs one live reading, not a
# sweep, so it can be more frequent than the sweep itself without adding register traffic of
# any consequence. Records are written at the sweep cadence regardless.
INTERVAL = int(os.environ.get("BLOCK_LOG_INTERVAL", "300"))
KEEP_DAYS = int(os.environ.get("BLOCK_LOG_KEEP_DAYS", "120"))

running = True


def fetch(path, timeout=90):
    req = urllib.request.Request(f"{BRIDGE}{path}", headers={"User-Agent": "splink-block-logger"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read())


def sweep():
    """The bridge's most recent block sweep: when it was read, and the raw words.

    Returns (read_at, {block: [words]}). read_at is the bridge's own timestamp for the sweep,
    so consecutive polls that see the same sweep can be collapsed rather than logged twice.
    """
    sp = fetch("/sppro?blocks=true")
    blocks = sp.get("blocks") or {}
    return sp.get("blocks_read_at"), {name: blk["raw"] for name, blk in blocks.items() if blk.get("raw")}


def prune():
    cutoff = datetime.date.today() - datetime.timedelta(days=KEEP_DAYS)
    for name in os.listdir(OUT_DIR):
        if not name.startswith("blocks-") or not name.endswith(".jsonl.gz"):
            continue
        try:
            day = datetime.date.fromisoformat(name[7:17])
        except ValueError:
            continue
        if day < cutoff:
            os.remove(os.path.join(OUT_DIR, name))
            print(f"pruned {name}", flush=True)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)

    def stop(*_):
        global running
        running = False
    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)

    last_sweep, last_prune, announced = None, 0.0, False
    while running:
        try:
            read_at, blocks = sweep()
            if not announced and blocks:
                print(f"logging {len(blocks)} blocks, {sum(len(w) for w in blocks.values())} words, "
                      f"at the bridge's sweep cadence; checking every {INTERVAL}s", flush=True)
                announced = True

            if blocks and read_at and read_at != last_sweep:
                record = {"at": read_at, "blocks": blocks}
                path = os.path.join(OUT_DIR, f"blocks-{datetime.date.today().isoformat()}.jsonl.gz")
                with gzip.open(path, "at", encoding="utf-8") as f:
                    f.write(json.dumps(record, separators=(",", ":")) + "\n")
                last_sweep = read_at

            if time.time() - last_prune > 86400:
                prune()
                last_prune = time.time()
        except Exception as e:
            print(f"sample failed: {e}", file=sys.stderr, flush=True)

        for _ in range(INTERVAL):
            if not running:
                break
            time.sleep(1)
    return 0


if __name__ == "__main__":
    sys.exit(main())
