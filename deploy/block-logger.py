#!/usr/bin/env python3
"""Record every register word the bridge can reach, so unnamed ones can be identified later.

About a hundred populated words sit inside blocks SP LINK reads but never labels, and a short
sample cannot tell a slowly-rising counter from a duplicate of something already decoded. This
writes the raw words periodically so they can be correlated against the decoded quantities over
days rather than minutes.

Every word is logged, not just the unnamed ones: identifying an unknown means correlating it
against known values from the same instant, and that only works if both are in the same record.

One gzipped JSON-lines file per day:

    {"at": "...", "blocks": {"Now": [...], "Technical": [...], ...}}

At 945 words every five minutes that is a few hundred KB a day compressed.
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
INTERVAL = int(os.environ.get("BLOCK_LOG_INTERVAL", "300"))
KEEP_DAYS = int(os.environ.get("BLOCK_LOG_KEEP_DAYS", "120"))

running = True


def fetch(path, timeout=90):
    req = urllib.request.Request(f"{BRIDGE}{path}", headers={"User-Agent": "splink-block-logger"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read())


def block_map():
    """Address and length of every block, taken from the bridge rather than hardcoded."""
    sp = fetch("/sppro?blocks=true").get("blocks") or {}
    return {name: (blk["address"], blk["words"]) for name, blk in sp.items()}


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

    blocks, last_prune = None, 0.0
    while running:
        try:
            if blocks is None:
                blocks = block_map()
                print(f"logging {len(blocks)} blocks, "
                      f"{sum(n for _, n in blocks.values())} words, every {INTERVAL}s", flush=True)

            record = {"at": datetime.datetime.now().astimezone().isoformat(), "blocks": {}}
            for name, (address, words) in blocks.items():
                if words < 1:
                    continue
                try:
                    record["blocks"][name] = fetch(f"/raw?address={address}&words={words}")["value"]
                except Exception as e:
                    # One unreadable block must not cost us the rest of the sample.
                    print(f"{name}: {e}", file=sys.stderr, flush=True)

            if record["blocks"]:
                path = os.path.join(OUT_DIR, f"blocks-{datetime.date.today().isoformat()}.jsonl.gz")
                with gzip.open(path, "at", encoding="utf-8") as f:
                    f.write(json.dumps(record, separators=(",", ":")) + "\n")

            if time.time() - last_prune > 86400:
                prune()
                last_prune = time.time()
        except Exception as e:
            print(f"sample failed: {e}", file=sys.stderr, flush=True)
            blocks = None   # re-read the layout in case the bridge restarted

        for _ in range(INTERVAL):
            if not running:
                break
            time.sleep(1)
    return 0


if __name__ == "__main__":
    sys.exit(main())
