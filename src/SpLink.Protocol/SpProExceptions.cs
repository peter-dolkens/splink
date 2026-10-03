namespace SpLink.Protocol;

public class SpProException : Exception
{
    public SpProException(string message) : base(message) { }
    public SpProException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>No (valid) response arrived within the allowed time and retries.</summary>
public class SpProTimeoutException(string message) : SpProException(message);

/// <summary>A response arrived but did not match the protocol or the request.</summary>
public class SpProProtocolException(string message) : SpProException(message);

/// <summary>The SP PRO rejected the login password.</summary>
public class SpProLoginException(string message) : SpProException(message);

/// <summary>The unit refused the connection because it is a non-L1 member of a Powerchain system.</summary>
public class SpProMultiPhaseBlockedException(string message) : SpProException(message);

public class SpProClockException(string message, Exception inner) : SpProException(message, inner);

/// <summary>A write was attempted on a client that is in read-only mode.</summary>
public class SpProReadOnlyException(string message) : SpProException(message);
