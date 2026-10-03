using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace SpLink.Protocol.Transport;

public sealed record SelectLiveSite(string Serial, string Name);

public class SelectLiveException(string message) : SpProException(message);

/// <summary>The select.live service rejected the account credentials.</summary>
public class SelectLiveAuthException(string message) : SelectLiveException(message);

/// <summary>
/// Remote access through a select.live unit, as SP LINK's "Select.live - Remote Connection" does: a TLS session to the
/// SP LINK gateway, a short line-based login/site-selection dialogue, then SP PRO frames pass straight through.
/// </summary>
public sealed class SelectLiveTransport : ISpProTransport
{
    public const string SelectLive2Host = "splink-api.selectlive.selectronic.com.au";
    public const string SelectLive1Host = "select.live";
    public const int DefaultPort = 7528;

    private readonly Stream _stream;
    private readonly IDisposable? _connection;
    private readonly byte[] _buffer = new byte[4096];
    private int _bufferStart;
    private int _bufferEnd;

    internal SelectLiveTransport(Stream stream, string description, IDisposable? connection = null)
    {
        _stream = stream;
        _connection = connection;
        Description = description;
    }

    public string Description { get; private set; }
    public IReadOnlyList<SelectLiveSite> Sites { get; private set; } = [];
    public string? ConnectedSerial { get; private set; }

    /// <summary>How long to wait for a reply to a gateway text command.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long <see cref="DiscardInputAsync"/> listens for stale bytes; the TLS stream cannot say whether any are pending.</summary>
    public TimeSpan DrainWindow { get; set; } = TimeSpan.FromMilliseconds(30);

    public Action<string>? Trace { get; set; }

    public static async Task<SelectLiveTransport> ConnectAsync(
        string username, string password, string host = SelectLive2Host, int port = DefaultPort,
        Action<string>? trace = null, CancellationToken cancellationToken = default)
    {
        var client = new TcpClient { NoDelay = true };
        SslStream? ssl = null;
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException)
        {
            ssl?.Dispose();
            client.Dispose();
            throw new SelectLiveException($"Cannot reach the select.live gateway at {host}:{port}: {ex.Message}");
        }

        var transport = new SelectLiveTransport(ssl, $"select.live gateway {host}:{port}", client) { Trace = trace };
        try
        {
            await transport.LoginAsync(username, password, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return transport;
    }

    internal async Task LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        var banner = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (!banner.Contains("LOGIN", StringComparison.Ordinal))
            throw new SelectLiveException($"Unexpected greeting from the gateway: '{banner}'");

        await WriteLineAsync($"USER:{username}:{password}", cancellationToken).ConfigureAwait(false);
        var reply = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (reply.Contains("REJECTED", StringComparison.Ordinal))
            throw new SelectLiveAuthException("select.live rejected the username/password");
    }

    public async Task<IReadOnlyList<SelectLiveSite>> ListSitesAsync(CancellationToken cancellationToken = default)
    {
        Sites = await ListAsync("LIST SITES", cancellationToken).ConfigureAwait(false);
        return Sites;
    }

    /// <summary>SP LINK's alternate listing; same reply format, typically serials only.</summary>
    public Task<IReadOnlyList<SelectLiveSite>> ListDevicesAsync(CancellationToken cancellationToken = default)
        => ListAsync("LIST DEVICES", cancellationToken);

    private async Task<IReadOnlyList<SelectLiveSite>> ListAsync(string command, CancellationToken cancellationToken)
    {
        await WriteLineAsync(command, cancellationToken).ConfigureAwait(false);
        var header = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        int at = header.IndexOf("DEVICES:", StringComparison.Ordinal);
        if (at < 0 || !int.TryParse(header.AsSpan(at + 8), out var count))
            throw new SelectLiveException($"Unexpected site list header from the gateway: '{header}'");

        var sites = new List<SelectLiveSite>(count);
        for (int i = 0; i < count; i++)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            int marker = line.IndexOf("DEVICE:", StringComparison.Ordinal);
            if (marker < 0) throw new SelectLiveException($"Unexpected site entry from the gateway: '{line}'");
            var body = line[(marker + 7)..];
            int bar = body.IndexOf('|');
            sites.Add(bar >= 0
                ? new SelectLiveSite(body[..bar].Trim(), body[(bar + 1)..].Trim())
                : new SelectLiveSite(body.Trim(), "{No Name}"));
        }
        return sites;
    }

    /// <summary>Asks the gateway to bridge to one SP PRO; afterwards the transport carries raw frames.</summary>
    public async Task SelectDeviceAsync(string serial, CancellationToken cancellationToken = default)
    {
        await WriteLineAsync($"CONNECT:{serial}", cancellationToken).ConfigureAwait(false);
        var reply = (await ReadLineAsync(cancellationToken).ConfigureAwait(false)).Trim();
        switch (reply)
        {
            case "READY":
                ConnectedSerial = serial;
                Description = $"select.live gateway → SP PRO {serial}";
                return;
            case "REJECTED":
                throw new SelectLiveException($"select.live refused access to SP PRO {serial}");
            case "OFFLINE":
                throw new SelectLiveException($"SP PRO {serial} is offline (its select.live unit is not connected)");
            case "BUSY":
                throw new SelectLiveException($"SP PRO {serial} is busy (another SP LINK session is connected)");
            default:
                throw new SelectLiveException($"Unexpected reply to CONNECT from the gateway: '{reply}'");
        }
    }

    private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        Trace?.Invoke(line.StartsWith("USER:", StringComparison.Ordinal) ? "SL> USER:***" : $"SL> {line}");
        var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        var line = new List<byte>();
        while (true)
        {
            while (_bufferStart < _bufferEnd)
            {
                byte b = _buffer[_bufferStart++];
                if (b == (byte)'\n')
                {
                    var text = Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
                    Trace?.Invoke($"SL< {text}");
                    return text;
                }
                line.Add(b);
            }
            try
            {
                await FillAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SelectLiveException($"No reply from the select.live gateway within {CommandTimeout.TotalSeconds:0} s");
            }
        }
    }

    private async Task FillAsync(CancellationToken cancellationToken)
    {
        _bufferStart = 0;
        _bufferEnd = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
        if (_bufferEnd == 0) throw new SelectLiveException("The select.live gateway closed the connection");
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_bufferStart >= _bufferEnd)
        {
            try { await FillAsync(cancellationToken).ConfigureAwait(false); }
            catch (SelectLiveException) { return 0; }
        }
        int count = Math.Min(buffer.Length, _bufferEnd - _bufferStart);
        _buffer.AsSpan(_bufferStart, count).CopyTo(buffer.Span);
        _bufferStart += count;
        return count;
    }

    public async ValueTask DiscardInputAsync(CancellationToken cancellationToken = default)
    {
        _bufferStart = _bufferEnd = 0;
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(DrainWindow);
        try
        {
            while (await _stream.ReadAsync(_buffer, window.Token).ConfigureAwait(false) > 0) { }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (IOException) { }
    }

    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        _connection?.Dispose();
        return ValueTask.CompletedTask;
    }
}
