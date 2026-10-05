#!/usr/bin/env python3
"""Publish solar bridge readings to Home Assistant over MQTT discovery.

Runs on the Pi. Reads the bridge over localhost (no tunnel round trip) and publishes to the
Home Assistant broker, which is reached through a Cloudflare Access TCP tunnel presented on
127.0.0.1:1883 by the mqtt-tunnel service.

Discovery configs are retained so Home Assistant rebuilds the entities after a restart
without waiting for us. Availability uses a retained LWT, so if this process dies the
entities go unavailable immediately rather than silently holding their last value.
"""
import json, os, re, signal, sys, time, urllib.request
import paho.mqtt.client as mqtt

CONF = os.environ.get("MQTT_ENV", "/etc/splink/mqtt.env")
BRIDGE = os.environ.get("BRIDGE_URL", "http://localhost:8080/")
INTERVAL = int(os.environ.get("INTERVAL", "60"))
# Republish even when unchanged this often, so retained values stay fresh.
FORCE_EVERY = int(os.environ.get("FORCE_EVERY", "900"))
PREFIX = "solar-bridge"
DISCOVERY = "homeassistant"
SERIAL = os.environ.get("SPPRO_SERIAL", "unknown")  # set per installation

running = True
_last_sent: dict[str, str] = {}
_last_forced = 0.0


def load(path):
    conf = {}
    with open(path) as f:
        for line in f:
            line = line.strip()
            if line and not line.startswith("#") and "=" in line:
                k, v = line.split("=", 1)
                conf[k.strip()] = v.strip()
    return conf


def device(kind, slug=None, label=None):
    if kind == "sppro":
        return {"identifiers": [f"sppro_{SERIAL}"], "name": "SP PRO",
                "manufacturer": "Selectronic", "model": "SPMC482",
                "serial_number": SERIAL, "suggested_area": "Build Site"}
    return {"identifiers": [f"fronius_{slug}_buildsite"], "name": label,
            "manufacturer": "Fronius", "model": "Solar Inverter",
            "suggested_area": "Build Site"}


def sensor(obj_id, name, state_topic, tpl, dev, unit=None, dclass=None,
           sclass="measurement", icon=None, precision=None, extra=None):
    cfg = {
        "name": name,
        "object_id": obj_id,            # fixes the entity_id rather than deriving it
        "unique_id": f"solarbridge_{obj_id}",
        "state_topic": state_topic,
        "value_template": tpl,
        "device": dev,
        "availability": [{"topic": f"{PREFIX}/status"}],
    }
    if unit: cfg["unit_of_measurement"] = unit
    if dclass: cfg["device_class"] = dclass
    if sclass: cfg["state_class"] = sclass
    if icon: cfg["icon"] = icon
    # Unlike the rest platform, MQTT honours this, so full precision is recorded
    # while the UI stays readable.
    if precision is not None: cfg["suggested_display_precision"] = precision
    if extra: cfg.update(extra)
    return cfg


# Unit -> (device_class, state_class). Anything not listed is published without a class, which
# still records and graphs; it just gets no special handling in Home Assistant.
_UNIT_CLASS = {
    "kWh": ("energy", "total_increasing"),
    "kW":  ("power", "measurement"),
    "W":   ("power", "measurement"),
    "V":   ("voltage", "measurement"),
    "A":   ("current", "measurement"),
    "\u00b0C": ("temperature", "measurement"),
    "Hz":  ("frequency", "measurement"),
    "h":   ("duration", "total_increasing"),
    "%":   (None, "measurement"),
}


def slug(text):
    """CamelCase to snake_case, keeping acronyms intact: ACOutStatus -> ac_out_status."""
    s = re.sub(r"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", "_", text)
    s = re.sub(r"[^0-9A-Za-z]+", "_", s).lower()
    return re.sub(r"_+", "_", s).strip("_")


