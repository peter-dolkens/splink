using System.Net;
using System.Text;
using SpLink.Fronius;
using Xunit;

namespace SpLink.Protocol.Tests;

public class FroniusTests
{
    /// <summary>A real GetInverterRealtimeData response, including the nulls a Fronius actually returns.</summary>
    private const string RealResponse = """
    {"Body":{"Data":{
      "DAY_ENERGY":{"Unit":"Wh","Value":null},
      "DeviceStatus":{"ErrorCode":1130,"InverterState":"Running","StatusCode":7},
      "FAC":{"Unit":"Hz","Value":50.00068664550781},
      "IAC":{"Unit":"A","Value":0.8940709829330444},
      "IDC":{"Unit":"A","Value":0.008169181644916534},
      "IDC_2":{"Unit":"A","Value":0.7884265184402466},
      "IDC_3":{"Unit":"A","Value":null},
      "PAC":{"Unit":"W","Value":200.6156768798828},
      "SAC":{"Unit":"VA","Value":215.1112823486328},
      "TOTAL_ENERGY":{"Unit":"Wh","Value":11122.9},
      "UAC":{"Unit":"V","Value":240.58},
      "UDC":{"Unit":"V","Value":330.9},
      "UDC_2":{"Unit":"V","Value":359.6}
    }},"Head":{"Status":{"Code":0,"Reason":"","UserMessage":""},"Timestamp":"2026-10-03T05:28:17+00:00"}}
    """;

    [Fact]
    public async Task Decodes_a_real_solar_api_response_including_nulls()
    {
        await using var server = new FakeFronius(RealResponse);
        using var http = new HttpClient();
        var reading = await new FroniusClient(http).ReadAsync(server.Host);

        Assert.Equal(200.616, reading.AcPowerWatts!.Value, 3);
        Assert.Equal(240.58, reading.AcVolts!.Value, 2);
        Assert.Equal(50.0007, reading.AcFrequencyHz!.Value, 4);
        Assert.Equal(11122.9, reading.TotalEnergyWh!.Value, 1);
        // The inverter genuinely reports null here; it must not become zero.
        Assert.Null(reading.DayEnergyWh);
        Assert.Equal("Running", reading.InverterState);
        Assert.Equal(1130, reading.ErrorCode);
        Assert.True(reading.HasError);

        // Only the two populated strings should appear; IDC_3/UDC_3 are null.
        Assert.Equal(2, reading.Strings.Count);
        Assert.Equal(330.9, reading.Strings[0].Volts!.Value, 1);
        Assert.Equal(359.6 * 0.7884265184402466, reading.Strings[1].Watts!.Value, 3);
    }

    [Fact]
    public async Task An_unreachable_inverter_is_an_error_not_a_crash()
    {
        var service = new FroniusService([new FroniusInverterConfig("dead", "127.0.0.1:1")], TimeSpan.FromMilliseconds(400));
        await using (service)
        {
            var results = await service.ReadAllAsync();

            var dead = Assert.Single(results);
            Assert.False(dead.Healthy);
            Assert.NotNull(dead.Error);
            Assert.Null(dead.Reading);
        }
    }

    [Fact]
    public async Task One_inverter_failing_does_not_affect_the_others()
    {
        await using var good = new FakeFronius(RealResponse);
        var service = new FroniusService(
            [new FroniusInverterConfig("good", good.Host), new FroniusInverterConfig("dead", "127.0.0.1:1")],
            TimeSpan.FromMilliseconds(500));

        await using (service)
        {
            var results = (await service.ReadAllAsync()).ToDictionary(r => r.Name);

            Assert.True(results["good"].Healthy);
            Assert.Equal(200.616, results["good"].Reading!.AcPowerWatts!.Value, 3);
            Assert.False(results["dead"].Healthy);
        }
    }

    [Fact]
    public async Task Malformed_json_is_reported_rather_than_thrown()
    {
        await using var server = new FakeFronius("not json at all");
        var service = new FroniusService([new FroniusInverterConfig("bad", server.Host)]);
        await using (service)
        {
            var result = Assert.Single(await service.ReadAllAsync());
            Assert.False(result.Healthy);
            Assert.NotNull(result.Error);
        }
    }

    [Fact]
    public async Task Repeated_reads_inside_the_cache_window_hit_the_inverter_once()
    {
        await using var server = new FakeFronius(RealResponse);
        var service = new FroniusService([new FroniusInverterConfig("a", server.Host)]) { CacheFor = TimeSpan.FromSeconds(30) };
        await using (service)
        {
            await service.ReadAllAsync();
            await service.ReadAllAsync();
            await service.ReadAllAsync();
            // One live read plus the one-off identity lookup.
            Assert.Equal(2, server.Requests);
        }
    }

    [Fact]
    public async Task Concurrent_callers_do_not_stampede_the_inverters()
    {
        // The bridge is published publicly, so request volume is not under our control.
        // Twenty simultaneous callers must still produce exactly one round of inverter traffic.
        await using var server = new FakeFronius(RealResponse) { DelayMs = 150 };
        var service = new FroniusService([new FroniusInverterConfig("a", server.Host)]);
        await using (service)
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => service.ReadAllAsync()));

            Assert.All(results, r => Assert.True(r.Single().Healthy));
            // One live read plus the one-off identity lookup — not twenty of each.
            Assert.Equal(2, server.Requests);
        }
    }

    [Fact]
    public async Task A_cancelled_caller_does_not_abort_the_shared_fetch()
    {
        await using var server = new FakeFronius(RealResponse) { DelayMs = 200 };
        var service = new FroniusService([new FroniusInverterConfig("a", server.Host)]);
        await using (service)
        {
            using var cts = new CancellationTokenSource();
            var giverUpper = service.ReadAllAsync(cts.Token);
            var stayer = service.ReadAllAsync();
            await cts.CancelAsync();

            // The shared fetch is not bound to any one caller's token, so it still completes.
            Assert.True((await stayer).Single().Healthy);
        }
    }

    [Theory]
    [InlineData("a=1.2.3.4", 1)]
    [InlineData("a=1.2.3.4,b=5.6.7.8", 2)]
    public void Inline_configuration_parses(string spec, int expected)
        => Assert.Equal(expected, BridgeConfig.ParseInline(spec).Count);

    [Theory]
    [InlineData("noequals")]
    [InlineData("=1.2.3.4")]
    [InlineData("name=")]
    public void Malformed_inline_configuration_is_rejected(string spec)
        => Assert.Throws<FroniusException>(() => BridgeConfig.ParseInline(spec));

    /// <summary>A minimal stand-in for a Fronius inverter's Solar API.</summary>
    private sealed class FakeFronius : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private int _requests;

        /// <summary>Artificial latency, so concurrency behaviour is observable.</summary>
        public int DelayMs { get; init; }

        public FakeFronius(string body)
        {
            var port = Random.Shared.Next(20000, 40000);
            Host = $"127.0.0.1:{port}";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); } catch (Exception) { return; }
                    Interlocked.Increment(ref _requests);
                    if (DelayMs > 0) await Task.Delay(DelayMs);
                    var bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    try { ctx.Response.OutputStream.Write(bytes); ctx.Response.Close(); } catch (Exception) { }
                }
            });
        }

        public string Host { get; }
        public int Requests => Volatile.Read(ref _requests);

        public ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch (Exception) { }
            _cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
