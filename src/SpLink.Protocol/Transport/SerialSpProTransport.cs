using System.IO.Ports;

namespace SpLink.Protocol.Transport;

/// <summary>Serial transport (USB CDC, FTDI via the OS driver, or RS-232) using the same 8N1/no-handshake/RTS+DTR setup as SP LINK.</summary>
public sealed class SerialSpProTransport : ISpProTransport
{
    private readonly SerialPort _port;

    private SerialSpProTransport(SerialPort port) => _port = port;

    public string Description => $"serial {_port.PortName} @ {_port.BaudRate} baud";
    public string PortName => _port.PortName;
    public int BaudRate => _port.BaudRate;

    public static SerialSpProTransport Open(string portName, int baudRate)
    {
        var port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            RtsEnable = true,
            DtrEnable = true,
            ReadTimeout = 1000,
            WriteTimeout = 1000,
        };
        try
        {
            port.Open();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
        {
            port.Dispose();
            throw new SpProException($"Cannot open {portName}: {ex.Message}", ex);
        }
        port.DiscardInBuffer();
        port.DiscardOutBuffer();
        return new SerialSpProTransport(port);
    }

    /// <summary>
    /// Mirrors SP LINK's auto-baud scan: open at each candidate rate, send a link-port read, and keep the first
    /// rate that produces a valid response (about one second per attempt).
    /// </summary>
    public static async Task<SerialSpProTransport> OpenWithAutoBaudAsync(
        string portName, int? preferredBaud = null, Action<string>? trace = null, CancellationToken cancellationToken = default)
    {
        IEnumerable<int> candidates = SpProRegisters.AutoBaudCandidates;
        if (preferredBaud is int preferred)
            candidates = new[] { preferred }.Concat(candidates.Where(b => b != preferred));

        var probe = SpProFrame.BuildRead(SpProRegisters.LinkPort, 1);
        foreach (var baud in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SerialSpProTransport? transport = null;
            try
            {
                transport = Open(portName, baud);
                var channel = new FrameChannel(transport) { Attempts = 1, ResponseTimeout = TimeSpan.FromSeconds(1), Trace = trace };
                await channel.ExchangeAsync(probe, cancellationToken).ConfigureAwait(false);
                trace?.Invoke($"-- SP PRO answered at {baud} baud");
                return transport;
            }
            catch (SpProException ex) when (transport is not null)
            {
                // Opened fine but nothing (valid) came back: wrong rate, try the next one.
                trace?.Invoke($"-- {baud} baud: {ex.Message}");
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
            {
                // The OS or driver refused this rate (e.g. 128000 on some termios builds): skip it.
                trace?.Invoke($"-- {baud} baud not usable here: {ex.Message}");
                if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
            }
        }
        throw new SpProTimeoutException($"No SP PRO answered on {portName} at any supported baud rate");
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        await _port.BaseStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await _port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _port.BaseStream.ReadAsync(buffer, cancellationToken);

    public ValueTask DiscardInputAsync(CancellationToken cancellationToken = default)
    {
        try { _port.DiscardInBuffer(); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try { if (_port.IsOpen) _port.Close(); } catch (IOException) { }
        _port.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Serial device nodes that could be an SP PRO, with Bluetooth/debug ports and macOS tty.* duplicates removed.</summary>
    public static IReadOnlyList<string> ListCandidatePorts()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        try { foreach (var name in SerialPort.GetPortNames()) names.Add(name); } catch (Exception) { }

        if (!OperatingSystem.IsWindows())
        {
            foreach (var pattern in new[] { "cu.*", "ttyUSB*", "ttyACM*" })
            {
                try { foreach (var path in Directory.EnumerateFiles("/dev", pattern)) names.Add(path); } catch (Exception) { }
            }
        }

        // On macOS /dev/tty.X and /dev/cu.X are the same device; the call-out node is the one to use for an initiating connection.
        if (OperatingSystem.IsMacOS())
            names.RemoveWhere(n => n.StartsWith("/dev/tty.", StringComparison.Ordinal));
        names.RemoveWhere(n => n.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("debug-console", StringComparison.OrdinalIgnoreCase));
        return names.ToList();
    }
}