def block_discovery_configs(snapshot):
    """Build a sensor per decoded block field, from whatever the bridge is currently reporting.

    Deriving these from the live payload rather than a hardcoded list means a field added to the
    decoder shows up without touching this file. They are all diagnostic: there are a few hundred,
    most of them rarely interesting, and the point is to have them recorded so the useful ones can
    be identified and the rest dropped later.
    """
    out = []
    d = device("sppro")
    for block, blk in (snapshot.get("blocks") or {}).items():
        for field, spec in blk.get("fields", {}).items():
            if spec.get("value") is None:
                continue
            unit = spec.get("unit")
            dclass, sclass = _UNIT_CLASS.get(unit, (None, None))
            obj = f"sp_pro_{slug(block)}_{slug(field)}"[:60]
            cfg = {
                "name": f"{block} {field}",
                "object_id": obj,
                "unique_id": f"solarbridge_{obj}",
                "state_topic": f"{PREFIX}/blocks/state",
                # Flat "Block.Field" keys keep the template simple and avoid nesting surprises.
                "value_template": "{{ value_json['%s.%s'] }}" % (block, field),
                "device": d,
                "entity_category": "diagnostic",
                "availability": [{"topic": f"{PREFIX}/status"}],
            }
            if unit: cfg["unit_of_measurement"] = unit
            if dclass: cfg["device_class"] = dclass
            if sclass: cfg["state_class"] = sclass
            out.append((f"{DISCOVERY}/sensor/{obj}/config", cfg))
    return out


def flatten_blocks(snapshot):
    """The decoded values alone, as flat Block.Field keys."""
    out = {}
    for block, blk in (snapshot.get("blocks") or {}).items():
        for field, spec in blk.get("fields", {}).items():
            if spec.get("value") is not None:
                out[f"{block}.{field}"] = spec["value"]
    return out


