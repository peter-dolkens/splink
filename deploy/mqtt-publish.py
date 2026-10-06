#!/usr/bin/env python3
"""Publish solar bridge readings to Home Assistant over MQTT discovery.

Runs on the Pi, reads the bridge over localhost and publishes to the Home Assistant broker.

Topics are namespaced per device, so a second inverter can be added without colliding:

    solar-bridge/status                              bridge availability (retained LWT)
    solar-bridge/selectronic/<inverter>/state        fast-moving measurements
    solar-bridge/selectronic/<inverter>/state_60s    status and limits that rarely move
    solar-bridge/selectronic/<inverter>/state_300s   today's accumulators
    solar-bridge/selectronic/<inverter>/blocks       the full register sweep
    solar-bridge/fronius/<name>/state

A topic republishes whenever any field on it changes, so splitting by rate is what gives each
reading its own cadence: a slow value cannot drag the live topic along with it, and the live
topic carries only what is worth watching at full speed. Entities are mapped onto the topic
matching how often the underlying reading actually moves.

Discovery follows the layout zigbee2mqtt uses -- <component>/<device_id>/<object_id>/config --
so entities group under a stable device identifier rather than relying on a name prefix.

Everything site-specific lives in the config file: which inverter this is, the bucket
intervals, which entities to leave out, and whether to round anything on the way out.
"""
import fnmatch
import json
import os
import re
import signal
import sys
import time
import urllib.request

import paho.mqtt.client as mqtt

CONF = os.environ.get("MQTT_ENV", "/etc/splink/mqtt.env")
CONFIG = os.environ.get("MQTT_CONFIG", "/etc/splink/publisher.json")
# The bridge omits the block sweep unless asked, since it is most of the payload and the public
# feed rarely wants it. The block sensors depend on it, so request it explicitly.
BRIDGE = os.environ.get("BRIDGE_URL", "http://localhost:8080/?blocks=true")
INTERVAL = int(os.environ.get("INTERVAL", "60"))
# Republish even when unchanged this often, so retained values stay fresh.
FORCE_EVERY = int(os.environ.get("FORCE_EVERY", "900"))
PREFIX = "solar-bridge"
DISCOVERY = "homeassistant"

running = True
_last_sent: dict[str, str] = {}
_last_published: dict[str, float] = {}
_last_forced = 0.0
_credentials = ("", "")
_host, _port = "127.0.0.1", 1883


def load_env(path):
    conf = {}
    with open(path) as f:
        for line in f:
            line = line.strip()
            if line and not line.startswith("#") and "=" in line:
                k, v = line.split("=", 1)
                conf[k.strip()] = v.strip()
    return conf


def load_config(path):
    """Site configuration. Every key is optional and the defaults suit a single SP PRO."""
    cfg = {}
    try:
        with open(path) as f:
            cfg = json.load(f)
    except FileNotFoundError:
        print(f"no {path}; using defaults", flush=True)
    except (OSError, ValueError) as e:
        print(f"config {path}: {e}; using defaults", file=sys.stderr, flush=True)
    cfg.setdefault("inverter_name", "sppro")
    # Prefixes object_id and unique_id for this inverter's entities. Home Assistant keys
    # entities on unique_id, so changing this orphans their history -- it is deliberately
    # separate from inverter_name, which only names topics. A second inverter needs its own.
    cfg.setdefault("entity_prefix", "sp_pro")
    # Minimum seconds between publishes of each topic. 0 publishes on every poll.
    cfg.setdefault("buckets", {"state": 0, "state_60s": 60, "state_300s": 300, "blocks": 900})
    # Globs matched against object_id. An excluded entity is never announced at all, so it
    # costs nothing downstream -- no discovery config, no state, no recorder rows.
    cfg.setdefault("exclude", [])
    # Per-entity rounding as {object_id_glob: decimal_places}. Empty publishes raw readings.
    cfg.setdefault("quantize", {})
    return cfg


