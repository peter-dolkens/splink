namespace SpLink.Protocol.Transport;

/// <summary>A byte pipe to an SP PRO: USB/RS-232 serial, a serial-to-Ethernet adaptor, the select.live gateway, or a simulator.</summary>
public interface ISpProTransport : IAsyncDisposable
{
    string Description { get; }

    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>Reads at least one byte, waiting until data arrives or <paramref name="cancellationToken"/> fires. Returns 0 if the link closed.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>Drops any unread input so the next response is read from a clean buffer.</summary>
    ValueTask DiscardInputAsync(CancellationToken cancellationToken = default);
}