def discovery_configs():
    out = []
    sp_topic = f"{PREFIX}/sppro/state"
    d = device("sppro")
    for obj, name, tpl, unit, dc, sc, icon, prec in [
        ("sp_pro_battery_soc", "Battery SoC", "{{ value_json.battery.soc_percent }}", "%", "battery", "measurement", None, 1),
        ("sp_pro_battery_voltage", "Battery Voltage", "{{ value_json.battery.volts }}", "V", "voltage", "measurement", None, 2),
        ("sp_pro_battery_current", "Battery Current", "{{ value_json.battery.amps }}", "A", "current", "measurement", None, 2),
        ("sp_pro_battery_power", "Battery Power", "{{ value_json.battery.kilowatts * 1000 }}", "W", "power", "measurement", None, 0),
        ("sp_pro_ac_load_power", "AC Load Power", "{{ value_json.ac.load_kilowatts * 1000 }}", "W", "power", "measurement", None, 0),
        ("sp_pro_ac_voltage", "AC Voltage", "{{ value_json.ac.volts }}", "V", "voltage", "measurement", None, 1),
        ("sp_pro_ac_frequency", "AC Frequency", "{{ value_json.ac.frequency_hz }}", "Hz", "frequency", "measurement", None, 2),
        ("sp_pro_charger_state", "Charger State", "{{ value_json.charger }}", None, None, None, "mdi:battery-charging", None),
        ("sp_pro_generator_state", "Generator State", "{{ value_json.generator.status }}", None, None, None, "mdi:engine", None),
        ("sp_pro_solar_total_power", "Solar Total Power", "{{ value_json.solar_total_watts }}", "W", "power", "measurement", None, 0),
        # The SP PRO's own measurement of AC-coupled solar. It duplicates what the Fronius
        # report over their own network on purpose: two independent paths to the same number
        # make a disagreement visible instead of silent.
        ("sp_pro_ac_coupled_power", "AC Coupled Solar Power",
         "{{ value_json.ac_coupled.kilowatts * 1000 }}", "W", "power", "measurement", None, 0),
        ("sp_pro_ac_coupled_power_1", "AC Coupled Solar Power 1",
         "{{ value_json.ac_coupled.per_inverter[0] * 1000 }}", "W", "power", "measurement", None, 0),
        ("sp_pro_ac_coupled_power_2", "AC Coupled Solar Power 2",
         "{{ value_json.ac_coupled.per_inverter[1] * 1000 }}", "W", "power", "measurement", None, 0),
        ("sp_pro_inverter_power", "Inverter AC Power",
         "{{ value_json.inverter.kilowatts * 1000 }}", "W", "power", "measurement", None, 0),
        ("sp_pro_inverter_mode", "Inverter Mode", "{{ value_json.inverter.mode }}", None, None, None, "mdi:sine-wave", None),
        ("sp_pro_ac_source_status", "AC Source Status", "{{ value_json.inverter.ac_source_status }}", None, None, None, "mdi:transmission-tower", None),
        ("sp_pro_battery_load_5min", "Battery Load 5 min",
         "{{ value_json.battery_trend.load_5min_kilowatts * 1000 }}", "W", "power", "measurement", None, 0),
        ("sp_pro_battery_load_15min", "Battery Load 15 min",
         "{{ value_json.battery_trend.load_15min_kilowatts * 1000 }}", "W", "power", "measurement", None, 0),
        # Daily accumulators. They reset at midnight, so total_increasing is the right class:
        # Home Assistant then treats the reset as a new cycle rather than a negative spike.
        ("sp_pro_today_ac_coupled_energy", "Solar Energy Today",
         "{{ value_json.today.ac_coupled_kwh }}", "kWh", "energy", "total_increasing", None, 3),
        ("sp_pro_today_ac_load_energy", "Load Energy Today",
         "{{ value_json.today.ac_load_kwh }}", "kWh", "energy", "total_increasing", None, 3),
        ("sp_pro_today_battery_in_energy", "Battery In Today",
         "{{ value_json.today.battery_in_kwh }}", "kWh", "energy", "total_increasing", None, 3),
        ("sp_pro_today_battery_out_energy", "Battery Out Today",
         "{{ value_json.today.battery_out_kwh }}", "kWh", "energy", "total_increasing", None, 3),
        ("sp_pro_today_ac_coupled_peak", "Solar Peak Today",
         "{{ value_json.today.ac_coupled_peak_kilowatts * 1000 }}", "W", "power", "measurement", None, 0),
        ("sp_pro_today_inverter_run_hours", "Inverter Run Hours Today",
         "{{ value_json.today.inverter_run_hours }}", "h", "duration", "total_increasing", None, 2),
        ("sp_pro_today_float_hours", "Float Hours Today",
         "{{ value_json.today.float_hours }}", "h", "duration", "total_increasing", None, 2),
    ]:
        out.append((f"{DISCOVERY}/sensor/{obj}/config",
                    sensor(obj, name, sp_topic, tpl, d, unit, dc, sc, icon, prec)))

    # The inverter's clock is compared against the bridge host's clock, so the figure is only
    # worth anything while that host clock is NTP-disciplined. When it is not, the bridge omits
    # the field entirely and the second availability rule takes this entity offline — better a
    # gap in the history than a number with nothing behind it. availability_mode must be "all":
    # the default ("latest") would let whichever message arrived last decide.
    drift_tpl = "{{ value_json.inverter_clock_drift_seconds }}"
    out.append((f"{DISCOVERY}/sensor/sp_pro_clock_drift/config",
                sensor("sp_pro_clock_drift", "Clock Offset vs Bridge", sp_topic, drift_tpl, d,
                       "s", None, "measurement", "mdi:clock-alert-outline", 1,
                       extra={
                           "availability_mode": "all",
                           "availability": [
                               {"topic": f"{PREFIX}/status"},
                               {"topic": sp_topic,
                                # "is defined" covers the field being dropped; "is not none"
                                # covers it being present and null. A genuine 0.0 s offset is
                                # falsy, so neither test may be a plain truthiness check.
                                "value_template":
                                    "{{ 'online' if value_json.inverter_clock_drift_seconds is defined"
                                    " and value_json.inverter_clock_drift_seconds is not none"
                                    " else 'offline' }}"},
                           ],
                       })))

    # Makes the reference itself observable, so "is the drift real?" is answerable from Home
    # Assistant rather than by trusting the bridge. Binary and near-static, so it costs no
    # meaningful traffic -- unlike the host's actual NTP offset, which jitters by tens of
    # milliseconds every probe. That stays in the JSON API for when it is actually wanted.
    out.append((f"{DISCOVERY}/binary_sensor/sp_pro_bridge_clock_synced/config", {
        "name": "Bridge Clock Synchronised",
        "object_id": "sp_pro_bridge_clock_synced",
        "unique_id": "solarbridge_sp_pro_bridge_clock_synced",
        "state_topic": sp_topic,
        # host_clock must be defaulted before it is indexed: attribute access on an undefined
        # raises, and a retained payload from an older bridge has no host_clock at all.
        "value_template": "{{ 'ON' if (value_json.host_clock | default({}, true)).synchronized"
                          " | default(false, true) else 'OFF' }}",
        "device_class": "connectivity",
        "entity_category": "diagnostic",
        "device": d,
        "availability": [{"topic": f"{PREFIX}/status"}],
    }))

    for slug, label in [("right", "Fronius Right"), ("left", "Fronius Left")]:
        t = f"{PREFIX}/fronius/{slug}/state"
        d = device("fronius", slug, label)
        base = [
            (f"fronius_{slug}_power", "Power", "{{ value_json.ac.power_watts }}", "W", "power", "measurement", 0),
            (f"fronius_{slug}_ac_voltage", "AC Voltage", "{{ value_json.ac.volts }}", "V", "voltage", "measurement", 1),
            (f"fronius_{slug}_energy_total", "Total Energy",
             "{{ (value_json.energy.total_wh | float / 1000) if value_json.energy.total_wh is not none else none }}",
             "kWh", "energy", "total_increasing", 3),
        ]
        for obj, name, tpl, unit, dc, sc, prec in base:
            out.append((f"{DISCOVERY}/sensor/{obj}/config",
                        sensor(obj, name, t, tpl, d, unit, dc, sc, precision=prec)))
        for n in (1, 2):
            # Under curtailment the inverter leaves its maximum power point: string voltage
            # climbs toward open circuit while current collapses. Both are needed to tell
            # curtailment apart from genuinely low sun.
            sel = ("{%% set s = value_json.strings | selectattr('index','eq',%d) | list %%}" % n)
            for field, fl, path, unit, dc, prec in [
                ("voltage", "Voltage", "volts", "V", "voltage", 1),
                ("current", "Current", "amps", "A", "current", 2),
                ("power", "Power", "watts", "W", "power", 0),
            ]:
                obj = f"fronius_{slug}_string{n}_{field}"
                tpl = sel + (" {{ s[0].%s if s else none }}" % path)
                out.append((f"{DISCOVERY}/sensor/{obj}/config",
                            sensor(obj, f"String {n} {fl}", t, tpl, d, unit, dc, "measurement", precision=prec)))
    return out


