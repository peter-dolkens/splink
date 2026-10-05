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
        Console.WriteLine("  /raw         Raw register block: ?address=<decimal|0xHEX>&words=<1..256>");

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
                case "/raw":
                {
                    // Lets a block the bridge does not decode be inspected without stopping the
                    // service to take the serial port, which is what wedged the USB device once.
                    var query = context.Request.QueryString;
                    if (!TryParseAddress(query["address"], out var address) ||
                        !int.TryParse(query["words"], out var words) || words is < 1 or > 256)
                    {
                        status = 400;
                        body = JsonSerializer.Serialize(new
                        {
                            error = "usage: /raw?address=<decimal|0xHEX>&words=<1..256>",
                        }, Json);
                        break;
                    }

                    try
                    {
                        var raw = await sppro.ReadWordsAsync(address, words, cancellationToken).ConfigureAwait(false);
                        body = JsonSerializer.Serialize(new
                        {
                            address,
                            words = raw.Length,
                            read_at = DateTimeOffset.Now,
                            value = raw,
                            hex = raw.Select(x => x.ToString("X4")).ToArray(),
                        }, Json);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        status = 503;
                        body = JsonSerializer.Serialize(new { address, error = ex.Message }, Json);
                    }
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

    private static bool TryParseAddress(string? text, out uint address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out address)
            : uint.TryParse(text, out address);
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
        // Omitted entirely when the host clock it is measured against is not disciplined.
        inverter_clock_drift_seconds = s.ClockDriftSeconds,
        // The reference the drift above is measured against, published so it can be judged
        // rather than assumed. offset_seconds is this host's own error against its NTP peer.
        host_clock = s.HostClock is null ? null : new
        {
            synchronized = s.HostClock.Synchronized,
            source = s.HostClock.Source,
            server = s.HostClock.Server,
            stratum = s.HostClock.Stratum,
            offset_seconds = s.HostClock.OffsetSeconds,
            root_distance_seconds = s.HostClock.RootDistanceSeconds,
            poll_interval_seconds = s.HostClock.PollIntervalSeconds,
            checked_at = s.HostClock.CheckedAt,
        },
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
        generator = s.Live is null ? null : new
        {
            status = s.Live.GeneratorState,
            reason = s.Live.GeneratorReason,
            kilowatts = Round(s.Live.GeneratorKilowatts, 3),
            kilowatts_5min_avg = Round(s.Live.GeneratorKilowatts5MinAverage, 3),
            volts = Round(s.Live.GeneratorVolts, 1),
            amps = Round(s.Live.GeneratorAmps, 2),
            frequency_hz = Round(s.Live.GeneratorFrequencyHz, 2),
            max_available_input_kilowatts = Round(s.Live.MaxAvailableInputKilowatts, 3),
        },

        // The SP PRO's own measurement of AC-coupled solar, which is independent of whatever the
        // solar inverters report over their own network. Having both lets them check each other.
        ac_coupled = s.Live is null ? null : new
        {
            kilowatts = Round(s.Live.AcCoupledKilowatts, 3),
            percent = Round(s.Live.AcCoupledPercent, 1),
            per_inverter = s.Live.AcCoupledKilowattsPerInverter.Select(x => Round(x, 3)).ToArray(),
        },
        inverter = s.Live is null ? null : new
        {
            kilowatts = Round(s.Live.InverterAcKilowatts, 3),
            amps = Round(s.Live.InverterAcAmps, 2),
            mode = s.Live.InverterMode,
            ac_source_status = s.Live.AcSourceStatus,
        },
        battery_trend = s.Live is null ? null : new
        {
            load_5min_kilowatts = Round(s.Live.BatteryLoad5MinKilowatts, 3),
            load_15min_kilowatts = Round(s.Live.BatteryLoad15MinKilowatts, 3),
        },
        shunts = s.Live is null ? null : new
        {
            shunt1_amps = Round(s.Live.Shunt1Amps, 2),
            shunt2_amps = Round(s.Live.Shunt2Amps, 2),
            shunt1_kilowatts = Round(s.Live.Shunt1Kilowatts, 3),
            shunt2_kilowatts = Round(s.Live.Shunt2Kilowatts, 3),
        },
        regulation = s.Live is null ? null : new
        {
            active_schedule = s.Live.ActiveSchedule,
            input_power_limit_kilowatts = Round(s.Live.InputPowerLimitKilowatts, 3),
            export_power_limit_kilowatts = Round(s.Live.ExportPowerLimitKilowatts, 3),
            charge_power_limit_kilowatts = Round(s.Live.ChargePowerLimitKilowatts, 3),
            support_power_limit_kilowatts = Round(s.Live.SupportPowerLimitKilowatts, 3),
            charger_status = s.Live.RegulationChargerStatus,
            inverter_lockout = s.Live.InverterLockoutStatus,
            source_disconnect = s.Live.SourceDisconnectStatus,
        },

        // Energy accumulators, read on a slower cadence than live data.
        today_read_at = s.TodayReadAt,
        today = s.Today is null ? null : new
        {
            dc_input_kwh = Round(s.Today.DcInputKilowattHours, 3),
            dc_output_kwh = Round(s.Today.DcOutputKilowattHours, 3),
            dc_net_kwh = Round(s.Today.DcNetKilowattHours, 3),
            inverter_dc_kwh = Round(s.Today.InverterDcKilowattHours, 3),
            battery_in_kwh = Round(s.Today.BatteryInKilowattHours, 3),
            battery_out_kwh = Round(s.Today.BatteryOutKilowattHours, 3),
            ac_load_kwh = Round(s.Today.AcLoadKilowattHours, 3),
            ac_input_kwh = Round(s.Today.AcInputKilowattHours, 3),
            ac_export_kwh = Round(s.Today.AcExportKilowattHours, 3),
            ac_coupled_kwh = Round(s.Today.AcCoupledKilowattHours, 3),
            ac_coupled_kwh_per_inverter = s.Today.AcCoupledKilowattHoursPerInverter.Select(x => Round(x, 3)).ToArray(),
            ac_coupled_peak_kilowatts = Round(s.Today.AcCoupledPeakKilowatts, 3),
            shunt1_kwh = Round(s.Today.Shunt1KilowattHours, 3),
            shunt2_kwh = Round(s.Today.Shunt2KilowattHours, 3),
            shunt1_peak_kilowatts = Round(s.Today.Shunt1PeakKilowatts, 3),
            shunt2_peak_kilowatts = Round(s.Today.Shunt2PeakKilowatts, 3),
            float_hours = Round(s.Today.FloatHours, 2),
            ac_input_hours = Round(s.Today.AcInputHours, 2),
            inverter_run_hours = Round(s.Today.InverterRunHours, 2),
        },
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