CFG = load_config(CONFIG)
INVERTER = CFG["inverter_name"]
EPREFIX = CFG["entity_prefix"]
BUCKETS = CFG["buckets"]
SPPRO_BASE = f"{PREFIX}/selectronic/{INVERTER}"


def excluded(object_id):
    return any(fnmatch.fnmatch(object_id, pat) for pat in CFG["exclude"])


def quantize(object_id, value):
    """Round only where configured; unset means the raw reading goes out untouched."""
    if not isinstance(value, float):
        return value
    for pat, places in CFG["quantize"].items():
        if fnmatch.fnmatch(object_id, pat):
            return round(value, places)
    return value


def slug(text):
    """CamelCase to snake_case, keeping acronyms intact: ACOutStatus -> ac_out_status."""
    s = re.sub(r"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", "_", text)
    s = re.sub(r"[^0-9A-Za-z]+", "_", s).lower()
    return re.sub(r"_+", "_", s).strip("_")


# --- devices ---------------------------------------------------------------------------------

def sppro_device(unit):
    serial = str((unit or {}).get("serial") or INVERTER)
    return f"sppro_{serial}", {
        "identifiers": [f"sppro_{serial}"],
        "name": "SP PRO",
        "manufacturer": "Selectronic",
        "model": (unit or {}).get("model") or "SP PRO",
        "serial_number": serial,
        "suggested_area": "Build Site",
    }


def fronius_device(name, label):
    return f"fronius_{name}", {
        "identifiers": [f"fronius_{name}_buildsite"],
        "name": label,
        "manufacturer": "Fronius",
        "model": "Solar Inverter",
        "suggested_area": "Build Site",
    }


# --- sensor definitions ----------------------------------------------------------------------
# (object_id, name, path, unit, device_class, state_class, icon, precision, bucket, multiplier)
#
# The bucket is the slowest topic that still gives a useful reading. Everything on the live
# topic contributes to how often it republishes, so only genuinely fast values belong there.

