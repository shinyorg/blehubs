namespace Shiny.SmartBle;

/// <summary>
/// Base for all SmartBle failures
/// </summary>
public class SmartBleException : Exception
{
    public SmartBleException(string message) : base(message) { }
    public SmartBleException(string message, Exception? inner) : base(message, inner) { }
}


/// <summary>
/// A malformed or unexpected frame was received
/// </summary>
public class SmartBleProtocolException(string message, ushort? messageId = null) : SmartBleException(message)
{
    public ushort? MessageId { get; } = messageId;
}


/// <summary>
/// The remote handler threw - the original exception type and message are carried across
/// </summary>
public class SmartBleRemoteException(string remoteErrorType, string message) : SmartBleException(message)
{
    public string RemoteErrorType { get; } = remoteErrorType;
}


/// <summary>
/// The connection dropped (or was ended by the host) while a call was in flight
/// </summary>
public class SmartBleDisconnectedException(string message, string? reason = null) : SmartBleException(message)
{
    public string? Reason { get; } = reason;
}


/// <summary>
/// The connected host does not serve file transfers (disabled, or L2CAP is unavailable on its platform)
/// </summary>
public class SmartBleFileTransferNotSupportedException(string message) : SmartBleException(message);