# Sensor jitter in the third decimal is noise, not signal: publishing it costs 5G data,
# defeats change detection and clutters history. Round to each quantity's useful resolution.
_PRECISION = {
    "volts": 1, "amps": 2, "watts": 1, "kilowatts": 3, "total_wh": 1, "day_wh": 1,
    "year_wh": 1, "apparent_va": 1, "power_watts": 1, "soc_percent": 2,
    "frequency_hz": 2, "solar_total_watts": 1, "inverter_clock_drift_seconds": 1,
}


def quantise(value, key=None):
    """Recursively round floats to the resolution that key actually carries."""
    if isinstance(value, dict):
        return {k: quantise(v, k) for k, v in value.items()}
    if isinstance(value, list):
        return [quantise(v, key) for v in value]
    if isinstance(value, float):
        return round(value, _PRECISION.get(key, 2))
    return value


def publish_if_changed(client, topic, payload, force):
    """Skip republishing an identical payload: on a metered 5G link that is pure cost.

    A periodic forced publish still happens so retained values survive a broker restart
    and never look indefinitely stale.
    """
    if not force and _last_sent.get(topic) == payload:
        return 0
    client.publish(topic, payload, retain=True)
    _last_sent[topic] = payload
    return 1


def main():
    conf = load(CONF)
    client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id="solar-bridge")
    client.username_pw_set(conf["MQTT_USERNAME"], conf["MQTT_PASSWORD"])
    client.will_set(f"{PREFIX}/status", "offline", retain=True)

    configs = discovery_configs()
    block_configs: list = []   # filled from the first snapshot; see the main loop

    def on_connect(_client, _userdata, _flags, reason, _props=None):
        """Re-announce on every connect, not just the first.

        A dropped link makes the broker publish our retained last-will, so Home Assistant
        sees "offline". paho reconnects by itself, but unless we say "online" again nothing
        ever clears that, and the entities stay unavailable while we happily publish state
        into the void. Discovery is re-sent too, in case the broker lost its retained set.
        """
        if reason != 0:
            print(f"connect failed: {reason}", file=sys.stderr, flush=True)
            return
        for topic, cfg in configs + block_configs:
            _client.publish(topic, json.dumps(cfg), retain=True)
        _client.publish(f"{PREFIX}/status", "online", retain=True)
        # Force the next cycle to publish state, so a reconnect does not wait for a change.
        _last_sent.clear()
        print(f"connected: re-announced {len(configs) + len(block_configs)} discovery configs", flush=True)

    client.on_connect = on_connect
    client.connect(conf.get("MQTT_HOST", "127.0.0.1"), int(conf.get("MQTT_PORT", "1883")), keepalive=60)
    client.loop_start()

    def stop(*_):
        global running
        running = False
    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)

    fails = published = skipped = 0
    last_report = time.time()
    while running:
        try:
            req = urllib.request.Request(BRIDGE, headers={"User-Agent": "solar-bridge-mqtt"})
            with urllib.request.urlopen(req, timeout=30) as r:
                snap = json.loads(r.read())
            global _last_forced
            force = (time.time() - _last_forced) >= FORCE_EVERY
            if force:
                _last_forced = time.time()

            # solar_total_watts lives at the top level but belongs to the SP PRO device's topic.
            sp = dict(snap.get("sp_pro") or {})
            sp["solar_total_watts"] = snap.get("solar_total_watts")
            # The raw clock ticks every read and would defeat change detection; the drift
            # figure it is derived from is published instead.
            sp.pop("inverter_clock", None)
            sp.pop("inverter_clock_read_at", None)
            sp.pop("taken_at", None)
            # Same reasoning for the clock reference: offset, root distance and the probe
            # timestamp move on every read, so carrying them would mean no payload ever
            # matches the last and change detection would never skip a publish. Only the
            # fields that actually change state are kept; the JSON API still has them all.
            # today_read_at advances on its own cadence and would defeat change detection for
            # the whole SP PRO payload; the values it timestamps are what matter.
            sp.pop("today_read_at", None)
            if isinstance(sp.get("host_clock"), dict):
                sp["host_clock"] = {k: v for k, v in sp["host_clock"].items()
                                    if k in ("synchronized", "source", "stratum")}

            # The block sweep lands on its own topic: a few hundred mostly-static diagnostic
            # values that would otherwise bloat every SP PRO publish and defeat change detection.
            # Blocks hang off sp_pro in the combined payload the bridge serves at "/".
            blocks = flatten_blocks(sp)
            if blocks and not block_configs:
                block_configs.extend(block_discovery_configs(sp))
                for topic, cfg in block_configs:
                    client.publish(topic, json.dumps(cfg), retain=True)
                print(f"announced {len(block_configs)} block sensors", flush=True)
            sp.pop("blocks", None)
            sp.pop("blocks_read_at", None)

            sent = publish_if_changed(client, f"{PREFIX}/sppro/state",
                                      json.dumps(quantise(sp), sort_keys=True), force)
            if blocks:
                sent += publish_if_changed(client, f"{PREFIX}/blocks/state",
                                           json.dumps(quantise(blocks), sort_keys=True), force)
            for f in snap.get("fronius") or []:
                g = {k: v for k, v in f.items() if k not in ("taken_at",)}
                # The inverter's own clock ticks every read. Like the SP PRO clock it carries
                # no information worth a publish, and leaving it in means the payload always
                # differs so nothing is ever deduplicated.
                if isinstance(g.get("status"), dict):
                    g["status"] = {k: v for k, v in g["status"].items() if k != "device_time"}
                sent += publish_if_changed(client, f"{PREFIX}/fronius/{f['name']}/state",
                                           json.dumps(quantise(g), sort_keys=True), force)
            skipped += 4 - sent
            published += sent
            if fails:
                print(f"bridge recovered after {fails} failures", flush=True)
                client.publish(f"{PREFIX}/status", "online", retain=True)
            fails = 0
        except Exception as e:
            fails += 1
            print(f"bridge read failed ({fails}): {e}", file=sys.stderr, flush=True)
            # Mark unavailable rather than leaving stale values looking live.
            if fails >= 2:
                client.publish(f"{PREFIX}/status", "offline", retain=True)
        if time.time() - last_report >= 3600:
            print(f"last hour: {published} publishes, {skipped} skipped as unchanged", flush=True)
            published = skipped = 0
            last_report = time.time()

        for _ in range(INTERVAL):
            if not running: break
            time.sleep(1)

    client.publish(f"{PREFIX}/status", "offline", retain=True)
    client.loop_stop()
    client.disconnect()
    return 0


if __name__ == "__main__":
    sys.exit(main())