SPPRO_SENSORS = [
    ("battery_soc", "Battery SoC", "battery.soc_percent", "%", "battery", "measurement", None, 2, "state", 1),
    ("battery_voltage", "Battery Voltage", "battery.volts", "V", "voltage", "measurement", None, 2, "state", 1),
    ("battery_current", "Battery Current", "battery.amps", "A", "current", "measurement", None, 2, "state", 1),
    ("battery_power", "Battery Power", "battery.kilowatts", "W", "power", "measurement", None, 0, "state", 1000),
    ("ac_load_power", "AC Load Power", "ac.load_kilowatts", "W", "power", "measurement", None, 0, "state", 1000),
    ("ac_voltage", "AC Voltage", "ac.volts", "V", "voltage", "measurement", None, 1, "state", 1),
    ("ac_frequency", "AC Frequency", "ac.frequency_hz", "Hz", "frequency", "measurement", None, 2, "state", 1),
    ("dc_current", "DC Current", "dc.amps", "A", "current", "measurement", None, 2, "state", 1),
    ("solar_total_power", "Solar Total Power", "solar_total_watts", "W", "power", "measurement", None, 0, "state", 1),
    ("ac_coupled_power", "AC Coupled Solar Power", "ac_coupled.kilowatts", "W", "power", "measurement", None, 0, "state", 1000),
    ("ac_coupled_power_1", "AC Coupled Solar Power 1", "ac_coupled.per_inverter.0", "W", "power", "measurement", None, 0, "state", 1000),
    ("ac_coupled_power_2", "AC Coupled Solar Power 2", "ac_coupled.per_inverter.1", "W", "power", "measurement", None, 0, "state", 1000),
    ("ac_coupled_power_3", "AC Coupled Solar Power 3", "ac_coupled.per_inverter.2", "W", "power", "measurement", None, 0, "state", 1000),
    ("ac_coupled_power_4", "AC Coupled Solar Power 4", "ac_coupled.per_inverter.3", "W", "power", "measurement", None, 0, "state", 1000),
    ("ac_coupled_power_5", "AC Coupled Solar Power 5", "ac_coupled.per_inverter.4", "W", "power", "measurement", None, 0, "state", 1000),
    ("ac_coupled_percent", "AC Coupled Commanded Limit", "ac_coupled.percent", "%", None, "measurement", "mdi:speedometer", 1, "state", 1),
    ("inverter_power", "Inverter AC Power", "inverter.kilowatts", "W", "power", "measurement", None, 0, "state", 1000),
    ("inverter_current", "Inverter AC Current", "inverter.amps", "A", "current", "measurement", None, 2, "state", 1),
    ("battery_load_5min", "Battery Load 5 min", "battery_trend.load_5min_kilowatts", "W", "power", "measurement", None, 0, "state", 1000),
    ("battery_load_15min", "Battery Load 15 min", "battery_trend.load_15min_kilowatts", "W", "power", "measurement", None, 0, "state", 1000),
    ("shunt1_current", "Shunt 1 Current", "shunts.shunt1_amps", "A", "current", "measurement", None, 2, "state", 1),
    ("shunt2_current", "Shunt 2 Current", "shunts.shunt2_amps", "A", "current", "measurement", None, 2, "state", 1),
    ("shunt1_power", "Shunt 1 Power", "shunts.shunt1_kilowatts", "W", "power", "measurement", None, 0, "state", 1000),
    ("shunt2_power", "Shunt 2 Power", "shunts.shunt2_kilowatts", "W", "power", "measurement", None, 0, "state", 1000),

    ("charger_state", "Charger State", "charger", None, None, None, "mdi:battery-charging", None, "state_60s", 1),
    ("generator_state", "Generator State", "generator.status", None, None, None, "mdi:engine", None, "state_60s", 1),
    ("generator_reason", "Generator Reason", "generator.reason", None, None, None, "mdi:engine-outline", None, "state_60s", 1),
    ("generator_power", "Generator Power", "generator.kilowatts", "W", "power", "measurement", None, 0, "state_60s", 1000),
    ("generator_voltage", "Generator Voltage", "generator.volts", "V", "voltage", "measurement", None, 1, "state_60s", 1),
    ("generator_frequency", "Generator Frequency", "generator.frequency_hz", "Hz", "frequency", "measurement", None, 2, "state_60s", 1),
    ("inverter_mode", "Inverter Mode", "inverter.mode", None, None, None, "mdi:sine-wave", None, "state_60s", 1),
    ("ac_source_status", "AC Source Status", "inverter.ac_source_status", None, None, None, "mdi:transmission-tower", None, "state_60s", 1),
    ("clock_drift", "Clock Offset vs Bridge", "inverter_clock_drift_seconds", "s", None, "measurement", "mdi:clock-alert-outline", 1, "state_60s", 1),
    ("charge_power_limit", "Charge Power Limit", "regulation.charge_power_limit_kilowatts", "W", "power", "measurement", None, 0, "state_60s", 1000),
    ("export_power_limit", "Export Power Limit", "regulation.export_power_limit_kilowatts", "W", "power", "measurement", None, 0, "state_60s", 1000),
    ("input_power_limit", "Input Power Limit", "regulation.input_power_limit_kilowatts", "W", "power", "measurement", None, 0, "state_60s", 1000),
    ("support_power_limit", "Support Power Limit", "regulation.support_power_limit_kilowatts", "W", "power", "measurement", None, 0, "state_60s", 1000),

    ("today_ac_coupled_energy", "Solar Energy Today", "today.ac_coupled_kwh", "kWh", "energy", "total_increasing", None, 3, "state_300s", 1),
    ("today_ac_load_energy", "Load Energy Today", "today.ac_load_kwh", "kWh", "energy", "total_increasing", None, 3, "state_300s", 1),
    ("today_battery_in_energy", "Battery In Today", "today.battery_in_kwh", "kWh", "energy", "total_increasing", None, 3, "state_300s", 1),
    ("today_battery_out_energy", "Battery Out Today", "today.battery_out_kwh", "kWh", "energy", "total_increasing", None, 3, "state_300s", 1),
    ("today_ac_coupled_peak", "Solar Peak Today", "today.ac_coupled_peak_kilowatts", "W", "power", "measurement", None, 0, "state_300s", 1000),
    ("today_inverter_run_hours", "Inverter Run Hours Today", "today.inverter_run_hours", "h", "duration", "total_increasing", None, 2, "state_300s", 1),
    ("today_float_hours", "Float Hours Today", "today.float_hours", "h", "duration", "total_increasing", None, 2, "state_300s", 1),
]

