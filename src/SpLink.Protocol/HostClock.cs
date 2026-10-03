using System.Diagnostics;
using System.Globalization;

namespace SpLink.Protocol;

/// <summary>
/// What the bridge host's own clock is worth.
/// <para>
/// Any drift figure we report for an inverter is measured against this clock, so it is only as
/// trustworthy as this clock is. Publishing the reference alongside the measurement lets a
/// consumer judge it rather than take it on trust — and lets the bridge withhold a drift figure
/// outright once it knows the reference is unsound.
/// </para>
/// </summary>
public sealed record HostClockStatus(
    DateTimeOffset CheckedAt,
    bool? Synchronized,
    string Source,
    string? Server = null,
    int? Stratum = null,
    double? OffsetSeconds = null,
    double? RootDistanceSeconds = null,
    double? PollIntervalSeconds = null)
{
    /// <summary>
    /// True only when we positively established the clock is disciplined. <c>null</c> means we
    /// could not tell, which is not the same as "no" — see <see cref="HostClockProbe"/>.
    /// </summary>
    public bool IsDisciplined => Synchronized == true;
}

/// <summary>
/// Asks the operating system whether its clock is being disciplined, and by what.
/// <para>
/// On Linux this is <c>timedatectl</c>: the <c>NTPSynchronized</c> property comes from the
/// kernel's own NTP state (<c>ntp_adjtime</c>), so it is accurate whichever daemon is doing the
/// work — timesyncd, chrony or ntpd. <c>timesync-status</c> adds the detail, but only
/// systemd-timesyncd publishes it, so it is strictly best effort.
/// </para>
/// <para>
/// Everywhere else we report <c>Synchronized = null</c>. That is deliberate: claiming "not
/// synchronised" on a platform we simply cannot interrogate would suppress perfectly good
/// readings, so callers treat null as "unverified" and carry on.
/// </para>
/// </summary>
public sealed class HostClockProbe
{
    private HostClockStatus? _cached;

    /// <summary>
    /// How long a probe result is reused. Short enough that losing NTP shows up promptly,
    /// long enough that we are not forking a process per request.
    /// </summary>
    public TimeSpan CacheFor { get; set; } = TimeSpan.FromSeconds(60);

    public HostClockStatus Read()
    {
        if (_cached is { } c && DateTimeOffset.Now - c.CheckedAt < CacheFor) return c;
        return _cached = Probe();
    }

    private static HostClockStatus Probe()
    {
        var at = DateTimeOffset.Now;
        if (!OperatingSystem.IsLinux()) return new HostClockStatus(at, null, "unverified");

        var synchronized = Run("timedatectl", "show --property=NTPSynchronized --value")?.Trim() switch
        {
            "yes" => (bool?)true,
            "no" => false,
            _ => null,
        };

        var detail = ParseTimesyncStatus(Run("timedatectl", "timesync-status"));
        var source = detail is not null ? "systemd-timesyncd"
                   : synchronized is not null ? "kernel (ntp_adjtime)"
                   : "unverified";

        return new HostClockStatus(at, synchronized, source,
            detail?.Server, detail?.Stratum, detail?.Offset, detail?.RootDistance, detail?.PollInterval);
    }

    private sealed record TimesyncDetail(
        string? Server, int? Stratum, double? Offset, double? RootDistance, double? PollInterval);

    private static TimesyncDetail? ParseTimesyncStatus(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        if (fields.Count == 0) return null;

        // "67.219.100.202 (2.debian.pool.ntp.org)" — prefer the name, which says more and
        // does not pin the record to whichever pool member answered this minute.
        string? server = null;
        if (fields.TryGetValue("Server", out var raw) && raw.Length > 0)
        {
            var open = raw.IndexOf('(');
            var close = raw.LastIndexOf(')');
            server = open >= 0 && close > open ? raw[(open + 1)..close] : raw;
        }

        int? stratum = fields.TryGetValue("Stratum", out var s)
                       && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                     ? n : null;

        return new TimesyncDetail(
            server, stratum,
            ParseDuration(fields.GetValueOrDefault("Offset")),
            // Trailing "(max: 5s)" / "(min: 32s; max 34min 8s)" describe limits, not the value.
            ParseDuration(Before(fields.GetValueOrDefault("Root distance"), '(')),
            ParseDuration(Before(fields.GetValueOrDefault("Poll interval"), '(')));
    }

    private static string? Before(string? text, char stop)
    {
        if (text is null) return null;
        var i = text.IndexOf(stop);
        return i < 0 ? text : text[..i];
    }

    /// <summary>
    /// Reads a systemd duration: a run of value/unit pairs, optionally signed —
    /// "34min 8s", "-10.483ms", "1us". Returns null if nothing parsed, never a partial guess.
    /// </summary>
    internal static double? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var span = text.AsSpan().Trim();

        var negative = span[0] == '-';
        if (negative || span[0] == '+') span = span[1..];

        double total = 0;
        var parsed = false;
        var i = 0;
        while (i < span.Length)
        {
            while (i < span.Length && span[i] == ' ') i++;
            var numberStart = i;
            while (i < span.Length && (char.IsAsciiDigit(span[i]) || span[i] == '.')) i++;
            if (i == numberStart) break;
            if (!double.TryParse(span[numberStart..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                break;

            var unitStart = i;
            while (i < span.Length && char.IsLetter(span[i])) i++;
            var seconds = span[unitStart..i].ToString() switch
            {
                "us" or "µs" or "usec" => value / 1_000_000,
                "ms" or "msec" => value / 1_000,
                "" or "s" or "sec" or "seconds" => value,
                "min" => value * 60,
                "h" => value * 3600,
                "d" => value * 86_400,
                _ => double.NaN,
            };
            if (double.IsNaN(seconds)) break;
            total += seconds;
            parsed = true;
        }
        return parsed ? (negative ? -total : total) : null;
    }

    private static string? Run(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null) return null;

            // Drain stderr concurrently; a full pipe would otherwise wedge the child.
            _ = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                return null;
            }
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            // A missing or unusable timedatectl just means we cannot verify the clock.
            return null;
        }
    }
}
