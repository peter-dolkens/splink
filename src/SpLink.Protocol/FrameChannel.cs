using SpLink.Protocol.Transport;

namespace SpLink.Protocol;

/// <summary>Request/response exchange over a transport with SP LINK-style timeouts, retries and resynchronisation.</summary>
public sealed class FrameChannel(ISpProTransport transport)
{
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(1);
    public int Attempts { get; set; } = 3;

    /// <summary>Receives a line per frame sent/received and per retry, for diagnostics.</summary>
    public Action<string>? Trace { get; set; }

    public async Task<byte[]> ExchangeAsync(byte[] request, CancellationToken cancellationToken = default)
    {
        int expected = SpProFrame.ResponseLengthFromHeader(request);
        SpProException? last = null;

        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await transport.DiscardInputAsync(cancellationToken).ConfigureAwait(false);
            Trace?.Invoke($"TX {SpProFrame.ToHex(request)}");
            await transport.WriteAsync(request, cancellationToken).ConfigureAwait(false);

            byte[] response;
            try
            {
                response = await ReadFrameAsync(request, expected, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                last = new SpProTimeoutException($"No response within {ResponseTimeout.TotalMilliseconds:0} ms (attempt {attempt}/{Attempts})");
                Trace?.Invoke($"-- {last.Message}");
                continue;
            }

            Trace?.Invoke($"RX {SpProFrame.ToHex(response)}");
            if (!SpProFrame.TryValidate(response, out var error))
            {
                last = new SpProProtocolException($"Invalid response: {error} (attempt {attempt}/{Attempts})");
                Trace?.Invoke($"-- {last.Message}");
                continue;
            }
            if (response[0] != request[0] || SpProFrame.Address(response) != SpProFrame.Address(request))
            {
                last = new SpProProtocolException($"Response header does not match request (attempt {attempt}/{Attempts})");
                Trace?.Invoke($"-- {last.Message}");
                continue;
            }
            return response;
        }

        throw last ?? new SpProTimeoutException("No response");
    }

    private async Task<byte[]> ReadFrameAsync(byte[] request, int expected, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResponseTimeout);

        var frame = new byte[expected];
        var chunk = new byte[expected];
        int have = 0;
        while (have < expected)
        {
            int read = await transport.ReadAsync(chunk.AsMemory(0, expected - have), timeout.Token).ConfigureAwait(false);
            if (read <= 0) throw new SpProProtocolException("The connection was closed while waiting for a response");
            for (int i = 0; i < read && have < expected; i++)
            {
                if (have == 0 && !SpProFrame.IsStartByte(chunk[i]))
                {
                    // Some network gateways hand back the first two header bytes zeroed. SP LINK repairs that when the
                    // address bytes still match the request; do the same, otherwise skip to the next 'Q'/'W' start byte.
                    if (i + 5 < read && chunk.AsSpan(i + 2, 4).SequenceEqual(request.AsSpan(2, 4)))
                    {
                        frame[have++] = request[0];
                        frame[have++] = request[1];
                        i++;
                        Trace?.Invoke("-- repaired mangled response header");
                    }
                    continue;
                }
                frame[have++] = chunk[i];
            }
        }
        return frame;
    }
}