FRONIUS_SENSORS = [
    ("power", "Power", "ac.power_watts", "W", "power", "measurement", 0, 1),
    ("ac_voltage", "AC Voltage", "ac.volts", "V", "voltage", "measurement", 1, 1),
    ("ac_frequency", "AC Frequency", "ac.frequency_hz", "Hz", "frequency", "measurement", 2, 1),
    ("energy_total", "Total Energy", "energy.total_wh", "kWh", "energy", "total_increasing", 3, 0.001),
]

_UNIT_CLASS = {
    "kWh": ("energy", "total_increasing"), "kW": ("power", "measurement"),
    "W": ("power", "measurement"), "V": ("voltage", "measurement"),
    "A": ("current", "measurement"), "°C": ("temperature", "measurement"),
    "Hz": ("frequency", "measurement"), "h": ("duration", "total_increasing"),
    "%": (None, "measurement"),
}


def jinja_path(path, multiplier=1):
    """Dotted path to a Jinja expression; a numeric segment indexes a list."""
    expr = "value_json"
    for part in path.split("."):
        if part.isdigit():
            expr += f"[{part}]"            # list index
        elif part.isidentifier():
            expr += f".{part}"
        else:
            # Register names can start with a digit (5MinBattLoad), which is not a valid
            # attribute, so subscript it instead.
            expr += f"['{part}']"
    if multiplier == 1:
        return f"{{{{ {expr} }}}}"
    return f"{{{{ ({expr} * {multiplier}) if {expr} is not none else none }}}}"


def sensor_config(object_id, name, topic, path, device, unit, dclass, sclass, icon,
                  precision, multiplier=1, diagnostic=False, availability_extra=None):
    cfg = {
        "name": name,
        "object_id": object_id,
        "unique_id": f"solarbridge_{object_id}",
        "state_topic": topic,
        "value_template": jinja_path(path, multiplier),
        "device": device,
        "availability": [{"topic": f"{PREFIX}/status"}],
    }
    if unit: cfg["unit_of_measurement"] = unit
    if dclass: cfg["device_class"] = dclass
    if sclass: cfg["state_class"] = sclass
    if icon: cfg["icon"] = icon
    if precision is not None: cfg["suggested_display_precision"] = precision
    if diagnostic: cfg["entity_category"] = "diagnostic"
    if availability_extra:
        cfg["availability"] = cfg["availability"] + availability_extra
        cfg["availability_mode"] = "all"
    return cfg


def discovery_topic(component, device_id, object_id):
    """<component>/<device_id>/<object_id>/config, as zigbee2mqtt builds it."""
    return f"{DISCOVERY}/{component}/{device_id}/{object_id}/config"


