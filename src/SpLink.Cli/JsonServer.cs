using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpLink.Fronius;
using SpLink.Protocol;

namespace SpLink.Cli;

/// <summary>
/// A read-only HTTP surface over the SP PRO and any configured Fronius inverters.
/// Everything is fetched on demand: an idle bridge puts no traffic on any inverter.
/// </summary>
internal sealed class JsonServer(SpProSession sppro, int port, FroniusService? fronius = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var listener = new HttpListener();
        // On Unix the managed HttpListener accepts a wildcard prefix; a hostname-specific one is rejected.
        listener.Prefixes.Add($"http://+:{port}/");
        try
        {
            listener.Start();
        }
        catch (Exception ex) when (ex is HttpListenerException or PlatformNotSupportedException)
        {
            listener.Prefixes.Clear();
            listener.Prefixes.Add($"http://localhost:{port}/");
            try { listener.Start(); }
            catch (HttpListenerException inner) { throw new SpProException($"Cannot listen on port {port}: {inner.Message}"); }
        }

        Console.WriteLine($"JSON API listening on port {port} (read-only) — http://localhost:{port}/");
        Console.WriteLine("  /            SP PRO live data" + (fronius is not null ? " + Fronius inverters" : ""));
        Console.WriteLine("  /sppro       SP PRO only");
        if (fronius is not null) Console.WriteLine("  /fronius     Fronius inverters only");
        Console.WriteLine("  /health      liveness");
        Console.WriteLine("  /metrics     Prometheus text format");

        using var stopping = cancellationToken.Register(listener.Stop);
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (cancellationToken.IsCancellationRequested) { break; }
            catch (HttpListenerException) { continue; }

            _ = Task.Run(() => HandleAsync(context, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        int status = 200;
        string contentType = "application/json";
        string body;

        try
        {
            switch (path)
            {
                case "/" or "/snapshot":
                {
                    var (sp, fr) = await ReadBothAsync(cancellationToken).ConfigureAwait(false);
                    status = sp.Healthy ? 200 : 503;
                    body = JsonSerializer.Serialize(new
                    {
                        sp_pro = DescribeSpPro(sp),
                        fronius = fr?.Select(DescribeFronius),
                        solar_total_watts = TotalSolarWatts(fr),
                    }, Json);
                    break;
                }
                case "/sppro":
                {
                    var sp = await sppro.ReadAsync(cancellationToken).ConfigureAwait(false);
                    status = sp.Healthy ? 200 : 503;
                    body = JsonSerializer.Serialize(DescribeSpPro(sp), Json);
                    break;
                }
                case "/fronius":
                {
                    if (fronius is null) { status = 404; body = """{"error":"no fronius inverters are configured"}"""; break; }
                    var fr = await fronius.ReadAllAsync(cancellationToken).ConfigureAwait(false);
                    // 207 when some answered and some did not: the response is still useful.
                    status = fr.Count > 0 && fr.All(f => f.Healthy) ? 200 : 207;
                    body = JsonSerializer.Serialize(fr.Select(DescribeFronius), Json);
                    break;
                }
                case "/health":
                {
                    var sp = await sppro.ReadAsync(cancellationToken).ConfigureAwait(false);
                    status = sp.Healthy ? 200 : 503;
                    body = JsonSerializer.Serialize(new
                    {
                        status = sp.Healthy ? "ok" : "degraded",
                        sp_pro = sp.Error ?? "ok",
                        sp_pro_connected = sppro.Connected,
                    }, Json);
                    break;
                }
                case "/metrics":
                {
                    var (sp, fr) = await ReadBothAsync(cancellationToken).ConfigureAwait(false);
                    contentType = "text/plain; version=0.0.4";
                    body = Metrics(sp, fr);
                    break;
                }
                default:
                    status = 404;
                    body = """{"error":"not found"}""";
                    break;
            }
        }
        catch (Exception ex)
        {
            // A failing request must never take the listener down.
            status = 500;
            body = JsonSerializer.Serialize(new { error = ex.Message }, Json);
        }

        try
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = contentType;
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes);
        }
        catch (Exception)
        {
            // Client went away mid-write.
        }
        finally
        {
            try { context.Response.Close(); } catch (Exception) { }
        }
    }

    /// <summary>Reads both sources concurrently; the Fronius HTTP calls need not wait on the serial link.</summary>
    private async Task<(SpProSnapshot, IReadOnlyList<FroniusResult>?)> ReadBothAsync(CancellationToken cancellationToken)
    {
        var spTask = sppro.ReadAsync(cancellationToken);
        var frTask = fronius?.ReadAllAsync(cancellationToken);
        var sp = await spTask.ConfigureAwait(false);
        var fr = frTask is null ? null : await frTask.ConfigureAwait(false);
        return (sp, fr);
    }

    private static object DescribeSpPro(SpProSnapshot s) => new
    {
        taken_at = s.TakenAt,
        healthy = s.Healthy,
        error = s.Error,
        unit = s.Unit is null ? null : new
        {
            model = s.Unit.Model,
            description = s.Unit.ModelDescription,
            serial = s.Unit.SerialNumber,
            hardware_revision = s.Unit.HardwareRevision,
        },
        inverter_clock = s.InverterClock,
        // Read on a slower cadence than the rest: it is a separate serial round trip and only
        // matters for spotting drift. Both fields describe the same measurement instant.
        inverter_clock_read_at = s.ClockReadAt,
        inverter_clock_drift_seconds = s.ClockDriftSeconds,
        battery = s.Live is null ? null : new
        {
            soc_percent = Round(s.Live.BatterySoCPercent, 3),
            volts = Round(s.Live.BatteryVolts, 2),
            amps = Round(s.Live.BatteryAmps, 2),
            kilowatts = Round(s.Live.BatteryKilowatts, 3),
        },
        ac = s.Live is null ? null : new
        {
            load_kilowatts = Round(s.Live.AcLoadKilowatts, 3),
            volts = Round(s.Live.AcVolts, 1),
            frequency_hz = Round(s.Live.AcFrequencyHz, 2),
        },
        dc = s.Live is null ? null : new { amps = Round(s.Live.DcAmps, 2) },
        charger = s.Live?.ChargerState,
        generator = s.Live is null ? null : new { status = s.Live.GeneratorState, reason = s.Live.GeneratorReason },
    };

    private static object DescribeFronius(FroniusResult f) => new
    {
        name = f.Name,
        host = f.Host,
        custom_name = f.CustomName,
        rated_watts = f.RatedWatts,
        healthy = f.Healthy,
        error = f.Error,
        taken_at = f.TakenAt,
        ac = f.Reading is null ? null : new
        {
            power_watts = Round(f.Reading.AcPowerWatts, 1),
            apparent_va = Round(f.Reading.AcApparentVa, 1),
            volts = Round(f.Reading.AcVolts, 1),
            amps = Round(f.Reading.AcAmps, 3),
            frequency_hz = Round(f.Reading.AcFrequencyHz, 2),
        },
        energy = f.Reading is null ? null : new
        {
            day_wh = Round(f.Reading.DayEnergyWh, 1),
            year_wh = Round(f.Reading.YearEnergyWh, 1),
            total_wh = Round(f.Reading.TotalEnergyWh, 1),
        },
        strings = f.Reading?.Strings.Select(t => new
        {
            index = t.Index,
            volts = Round(t.Volts, 1),
            amps = Round(t.Amps, 3),
            watts = Round(t.Watts, 1),
        }),
        status = f.Reading is null ? null : new
        {
            state = f.Reading.InverterState,
            status_code = f.Reading.StatusCode,
            error_code = f.Reading.ErrorCode,
            device_time = f.Reading.DeviceTimestamp,
        },
    };

    /// <summary>Combined PV output across reachable Fronius inverters, or null when none are.</summary>
    private static double? TotalSolarWatts(IReadOnlyList<FroniusResult>? all)
    {
        var readings = all?.Where(f => f.Healthy && f.Reading?.AcPowerWatts is not null).ToList();
        return readings is null or { Count: 0 } ? null : Math.Round(readings.Sum(f => f.Reading!.AcPowerWatts!.Value), 1);
    }

    private static string Metrics(SpProSnapshot s, IReadOnlyList<FroniusResult>? fronius)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# HELP sppro_up 1 when the last SP PRO read succeeded.");
        sb.AppendLine("# TYPE sppro_up gauge");
        sb.AppendLine($"sppro_up {(s.Healthy ? 1 : 0)}");
        if (s.Live is { } l)
        {
            Gauge(sb, "sppro_battery_soc_percent", l.BatterySoCPercent);
            Gauge(sb, "sppro_battery_volts", l.BatteryVolts);
            Gauge(sb, "sppro_battery_amps", l.BatteryAmps);
            Gauge(sb, "sppro_battery_kilowatts", l.BatteryKilowatts);
            Gauge(sb, "sppro_ac_volts", l.AcVolts);
            Gauge(sb, "sppro_ac_frequency_hz", l.AcFrequencyHz);
            Gauge(sb, "sppro_ac_load_kilowatts", l.AcLoadKilowatts);
        }

        if (fronius is not null)
        {
            sb.AppendLine("# HELP fronius_up 1 when the inverter answered.");
            sb.AppendLine("# TYPE fronius_up gauge");
            foreach (var f in fronius) sb.AppendLine($"fronius_up{{inverter=\"{f.Name}\"}} {(f.Healthy ? 1 : 0)}");
            foreach (var f in fronius)
            {
                if (f.Reading is not { } r) continue;
                var label = $"{{inverter=\"{f.Name}\"}}";
                Gauge(sb, "fronius_ac_power_watts" + label, r.AcPowerWatts);
                Gauge(sb, "fronius_ac_volts" + label, r.AcVolts);
                Gauge(sb, "fronius_ac_frequency_hz" + label, r.AcFrequencyHz);
                Gauge(sb, "fronius_energy_total_wh" + label, r.TotalEnergyWh);
            }
            Gauge(sb, "solar_total_watts", TotalSolarWatts(fronius));
        }
        return sb.ToString();
    }

    private static void Gauge(StringBuilder sb, string nameWithLabels, double? value)
    {
        if (value is null) return;
        // A TYPE line must name the metric only, so strip any label set before emitting it.
        var bare = nameWithLabels.Split('{')[0];
        sb.AppendLine($"# TYPE {bare} gauge");
        sb.AppendLine($"{nameWithLabels} {value.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
    }

    private static double? Round(double? v, int places) => v is null ? null : Math.Round(v.Value, places);
}
