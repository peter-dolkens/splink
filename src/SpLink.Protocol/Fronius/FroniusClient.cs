using System.Net.Http.Json;
using System.Text.Json;

namespace SpLink.Fronius;

/// <summary>One Fronius inverter to poll, as named in configuration.</summary>
public sealed record FroniusInverterConfig(string Name, string Host);

/// <summary>A reading from one Fronius inverter. Any field may be null: the Solar API reports null for
/// values a given model or meter configuration does not provide (DAY_ENERGY commonly is).</summary>
public sealed record FroniusReading
{
    public string? CustomName { get; init; }
    public double? AcPowerWatts { get; init; }
    public double? AcApparentVa { get; init; }
    public double? AcVolts { get; init; }
    public double? AcAmps { get; init; }
    public double? AcFrequencyHz { get; init; }
    public double? DayEnergyWh { get; init; }
    public double? YearEnergyWh { get; init; }
    public double? TotalEnergyWh { get; init; }
    public IReadOnlyList<FroniusString> Strings { get; init; } = [];
    public string? InverterState { get; init; }
    public int? StatusCode { get; init; }
    public int? ErrorCode { get; init; }
    public DateTimeOffset? DeviceTimestamp { get; init; }

    /// <summary>The inverter reports a non-zero error code even while otherwise running.</summary>
    public bool HasError => ErrorCode is not null and not 0;
}

/// <summary>One MPPT string's DC measurements.</summary>
public sealed record FroniusString(int Index, double? Volts, double? Amps)
{
    public double? Watts => Volts is { } v && Amps is { } a ? v * a : null;
}

public sealed class FroniusException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Read-only client for the Fronius Solar API v1. The Solar API offers no write operations,
/// so this cannot change inverter behaviour even in principle.
/// </summary>
public sealed class FroniusClient(HttpClient http)
{
    /// <summary>Reads live data for device 1, the only device on a standalone inverter's own API.</summary>
    public async Task<FroniusReading> ReadAsync(string host, CancellationToken cancellationToken = default)
    {
        var url = $"http://{host}/solar_api/v1/GetInverterRealtimeData.cgi?Scope=Device&DeviceId=1&DataCollection=CommonInverterData";
        JsonElement root;
        try
        {
            root = await http.GetFromJsonAsync<JsonElement>(url, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new FroniusException($"{host}: {ex.Message}", ex);
        }

        if (!root.TryGetProperty("Body", out var body) || !body.TryGetProperty("Data", out var data))
            throw new FroniusException($"{host}: unexpected Solar API response (no Body.Data)");

        var strings = new List<FroniusString>();
        for (int i = 1; i <= 4; i++)
        {
            var volts = Measurement(data, i == 1 ? "UDC" : $"UDC_{i}");
            var amps = Measurement(data, i == 1 ? "IDC" : $"IDC_{i}");
            if (volts is not null || amps is not null) strings.Add(new FroniusString(i, volts, amps));
        }

        int? status = null, error = null;
        string? state = null;
        if (data.TryGetProperty("DeviceStatus", out var ds) && ds.ValueKind == JsonValueKind.Object)
        {
            state = ds.TryGetProperty("InverterState", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            status = Int(ds, "StatusCode");
            error = Int(ds, "ErrorCode");
        }

        DateTimeOffset? stamp = null;
        if (root.TryGetProperty("Head", out var head) && head.TryGetProperty("Timestamp", out var ts)
            && ts.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(ts.GetString(), out var parsed))
            stamp = parsed;

        return new FroniusReading
        {
            AcPowerWatts = Measurement(data, "PAC"),
            AcApparentVa = Measurement(data, "SAC"),
            AcVolts = Measurement(data, "UAC"),
            AcAmps = Measurement(data, "IAC"),
            AcFrequencyHz = Measurement(data, "FAC"),
            DayEnergyWh = Measurement(data, "DAY_ENERGY"),
            YearEnergyWh = Measurement(data, "YEAR_ENERGY"),
            TotalEnergyWh = Measurement(data, "TOTAL_ENERGY"),
            Strings = strings,
            InverterState = state,
            StatusCode = status,
            ErrorCode = error,
            DeviceTimestamp = stamp,
        };
    }

    /// <summary>Reads the configured display name and rated PV power, which change rarely.</summary>
    public async Task<(string? Name, double? RatedWatts)> ReadInfoAsync(string host, CancellationToken cancellationToken = default)
    {
        try
        {
            var root = await http.GetFromJsonAsync<JsonElement>(
                $"http://{host}/solar_api/v1/GetInverterInfo.cgi", cancellationToken).ConfigureAwait(false);
            if (root.TryGetProperty("Body", out var body) && body.TryGetProperty("Data", out var data))
                foreach (var device in data.EnumerateObject())
                {
                    var name = device.Value.TryGetProperty("CustomName", out var n) ? n.GetString() : null;
                    double? rated = device.Value.TryGetProperty("PVPower", out var p) && p.TryGetDouble(out var w) ? w : null;
                    return (name, rated);
                }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Informational only; a failure here must not stop live polling.
        }
        return (null, null);
    }

    /// <summary>Solar API measurements are {"Unit": "...", "Value": n}, and Value is null when unavailable.</summary>
    private static double? Measurement(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var m) || m.ValueKind != JsonValueKind.Object) return null;
        if (!m.TryGetProperty("Value", out var v) || v.ValueKind is JsonValueKind.Null) return null;
        return v.TryGetDouble(out var d) ? d : null;
    }

    private static int? Int(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
