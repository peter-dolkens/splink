using System.Net.Sockets;

namespace SpLink.Protocol.Transport;

/// <summary>Raw TCP transport for the SP PRO Ethernet adaptor (a serial-to-Ethernet bridge; the byte stream is identical to serial).</summary>
public sealed class TcpSpProTransport : ISpProTransport
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    private TcpSpProTransport(TcpClient client, string host, int port)
    {
        _client = client;
        _stream = client.GetStream();
        Description = $"tcp {host}:{port}";
    }

    public string Description { get; }

    public static async Task<TcpSpProTransport> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var client = new TcpClient { NoDelay = true, ReceiveBufferSize = 4096, SendBufferSize = 4096 };
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            client.Dispose();
            throw new SpProException($"Cannot connect to {host}:{port}: {ex.Message}", ex);
        }
        return new TcpSpProTransport(client, host, port);
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => _stream.WriteAsync(data, cancellationToken);

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _stream.ReadAsync(buffer, cancellationToken);

    public ValueTask DiscardInputAsync(CancellationToken cancellationToken = default)
    {
        var junk = new byte[4096];
        try { while (_stream.DataAvailable && _stream.Read(junk) > 0) { } } catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
