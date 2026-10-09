#!/usr/bin/env bash
# Provision a Raspberry Pi (or any Debian host) as an SP PRO bridge.
#
# Installs the self-contained .NET build, a Cloudflare tunnel client for reaching an MQTT
# broker that is not directly routable, and systemd units for both. Idempotent.
#
# Everything here is read-only with respect to the inverters.
#
#   sudo ./provision.sh
#
# Expects, alongside this script:
#   splink/        the published linux-arm64 (or linux-x64) build
#   bridge.json    which Fronius inverters to poll
#   mqtt-publish.py
#   watchdog.py, splink-watchdog.service
set -euo pipefail

SERIAL_PORT="${SERIAL_PORT:-auto}"        # 'auto' picks the sole candidate tty
HTTP_PORT="${HTTP_PORT:-8080}"
# Must exceed the polling interval of whatever consumes the API, or the SP PRO link is torn
# down and re-dialled (auto-baud plus login handshake) between every request.
IDLE_TIMEOUT="${IDLE_TIMEOUT:-300}"
CONFIG_FILE=/etc/splink/bridge.json
HERE="$(cd "$(dirname "$0")" && pwd)"

say() { printf '\n\033[1m== %s\033[0m\n' "$*"; }

say "Base packages"
apt-get update -qq
DEBIAN_FRONTEND=noninteractive apt-get install -y -qq curl ca-certificates python3-paho-mqtt >/dev/null

say "Service account"
# Dedicated account; dialout grants access to the SP PRO's USB CDC port and nothing else.
if ! id splink >/dev/null 2>&1; then
    useradd --system --home-dir /opt/splink --shell /usr/sbin/nologin --groups dialout splink
fi
install -d -o splink -g splink /opt/splink
# 750 root:splink - a 640 file inside a 700 directory is still unreachable to the service.
install -d -m750 -o root -g splink /etc/splink
install -m644 "$HERE/bridge.json" "$CONFIG_FILE"

say "Bridge"
rm -rf /opt/splink/app
mkdir -p /opt/splink/app
cp -a "$HERE/splink/." /opt/splink/app/
install -m755 "$HERE/mqtt-publish.py" /opt/splink/mqtt-publish.py
install -m755 "$HERE/watchdog.py" /opt/splink/watchdog.py
chown -R splink:splink /opt/splink
chmod +x /opt/splink/app/splink

cat > /etc/systemd/system/splink.service <<UNIT
[Unit]
Description=SP PRO read-only bridge (JSON API and Prometheus metrics)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=splink
Group=splink
SupplementaryGroups=dialout
WorkingDirectory=/opt/splink/app
# ProtectSystem=strict leaves no writable HOME, which the .NET runtime expects.
Environment=HOME=/tmp
Environment=DOTNET_CLI_HOME=/tmp
Environment=DOTNET_NOLOGO=1
ExecStart=/opt/splink/app/splink serve --port ${SERIAL_PORT} --http-port ${HTTP_PORT} --config ${CONFIG_FILE} --idle-timeout ${IDLE_TIMEOUT}
Restart=always
RestartSec=10s
NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true
ProtectSystem=strict
ProtectKernelTunables=true
ProtectControlGroups=true
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX
MemoryMax=400M

[Install]
WantedBy=multi-user.target
UNIT

say "Watchdog"
# Ethernet on a Pi 3 shares its single USB controller with the SP PRO's serial adapter, while
# the SD card does not -- so a USB wedge takes out the inverter link and the network together
# and leaves the OS running happily, with nothing to recover it. This caught that case.
install -m644 "$HERE/splink-watchdog.service" /etc/systemd/system/splink-watchdog.service
install -d -o splink -g splink /var/lib/splink/diagnostics
# The publisher's heartbeat lives on tmpfs so it can be written every cycle without wearing
# the card; the watchdog reads it to tell "publishing" from "running but achieving nothing".
mkdir -p /etc/systemd/system/mqtt-publish.service.d
printf '[Service]\nRuntimeDirectory=splink\nRuntimeDirectoryPreserve=yes\n' \
  > /etc/systemd/system/mqtt-publish.service.d/10-runtime-dir.conf

systemctl daemon-reload
systemctl enable splink
systemctl enable splink-watchdog
systemctl restart splink

say "Verifying"
sleep 8
systemctl --no-pager --lines=5 status splink || true
echo
if curl -fsS -m 20 "http://localhost:${HTTP_PORT}/health"; then
    echo "  bridge is answering"
else
    echo "  bridge did NOT answer; see: journalctl -u splink -n 40"
fi
echo
cat <<'NEXT'

Optional: publishing to Home Assistant over MQTT
  See deploy/README.md. If the broker is not directly routable (for example it sits behind
  Home Assistant's own Cloudflare tunnel), mqtt-tunnel.service presents it on localhost and
  mqtt-publish.service does the publishing.
NEXT