def build_entities(snapshot):
    """Every entity, plus which bucket and path each reads from.

    Returns (discovery, routes); routes maps bucket -> [(object_id, path)] so the publisher
    knows which fields belong on which topic.
    """
    discovery, routes = [], {name: [] for name in BUCKETS}
    sp = snapshot.get("sp_pro") or {}
    dev_id, device = sppro_device(sp.get("unit"))

    for short, name, path, unit, dclass, sclass, icon, prec, bucket, mult in SPPRO_SENSORS:
        obj = f"{EPREFIX}_{short}" if EPREFIX else short
        if excluded(obj) or bucket not in routes:
            continue
        topic = f"{SPPRO_BASE}/{bucket}"
        extra = None
        if short == "clock_drift":
            # Only meaningful while the host clock it is measured against is disciplined.
            extra = [{"topic": topic,
                      "value_template": "{{ 'online' if value_json.inverter_clock_drift_seconds is defined"
                                        " and value_json.inverter_clock_drift_seconds is not none"
                                        " else 'offline' }}"}]
        discovery.append((discovery_topic("sensor", dev_id, obj),
                          sensor_config(obj, name, topic, path, device, unit, dclass, sclass,
                                        icon, prec, mult, availability_extra=extra)))
        routes[bucket].append((obj, path))

    obj = f"{EPREFIX}_bridge_clock_synced" if EPREFIX else "bridge_clock_synced"
    if not excluded(obj) and "state_60s" in routes:
        discovery.append((discovery_topic("binary_sensor", dev_id, obj), {
            "name": "Bridge Clock Synchronised",
            "object_id": obj,
            "unique_id": f"solarbridge_{obj}",
            "state_topic": f"{SPPRO_BASE}/state_60s",
            "value_template": "{{ 'ON' if (value_json.host_clock | default({}, true)).synchronized"
                              " | default(false, true) else 'OFF' }}",
            "device_class": "connectivity",
            "entity_category": "diagnostic",
            "device": device,
            "availability": [{"topic": f"{PREFIX}/status"}],
        }))
        routes["state_60s"].append((obj, "host_clock"))

    # Block fields, taken from whatever the bridge currently decodes rather than a fixed list.
    for block, blk in (sp.get("blocks") or {}).items():
        for field, spec in blk.get("fields", {}).items():
            if spec.get("value") is None:
                continue
            obj = f"{EPREFIX}_{slug(block)}_{slug(field)}" if EPREFIX else f"{slug(block)}_{slug(field)}"
            obj = obj[:60]
            if excluded(obj) or "blocks" not in routes:
                continue
            unit = spec.get("unit")
            dclass, sclass = _UNIT_CLASS.get(unit, (None, None))
            discovery.append((discovery_topic("sensor", dev_id, obj),
                              sensor_config(obj, f"{block} {field}", f"{SPPRO_BASE}/blocks",
                                            f"{block}.{field}", device, unit, dclass, sclass,
                                            None, None, diagnostic=True)))
            routes["blocks"].append((obj, f"{block}.{field}"))

    for f in snapshot.get("fronius") or []:
        name = f.get("name")
        fdev_id, fdevice = fronius_device(name, f.get("custom_name") or f"Fronius {name}")
        topic = f"{PREFIX}/fronius/{name}/state"
        for suffix, label, path, unit, dclass, sclass, prec, mult in FRONIUS_SENSORS:
            obj = f"fronius_{name}_{suffix}"
            if excluded(obj):
                continue
            discovery.append((discovery_topic("sensor", fdev_id, obj),
                              sensor_config(obj, label, topic, path, fdevice, unit, dclass,
                                            sclass, None, prec, mult)))
        for s in f.get("strings") or []:
            i = s.get("index")
            # (payload field, object_id suffix, label, ...). The suffix differs from the field
            # on purpose: it is part of unique_id, and renaming it would orphan the history.
            for field, suffix, flabel, unit, dclass, prec in [
                ("volts", "voltage", "Voltage", "V", "voltage", 1),
                ("amps", "current", "Current", "A", "current", 2),
                ("watts", "power", "Power", "W", "power", 0),
            ]:
                obj = f"fronius_{name}_string{i}_{suffix}"
                if excluded(obj):
                    continue
                discovery.append((discovery_topic("sensor", fdev_id, obj),
                                  sensor_config(obj, f"String {i} {flabel}", topic,
                                                f"strings.{i - 1}.{field}", fdevice, unit,
                                                dclass, "measurement", None, prec)))
    return discovery, routes


