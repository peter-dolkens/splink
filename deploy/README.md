# Deploying as a bridge appliance

Runs the bridge on a small always-on machine next to the inverter. Developed on a
Raspberry Pi 3B; anything that runs .NET and has a USB or serial port will do.

## Build and install

```sh
dotnet publish src/SpLink.Cli -c Release -r linux-arm64 --self-contained true \
    -p:PublishSingleFile=false -o deploy/splink
sudo ./deploy/provision.sh
```

Self-contained, so the target needs no .NET runtime — useful on a 1 GB board.
Use `-r linux-x64` or `linux-arm` as appropriate.

`provision.sh` creates an unprivileged `splink` account (in `dialout`, purely to open the
serial port), installs a sandboxed systemd unit, and verifies the endpoint answers.

Edit `bridge.json` for your Fronius inverters, or drop it if you have none.

## Why `--port auto`

Serial device numbering can shift across reboots. `auto` resolves the port by discovery at
start-up and fails loudly if it finds none or several. Pin a `/dev/serial/by-id/` path
instead if the machine has other serial hardware.

## Why `--idle-timeout` must exceed your poll interval

The SP PRO admits a single session, and establishing one costs an auto-baud scan plus the
login handshake. The bridge connects lazily on the first request and releases the link
after the idle timeout — which also frees the port for SP LINK or any other tool.

Set the timeout *below* your poll interval and the link is torn down and re-dialled between
every single request. The default of 300s suits a 60s poll.

## Home Assistant over MQTT

`mqtt-publish.py` publishes MQTT discovery, creating proper devices with per-entity
availability driven by a retained last-will. It reads the bridge over localhost, so the
publishing path does not depend on the JSON API being reachable from outside.

**If you adapt this, keep the `on_connect` handler.** When the broker link drops, the broker
publishes the retained last-will and every entity goes unavailable — correct behaviour. The
client reconnects by itself, but unless something republishes `online` afterwards the
entities stay unavailable while the publisher cheerfully pushes state nobody is listening to.
Re-announcing on every connect, rather than only at start-up, is what makes that recoverable.

The same applies to the publish-on-change behaviour below: because an unchanged topic is
never resent, a reconnect must clear the cache or the first post-reconnect state could be
many minutes away. State is published retained for the same reason, so a subscriber
attaching late gets current values rather than silence.

If the broker is not directly routable — for instance it sits behind Home Assistant's own
Cloudflare tunnel — run a tunnel client alongside it to present the broker locally:

```ini
# /etc/systemd/system/mqtt-tunnel.service
[Service]
EnvironmentFile=/etc/splink/mqtt-tunnel.env
ExecStart=/usr/bin/cloudflared access tcp --hostname mqtt.example.com --url 127.0.0.1:1883 \
    --service-token-id ${TOKEN_ID} --service-token-secret ${TOKEN_SECRET}
Restart=always
```

Gate that hostname with an access policy. A broker reachable by anyone who knows the name
is a far larger exposure than a read-only JSON endpoint.

Credentials live in `/etc/splink/mqtt.env` (mode 640, group `splink`):

```
MQTT_HOST=127.0.0.1
MQTT_PORT=1883
MQTT_USERNAME=solar-bridge
MQTT_PASSWORD=...
```

Use a dedicated broker account rather than Home Assistant's own, so a compromised bridge
does not hand over Home Assistant's credentials.

## Metered connections

If the site is on mobile data, note that most of the avoidable traffic turns out to be
*clocks*. Timestamps change on every read by definition, so they defeat change detection
and force a publish every cycle. The publisher therefore strips the inverter clocks and its
own `taken_at`, quantises floats to each quantity's real resolution, and skips byte-identical
payloads — with a forced republish every 15 minutes so retained values cannot go stale.

Overnight, with solar at zero, the Fronius topics fall silent entirely.

What remains is the tunnel's own keepalive, which is the cost of being reachable at all.
