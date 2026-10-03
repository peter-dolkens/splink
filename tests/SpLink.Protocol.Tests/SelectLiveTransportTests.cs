using System.IO.Pipelines;
using System.Text;
using SpLink.Protocol;
using SpLink.Protocol.Simulation;
using SpLink.Protocol.Transport;
using Xunit;

namespace SpLink.Protocol.Tests;

public class SelectLiveTransportTests
{
    [Fact]
    public async Task Logs_in_lists_sites_selects_a_device_and_tunnels_sp_pro_frames()
    {
        using var harness = new GatewayHarness();
        var transport = harness.Transport;

        await transport.LoginAsync("peter", "secret", CancellationToken.None);

        var sites = await transport.ListSitesAsync();
        Assert.Equal([new SelectLiveSite("240001", "Home"), new SelectLiveSite("240002", "Shed")], sites);

        await transport.SelectDeviceAsync("240001");
        Assert.Equal("240001", transport.ConnectedSerial);

        await using var client = new SpProClient(transport, allowWrites: true);
        var info = await client.ConnectAsync();
        Assert.True(info.LoggedIn);

        var reading = await client.ReadClockAsync();
        Assert.Equal(new DateTime(2026, 10, 3, 12, 0, 0), reading.Time);

        await client.SetClockAsync(new DateTime(2026, 10, 4, 8, 15, 0));
        Assert.Equal(new DateTime(2026, 10, 4, 8, 15, 0), harness.Gateway.Inverter.Clock);
    }

    [Fact]
    public async Task Rejected_credentials_are_an_auth_error()
    {
        using var harness = new GatewayHarness();
        await Assert.ThrowsAsync<SelectLiveAuthException>(() => harness.Transport.LoginAsync("peter", "wrong", CancellationToken.None));
    }

    [Fact]
    public async Task Offline_device_is_reported()
    {
        using var harness = new GatewayHarness();
        await harness.Transport.LoginAsync("peter", "secret", CancellationToken.None);

        var ex = await Assert.ThrowsAsync<SelectLiveException>(() => harness.Transport.SelectDeviceAsync("240002"));
        Assert.Contains("offline", ex.Message);
    }

    [Fact]
    public async Task Unparseable_site_entries_without_a_name_get_a_placeholder()
    {
        using var harness = new GatewayHarness();
        harness.Gateway.OmitSiteNames = true;
        await harness.Transport.LoginAsync("peter", "secret", CancellationToken.None);

        var sites = await harness.Transport.ListSitesAsync();
        Assert.All(sites, s => Assert.Equal("{No Name}", s.Name));
    }

    /// <summary>Wires a <see cref="SelectLiveTransport"/> to a scripted gateway over in-memory pipes.</summary>
    private sealed class GatewayHarness : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _gatewayTask;

        public GatewayHarness()
        {
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var clientStream = new DuplexStream(serverToClient.Reader.AsStream(), clientToServer.Writer.AsStream());
            var serverStream = new DuplexStream(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());

            Transport = new SelectLiveTransport(clientStream, "test gateway") { CommandTimeout = TimeSpan.FromSeconds(5) };
            Gateway = new FakeGateway();
            _gatewayTask = Gateway.RunAsync(serverStream, _cts.Token);
        }

        public SelectLiveTransport Transport { get; }
        public FakeGateway Gateway { get; }

        public void Dispose()
        {
            _cts.Cancel();
            try { _gatewayTask.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            Transport.DisposeAsync().AsTask().Wait();
        }
    }

    private sealed class FakeGateway
    {
        public FakeSpPro Inverter { get; } = new(new DateTime(2026, 10, 3, 12, 0, 0), ticksInRealTime: false);
        public bool OmitSiteNames { get; set; }
        private static readonly (string Serial, string Name)[] Sites = [("240001", "Home"), ("240002", "Shed")];

        public async Task RunAsync(Stream stream, CancellationToken ct)
        {
            await WriteAsync(stream, "LOGIN select.live gateway\r\n", ct);
            while (!ct.IsCancellationRequested)
            {
                var line = await ReadLineAsync(stream, ct);
                if (line is null) return;

                if (line.StartsWith("USER:", StringComparison.Ordinal))
                {
                    await WriteAsync(stream, line == "USER:peter:secret" ? "OK\r\n" : "REJECTED\r\n", ct);
                }
                else if (line == "LIST SITES")
                {
                    var sb = new StringBuilder($"DEVICES:{Sites.Length}\r\n");
                    foreach (var (serial, name) in Sites)
                        sb.Append(OmitSiteNames ? $"DEVICE:{serial}\r\n" : $"DEVICE:{serial}|{name}\r\n");
                    await WriteAsync(stream, sb.ToString(), ct);
                }
                else if (line.StartsWith("CONNECT:", StringComparison.Ordinal))
                {
                    if (line != "CONNECT:240001")
                    {
                        await WriteAsync(stream, "OFFLINE\r\n", ct);
                        continue;
                    }
                    await WriteAsync(stream, "READY\r\n", ct);
                    await BridgeFramesAsync(stream, ct);
                    return;
                }
            }
        }

        private async Task BridgeFramesAsync(Stream stream, CancellationToken ct)
        {
            var inbound = new byte[1024];
            var outbound = new byte[1024];
            while (!ct.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(inbound, ct);
                if (read == 0) return;
                await Inverter.WriteAsync(inbound.AsMemory(0, read), ct);
                int written = await Inverter.ReadAsync(outbound, ct);
                await stream.WriteAsync(outbound.AsMemory(0, written), ct);
                await stream.FlushAsync(ct);
            }
        }

        private static async Task WriteAsync(Stream stream, string text, CancellationToken ct)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
            await stream.FlushAsync(ct);
        }

        private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
        {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                int read = await stream.ReadAsync(one, ct);
                if (read == 0) return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
                if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
                bytes.Add(one[0]);
            }
        }
    }

    private sealed class DuplexStream(Stream input, Stream output) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => input.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => input.ReadAsync(buffer, offset, count, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => output.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => output.WriteAsync(buffer, offset, count, cancellationToken);
        public override void Flush() => output.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) { input.Dispose(); output.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
