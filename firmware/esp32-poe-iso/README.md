# ESP32-POE-ISO serial → MQTT bridge

A small firmware that reads the SP PRO's fast register window over a UART and publishes it to
MQTT with Home Assistant discovery. That is all it does.

## Why this board

On a Raspberry Pi 3 the Ethernet adapter and the SP PRO's serial adapter hang off the same
single USB controller, while the SD card does not. When that bus wedges, the inverter link and
the network die together and the OS carries on regardless — which is how the host bridge went
silent for 19 hours on 2026-10-09 with nothing in the logs to show for it.

On an [ESP32-POE-ISO](https://www.olimex.com/Products/IoT/ESP32/ESP32-POE-ISO/) the Ethernet
PHY is on RMII and the inverter is on a UART. They share no bus, so that failure mode does not
exist. PoE removes the separate supply, which is the usual cause of the undervoltage and card
corruption that plague Pi deployments, and the `-ISO` galvanic isolation is worth having with a
serial line running next to switching power electronics.

**Buy a WROVER variant** (`-WROVER-E` / `-WROVER-IE`): 8 MB PSRAM rather than bare SRAM. The
firmware as written fits comfortably either way, but the headroom is nearly free at purchase
and impossible to add later.

## What it deliberately does not do

No HTTP server, no Prometheus endpoint, no log backfill, no configuration dump, no block sweep.
Those stay in `splink` on a host, which has the memory and a filesystem for them. This is a
sensor feed, not a second implementation of the whole tool.

**Read-only, with one unavoidable exception.** The SP PRO will not answer register reads until
a challenge-response handshake is satisfied, and the answer must be written to a register. That
write is the only one this firmware performs. `sppro_build_write` exists solely to serve it, and
nothing else calls it. Keep it that way.

## Status

| part | state |
| --- | --- |
| CRC, framing, response validation | **tested on host** |
| Fast-window decode and scaling | **tested on host against a live capture** |
| State-name tables | **tested on host**, string-for-string against the C# |
| UART link layer, login handshake | written, **never run** |
| Ethernet, MQTT, poll loop | written, **never run, never compiled** |

There is no ESP-IDF toolchain on the machine this was written on, so everything above the
protocol layer is unbuilt. Expect to fix compile errors on first build. The protocol layer is a
different matter: `make -C test run` compiles it with `-Werror` and checks it against vectors
taken from a live SPMC482 alongside what `splink` decoded from the same registers at the same
moment, so the two agree by test rather than by hope.

## Before you wire anything

**The open hardware question.** The SP PRO's configuration exposes `Port1Baudrate` and
`Port2Baudrate`, so UART-level ports exist and the native protocol is a UART protocol — `splink`
already reaches it through an Ethernet serial adapter, not only over USB. What is *not*
established is the physical connector and signalling level on this unit:

- **RS232** (±12 V) needs a MAX3232 between the SP PRO and the ESP32
- **RS485** needs a transceiver
- **TTL 3.3 V** connects directly

Connecting an RS232 line straight to an ESP32 GPIO will destroy the pin. Establish which it is
first. If it turns out the native protocol is reachable only over the USB port, this board
cannot do the job at all — a classic ESP32 has no USB host.

**Verify the pin assignments** in `main/Kconfig.projbuild` against the board schematic. The
Ethernet defaults (MDC 23, MDIO 18, clock out 17, PHY power 12, PHY address 0) are the usual
ESP32-POE values, and the UART defaults (TX 4, RX 36) are the UEXT connector. A UART pin that
collides with the PHY will take the network down rather than fail obviously.

## Build

```sh
idf.py set-target esp32
idf.py menuconfig          # "SP PRO bridge": broker, credentials, pins, poll interval
idf.py build flash monitor
```

Host tests, which need no toolchain:

```sh
make -C test run
```

## Entities

Publishes to `solar-bridge/selectronic/<name>/state_fast`, retained and only when a value
changes, with eight sensors announced over Home Assistant discovery: AC load power, inverter AC
power, battery SoC, battery current, DC current, charger state, inverter mode, AC source status.

The entity prefix defaults to `esp_sp_pro`, distinct from the host bridge's `sp_pro`, so the two
can run side by side and be compared rather than quietly fighting over one set of entities.
Point it at the host's prefix only when this board is taking over for good — and note that
Home Assistant keys history on `unique_id`, so that switch adopts the existing history rather
than starting fresh.

Battery SoC publishes `null` rather than a number when the inverter reports `0xFFFF`, so Home
Assistant shows unknown instead of a fabricated zero.
