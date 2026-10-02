namespace Shiny.BluetoothLE.Hubs;

/// <summary>
/// Base for all BLE hub failures
/// </summary>
public class BleHubException : Exception
{
    public BleHubException(string message) : base(message) { }
    public BleHubException(string message, Exception? inner) : base(message, inner) { }
}


/// <summary>
/// A malformed or unexpected frame was received
/// </summary>
public class BleHubProtocolException(string message, ushort? messageId = null) : BleHubException(message)
{
    public ushort? MessageId { get; } = messageId;
}


/// <summary>
/// The remote handler threw - the original exception type and message are carried across
/// </summary>
public class BleHubRemoteException(string remoteErrorType, string message) : BleHubException(message)
{
    public string RemoteErrorType { get; } = remoteErrorType;
}


/// <summary>
/// The connection dropped (or was ended by the host) while a call was in flight
/// </summary>
public class BleHubDisconnectedException(string message, string? reason = null) : BleHubException(message)
{
    public string? Reason { get; } = reason;
}


/// <summary>
/// The connected host does not serve file transfers (disabled, or L2CAP is unavailable on its platform)
/// </summary>
public class BleHubFileTransferNotSupportedException(string message) : BleHubException(message);