# --- payload assembly ------------------------------------------------------------------------

def dig(obj, path):
    for part in path.split("."):
        if obj is None:
            return None
        if part.isdigit() and isinstance(obj, list):
            obj = obj[int(part)] if int(part) < len(obj) else None
        elif isinstance(obj, dict):
            obj = obj.get(part)
        else:
            return None
    return obj


def put(target, path, value):
    """Place a value at a dotted path, growing dicts and lists as needed."""
    parts = path.split(".")
    for i, part in enumerate(parts[:-1]):
        nxt = parts[i + 1]
        child = [] if nxt.isdigit() else {}
        if isinstance(target, list):
            idx = int(part)
            while len(target) <= idx:
                target.append(None)
            if not isinstance(target[idx], (dict, list)):
                target[idx] = child
            target = target[idx]
        else:
            if not isinstance(target.get(part), (dict, list)):
                target[part] = child
            target = target[part]
    last = parts[-1]
    if isinstance(target, list):
        idx = int(last)
        while len(target) <= idx:
            target.append(None)
        target[idx] = value
    else:
        target[last] = value


def flatten_blocks(sp):
    out = {}
    for block, blk in (sp.get("blocks") or {}).items():
        for field, spec in blk.get("fields", {}).items():
            if spec.get("value") is not None:
                out[f"{block}.{field}"] = spec["value"]
    return out


def bucket_payload(sp, blocks_flat, routes, bucket):
    """Exactly the fields routed to this bucket, nested as the templates expect."""
    out = {}
    for object_id, path in routes[bucket]:
        value = blocks_flat.get(path) if bucket == "blocks" else dig(sp, path)
        put(out, path, quantize(object_id, value))
    return out


def publish_if_due(client, topic, payload, bucket, force):
    """Publish when the bucket's interval has elapsed and the payload actually changed."""
    now = time.time()
    if not force:
        if now - _last_published.get(topic, 0) < BUCKETS.get(bucket, 0):
            return 0
        if _last_sent.get(topic) == payload:
            return 0
    client.publish(topic, payload, retain=True)
    _last_sent[topic] = payload
    _last_published[topic] = now
    return 1


def retire_stale_discovery(client, keep):
    """Clear retained discovery configs we no longer publish.

    The topic layout changed, so configs left at the previous addresses would keep their
    entities alive forever as duplicates. An empty retained payload is how MQTT discovery
    deletes one. Entities are keyed on unique_id, which is unchanged, so Home Assistant
    reuses the existing registry entry and the history survives.
    """
    stale = []
    probe = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id="solar-bridge-retire")
    probe.username_pw_set(*_credentials)
    probe.on_message = lambda _c, _u, m: stale.append(m.topic) if m.payload and m.topic not in keep else None
    probe.on_connect = lambda c, _u, _f, _r, _p=None: c.subscribe(f"{DISCOVERY}/+/+/config")
    try:
        probe.connect(_host, _port, 30)
        probe.loop_start()
        time.sleep(8)
        probe.loop_stop()
        probe.disconnect()
    except Exception as e:
        print(f"could not scan for stale discovery configs: {e}", file=sys.stderr, flush=True)
        return 0
    for topic in stale:
        client.publish(topic, "", retain=True)
    if stale:
        print(f"retired {len(stale)} discovery config(s) from the previous layout", flush=True)
        # Let Home Assistant finish removing them before the replacements arrive. Re-announcing
        # a unique_id while its old entity is still registered gets the new one a "_2" suffix,
        # and a different entity_id is a different series as far as the recorder is concerned.
        time.sleep(10)
    return len(stale)


