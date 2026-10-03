using SpLink.Protocol;
using Xunit;

namespace SpLink.Protocol.Tests;

public class HostClockTests
{
    [Theory]
    // Real strings from `timedatectl timesync-status` on the bridge Pi.
    [InlineData("-10.483ms", -0.010483)]
    [InlineData("33.011ms", 0.033011)]
    [InlineData("34min 8s", 2048)]
    [InlineData("32s", 32)]
    [InlineData("1us", 0.000001)]
    [InlineData("5s", 5)]
    [InlineData("1h 2min 3s", 3723)]
    [InlineData("0", 0)]
    public void Parses_systemd_durations(string text, double expected) =>
        Assert.Equal(expected, HostClockProbe.ParseDuration(text)!.Value, 9);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("n/a")]
    public void Returns_null_rather_than_guessing(string? text) =>
        Assert.Null(HostClockProbe.ParseDuration(text));

    [Fact]
    public void Stops_at_the_first_unit_it_does_not_know()
    {
        // Better to under-report than to silently drop a component and look precise.
        Assert.Equal(60, HostClockProbe.ParseDuration("1min 4fortnights")!.Value, 9);
    }

    [Fact]
    public void An_unverifiable_clock_is_not_a_bad_clock()
    {
        // Off Linux we cannot interrogate the OS. That must read as "unknown", never as
        // "not synchronised", or every drift reading on a dev machine would be suppressed.
        var status = new HostClockProbe().Read();
        if (!OperatingSystem.IsLinux())
        {
            Assert.Null(status.Synchronized);
            Assert.False(status.IsDisciplined);
        }
        else
        {
            Assert.NotNull(status.Synchronized);
        }
    }

    [Fact]
    public void Caches_so_a_burst_of_requests_does_not_fork_a_process_each()
    {
        var probe = new HostClockProbe();
        Assert.Same(probe.Read(), probe.Read());
    }
}

public class ClockDriftGateTests
{
    private static HostClockStatus Host(bool? synchronized) =>
        new(DateTimeOffset.Now, synchronized, "test");

    [Fact]
    public void Withholds_the_figure_when_the_reference_is_known_bad()
    {
        // The whole point: an undisciplined host clock makes the subtraction meaningless,
        // and a meaningless number that still looks like a measurement is worse than a gap.
        Assert.Null(SpProSession.Drift(Host(false), -20.7));
    }

    [Fact]
    public void Reports_the_figure_when_the_reference_is_disciplined() =>
        Assert.Equal(-20.7, SpProSession.Drift(Host(true), -20.7));

    [Fact]
    public void Reports_the_figure_when_the_reference_cannot_be_checked() =>
        // "Unverifiable" must not collapse into "bad", or the bridge goes mute off Linux.
        Assert.Equal(-20.7, SpProSession.Drift(Host(null), -20.7));

    [Fact]
    public void A_zero_offset_survives_the_gate() =>
        // 0.0 is a real reading, not an absent one; it must not be confused with null anywhere
        // along the chain from here to the Home Assistant availability template.
        Assert.Equal(0.0, SpProSession.Drift(Host(true), 0.0));

    [Fact]
    public void Nothing_measured_stays_nothing() =>
        Assert.Null(SpProSession.Drift(Host(true), null));
}
