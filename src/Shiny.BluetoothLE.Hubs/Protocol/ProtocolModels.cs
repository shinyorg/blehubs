using System.Text.Json.Serialization;

namespace Shiny.BluetoothLE.Hubs.Protocol;

/// <summary>
/// Sent by the client once it is subscribed to the outbox
/// </summary>
public sealed record HandshakeInfo(
    int ProtocolVersion,
    string? Name,
    string? AppVersion,
    Dictionary<string, string>? Properties
);

/// <summary>
/// The host's answer to a handshake
/// </summary>
/// <param name="Accepted">Whether the client may use the hub</param>
/// <param name="Reason">Why the client was refused</param>
/// <param name="HostName">The host's advertised local name</param>
/// <param name="FileTransferPsm">The L2CAP PSM serving file transfers, 0 when file transfers are not available</param>
/// <param name="FileTransferSecure">Whether the L2CAP channel must be opened secure (Android listens on secure and insecure channels separately)</param>
public sealed record HandshakeAck(
    bool Accepted,
    string? Reason,
    string? HostName,
    ushort FileTransferPsm,
    bool FileTransferSecure
);

/// <summary>
/// A handler failure relayed to the caller
/// </summary>
public sealed record RemoteError(
    string ErrorType,
    string Message
);

/// <summary>
/// The host is ending the session
/// </summary>
public sealed record DisconnectInfo(string? Reason);


[JsonSerializable(typeof(HandshakeInfo))]
[JsonSerializable(typeof(HandshakeAck))]
[JsonSerializable(typeof(RemoteError))]
[JsonSerializable(typeof(DisconnectInfo))]
internal partial class BleHubProtocolJsonContext : JsonSerializerContext;