def main():
    global _credentials, _host, _port, _last_forced
    conf = load_env(CONF)
    _credentials = (conf["MQTT_USERNAME"], conf["MQTT_PASSWORD"])
    _host = conf.get("MQTT_HOST", "127.0.0.1")
    _port = int(conf.get("MQTT_PORT", "1883"))

    client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id="solar-bridge")
    client.username_pw_set(*_credentials)
    client.will_set(f"{PREFIX}/status", "offline", retain=True)

    state = {"discovery": [], "routes": None, "retired": False}

    def on_connect(_client, _userdata, _flags, reason, _props=None):
        """Re-announce on every connect, not just the first.

        A dropped link makes the broker publish our retained last-will, so Home Assistant sees
        "offline". paho reconnects by itself, but unless we say "online" again nothing clears
        that, and the entities stay unavailable while we publish state into the void.
        """
        if reason != 0:
            print(f"connect failed: {reason}", file=sys.stderr, flush=True)
            return
        for topic, cfg in state["discovery"]:
            _client.publish(topic, json.dumps(cfg), retain=True)
        _client.publish(f"{PREFIX}/status", "online", retain=True)
        _last_sent.clear()
        _last_published.clear()
        print(f"connected: announced {len(state['discovery'])} discovery configs", flush=True)

    client.on_connect = on_connect
    client.connect(_host, _port, keepalive=60)
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
            with urllib.request.urlopen(req, timeout=90) as r:
                snap = json.loads(r.read())
            sp = snap.get("sp_pro") or {}
            sp["solar_total_watts"] = snap.get("solar_total_watts")

            if state["routes"] is None and sp:
                discovery, routes = build_entities(snap)
                state["discovery"], state["routes"] = discovery, routes
                if not state["retired"]:
                    retire_stale_discovery(client, {t for t, _ in discovery})
                    state["retired"] = True
                for topic, cfg in discovery:
                    client.publish(topic, json.dumps(cfg), retain=True)
                counts = ", ".join(f"{b}={len(v)}" for b, v in routes.items())
                print(f"announced {len(discovery)} entities ({counts})", flush=True)

            force = (time.time() - _last_forced) >= FORCE_EVERY
            if force:
                _last_forced = time.time()

            sent = topics = 0
            if state["routes"]:
                blocks_flat = flatten_blocks(sp)
                for bucket in BUCKETS:
                    if not state["routes"][bucket]:
                        continue
                    topics += 1
                    payload = bucket_payload(sp, blocks_flat, state["routes"], bucket)
                    sent += publish_if_due(client, f"{SPPRO_BASE}/{bucket}",
                                           json.dumps(payload, sort_keys=True), bucket, force)

            for f in snap.get("fronius") or []:
                g = {k: v for k, v in f.items() if k != "taken_at"}
                if isinstance(g.get("status"), dict):
                    g["status"] = {k: v for k, v in g["status"].items() if k != "device_time"}
                topics += 1
                sent += publish_if_due(client, f"{PREFIX}/fronius/{f['name']}/state",
                                       json.dumps(g, sort_keys=True), "state", force)
            published += sent
            skipped += topics - sent

            if fails:
                print(f"bridge recovered after {fails} failures", flush=True)
                client.publish(f"{PREFIX}/status", "online", retain=True)
            fails = 0
        except Exception as e:
            fails += 1
            print(f"bridge read failed ({fails}): {e}", file=sys.stderr, flush=True)
            if fails >= 2:
                client.publish(f"{PREFIX}/status", "offline", retain=True)

        if time.time() - last_report >= 3600:
            print(f"last hour: {published} publishes, {skipped} skipped", flush=True)
            published = skipped = 0
            last_report = time.time()

        for _ in range(INTERVAL):
            if not running:
                break
            time.sleep(1)

    client.publish(f"{PREFIX}/status", "offline", retain=True)
    client.loop_stop()
    client.disconnect()
    return 0


if __name__ == "__main__":
    sys.exit(main())
