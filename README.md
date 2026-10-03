# splink

A cross-platform, **read-only** bridge for Selectronic SP PRO inverters, with optional
Fronius Solar API aggregation. Speaks the SP PRO's native serial protocol directly, so it
runs anywhere .NET runs — no Windows, no SP LINK installation.

Built because SP LINK, the vendor's configuration and monitoring tool, is Windows-only.

## What it does

| | |
| --- | --- |
| **Live data** | battery SoC, voltage, current and power; AC load, voltage and frequency; charger and generator state |
| **Configuration** | all 563 settings, decoded with the vendor's own names and labels |
| **Logged data** | the four on-device logs — alert events, operational events, daily summary and detailed — decoded to engineering units |
| **Clock** | read the inverter's real-time clock and report drift |
| **Fronius** | aggregate any number of Fronius inverters over the Solar API, including per-MPPT DC strings |
| **Serving** | a read-only JSON API and Prometheus `/metrics`, or MQTT discovery for Home Assistant |

### Read-only by design

This talks to live power equipment. `SpProClient` refuses every write unless explicitly
constructed with `allowWrites: true`, and the CLI requires `--allow-writes`. The only
exceptions are two connection-lifecycle registers — the login challenge response and the
disconnect notification — which are volatile protocol state rather than device settings.

`splink serve` can never write. Configuration writes and firmware update are **not
implemented and not planned**: the risk of bricking an inverter or corrupting battery
charge parameters is not worth it.

## Quick start

```sh
splink ports                                   # find the inverter
splink probe   --port auto --verbose           # connect, log in, show the clock
splink now     --port auto                     # live data
splink config  --port auto --json              # all 563 settings
splink logs    --port auto                     # what logged data exists
splink download --port auto --log daily        # logged records as CSV
splink serve   --port auto --fronius a=10.0.0.1,b=10.0.0.2
```

Connect over USB/serial (`--port`, with auto-baud), an Ethernet serial adaptor
(`--host`/`--tcp-port`), or remotely through a select.live gateway (`--select-live`).
`--simulate` runs everything against a built-in simulated inverter, no hardware required.

## Protocol notes

Recovered by reverse engineering for interoperability, then verified against hardware.
These are facts about the wire format, not vendor source.

- **Framing** — `op` (`Q` = 0x51 read, `W` = 0x57 write), `wordCount - 1`, a 32-bit
  little-endian register address, then CRC-16/KERMIT over those six bytes. Writes append
  the data words plus a CRC over the data. Responses echo the 8-byte header, then data and
  CRC; a write response echoes the request. The receiver resynchronises on the next
  `Q`/`W` byte.
- **Serial** — 8N1, no flow control, RTS and DTR asserted. Auto-baud tries 57600, 115200,
  230400, 128000, 9600, 2400, 1200, 4800, 19200, 38400.
- **Login** — read the link-port register; `0xFFFF` means a login is required. Read a
  16-byte challenge, reply with `MD5(challenge ‖ password padded to 32 ASCII bytes)` as
  eight words, then read the result register, which is `1` on success.
- **Analogue scaling** — raw counts times a model-specific factor over 327680. The factors
  are read from the device, not assumed.
- **Timestamps** — seconds since **2001-01-01**, low word first.
- **Logged data** — four circular logs, each with its own register block. Entry size and
  sector layout are read from the device. Paging walks *backwards* from the newest record,
  wrapping between sectors. Records carry their own scale factors in trailing words.

## Architecture

```
src/SpLink.Protocol    framing, CRC, login, codecs, transports, Fronius client, simulator
src/SpLink.Cli         the splink command
tests/                 82 tests, including a simulated inverter and a fake Fronius
tools/                 scripts that mine register maps and label tables (see below)
deploy/                provisioning for a Raspberry Pi bridge
```

The register map, the 30 configuration enum tables and the 733 event labels are **not
hand-transcribed**. `tools/extract_*.py` mine them and generate the `*.g.cs` files, so they
can be regenerated against a different firmware revision rather than maintained by hand.
The tools expect a local decompilation, which is deliberately not distributed here.

## Status

Validated against an SP PRO SPMC482 and two Fronius inverters: connection and auto-baud,
the MD5 login, live data, the clock, all 563 configuration settings, and all four logged-data
types. The Fronius scaling cross-checks against the SP PRO's independent measurement of the
same power to within 1%.

## Legal

An independent interoperability project. Not affiliated with, endorsed by, or supported by
Selectronic Australia Pty Ltd or Fronius International GmbH. "SP PRO", "SP LINK" and
"Fronius" are the trademarks of their respective owners.

The protocol was determined by reverse engineering a lawfully obtained copy of the vendor's
software, for the sole purpose of interoperating with hardware the author owns. No vendor
source code is reproduced in this repository.

Use at your own risk. This software reads from power equipment that people depend on.

## Licence

MIT — see [LICENSE](LICENSE).
