using SpLink.Protocol;
using SpLink.Protocol.Simulation;
using SpLink.Protocol.Transport;
using Xunit;

namespace SpLink.Protocol.Tests;

public class SpProClientTests
{
    [Fact]
    public async Task Connects_and_logs_in_with_the_default_password()
    {
        var fake = new FakeSpPro(ticksInRealTime: false);
        await using var client = new SpProClient(fake);

        var info = await client.ConnectAsync();

        Assert.True(info.LoggedIn);
        Assert.True(fake.LoggedIn);
        Assert.Equal(1, info.LinkPort);
        Assert.Equal("115200", info.BaudDescription);

        var addresses = fake.RequestLog.Select(r => (SpProFrame.Op(r), SpProFrame.Address(r))).ToList();
        Assert.Equal(
        [
            (FrameOp.Read, SpProRegisters.LinkPort),
            (FrameOp.Read, SpProRegisters.LoginChallenge),
            (FrameOp.Write, SpProRegisters.LoginChallenge),
            (FrameOp.Read, SpProRegisters.LoginResult),
            (FrameOp.Read, SpProRegisters.LinkPort),
            (FrameOp.Read, SpProRegisters.Port1BaudCode),
        ], addresses);
    }

    [Fact]
    public async Task Wrong_password_is_reported_as_a_login_failure()
    {
        var fake = new FakeSpPro(ticksInRealTime: false) { Password = "something else" };
        await using var client = new SpProClient(fake);

        await Assert.ThrowsAsync<SpProLoginException>(() => client.ConnectAsync("Selectronic SP PRO"));
        Assert.False(fake.LoggedIn);
    }

    [Fact]
    public async Task Skips_login_when_the_unit_does_not_ask_for_it()
    {
        var fake = new FakeSpPro(ticksInRealTime: false) { RequireLogin = false, LinkPort = 2, BaudCode = 6 };
        await using var client = new SpProClient(fake);

        var info = await client.ConnectAsync();

        Assert.False(info.LoggedIn);
        Assert.Equal(2, info.LinkPort);
        Assert.Equal("57600", info.BaudDescription);
        Assert.DoesNotContain(fake.RequestLog, r => SpProFrame.Address(r) == SpProRegisters.LoginChallenge);
        Assert.Contains(fake.RequestLog, r => SpProFrame.Address(r) == SpProRegisters.Port2BaudCode);
    }

    [Fact]
    public async Task Reads_the_clock_exactly()
    {
        var fake = new FakeSpPro(new DateTime(2026, 10, 3, 14, 5, 9, 120), ticksInRealTime: false);
        await using var client = new SpProClient(fake);
        await client.ConnectAsync();

        var reading = await client.ReadClockAsync();

        Assert.Equal(new DateTime(2026, 10, 3, 14, 5, 9, 120), reading.Time);
        Assert.Equal(DayOfWeek.Saturday, reading.StoredDayOfWeek);
        Assert.True(reading.DayOfWeekConsistent);
    }

    [Fact]
    public async Task Sets_the_clock()
    {
        var fake = new FakeSpPro(new DateTime(2020, 1, 1), ticksInRealTime: false);
        await using var client = new SpProClient(fake, allowWrites: true);
        await client.ConnectAsync();

        var target = new DateTime(2026, 10, 4, 9, 30, 0);
        await client.SetClockAsync(target);

        Assert.Equal(target, fake.Clock);
        var reading = await client.ReadClockAsync();
        Assert.Equal(target, reading.Time);
    }

    [Fact]
    public async Task Resynchronises_past_noise_before_a_response()
    {
        var fake = new FakeSpPro(new DateTime(2026, 10, 3, 1, 2, 3), ticksInRealTime: false) { NoisePrefix = [0x00, 0xFF, 0x13] };
        await using var client = new SpProClient(fake);
        await client.ConnectAsync();

        var reading = await client.ReadClockAsync();
        Assert.Equal(new DateTime(2026, 10, 3, 1, 2, 3), reading.Time);
    }

    [Fact]
    public async Task Repairs_a_gateway_mangled_link_port_header()
    {
        var fake = new FakeSpPro(ticksInRealTime: false) { MangleLinkPortHeader = true };
        await using var client = new SpProClient(fake);

        var info = await client.ConnectAsync();

        Assert.True(info.LoggedIn);
        Assert.Equal(1, info.LinkPort);
    }

    [Fact]
    public async Task Read_only_client_refuses_to_set_the_clock()
    {
        var fake = new FakeSpPro(new DateTime(2020, 1, 1), ticksInRealTime: false);
        await using var client = new SpProClient(fake); // read-only by default
        await client.ConnectAsync();

        var ex = await Assert.ThrowsAsync<SpProReadOnlyException>(() => client.SetClockAsync(new DateTime(2026, 10, 4, 9, 0, 0)));

        Assert.Contains("read-only", ex.Message);
        Assert.Equal(new DateTime(2020, 1, 1), fake.Clock);
        Assert.DoesNotContain(fake.RequestLog, r => SpProFrame.Op(r) == FrameOp.Write && SpProFrame.Address(r) == SpProRegisters.ClockWrite);
    }

    [Fact]
    public async Task Read_only_client_still_completes_the_login_handshake()
    {
        var fake = new FakeSpPro(ticksInRealTime: false);
        await using var client = new SpProClient(fake);

        var info = await client.ConnectAsync();

        Assert.True(info.LoggedIn);
        Assert.Contains(fake.RequestLog, r => SpProFrame.Op(r) == FrameOp.Write && SpProFrame.Address(r) == SpProRegisters.LoginChallenge);
    }

    [Fact]
    public async Task Read_only_client_can_suppress_the_disconnect_write()
    {
        var fake = new FakeSpPro(ticksInRealTime: false);
        await using var client = new SpProClient(fake) { NotifyOnDisconnect = false };
        await client.ConnectAsync();

        await client.DisconnectAsync();

        Assert.False(fake.DisconnectRequested);
    }

    [Fact]
    public async Task Disconnect_notifies_the_unit()
    {
        var fake = new FakeSpPro(ticksInRealTime: false);
        await using var client = new SpProClient(fake);
        await client.ConnectAsync();

        await client.DisconnectAsync();

        Assert.True(fake.DisconnectRequested);
        Assert.Null(client.Connection);
        Assert.Contains(fake.RequestLog, r => SpProFrame.Op(r) == FrameOp.Write && SpProFrame.Address(r) == SpProRegisters.Port1Disconnect);
    }

    [Fact]
    public async Task Silence_becomes_a_timeout_after_retries()
    {
        var silent = new SilentTransport();
        var channel = new FrameChannel(silent) { ResponseTimeout = TimeSpan.FromMilliseconds(50), Attempts = 2 };

        var ex = await Assert.ThrowsAsync<SpProTimeoutException>(() => channel.ExchangeAsync(SpProFrame.BuildRead(SpProRegisters.LinkPort, 1)));

        Assert.Contains("attempt 2/2", ex.Message);
        Assert.Equal(2, silent.Writes);
    }

    private sealed class SilentTransport : ISpProTransport
    {
        public int Writes { get; private set; }
        public string Description => "silent";

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            Writes++;
            return ValueTask.CompletedTask;
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public ValueTask DiscardInputAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
