using System.Text.Json;

namespace Shiny.SmartBle.Protocol;

/// <summary>
/// Protocol models are always JSON via an internal source generated context - independent of the app's serializer
/// </summary>
internal static class ProtocolSerializer
{
    public static byte[] Serialize(HandshakeInfo value) => JsonSerializer.SerializeToUtf8Bytes(value, SmartBleProtocolJsonContext.Default.HandshakeInfo);
    public static byte[] Serialize(HandshakeAck value) => JsonSerializer.SerializeToUtf8Bytes(value, SmartBleProtocolJsonContext.Default.HandshakeAck);
    public static byte[] Serialize(RemoteError value) => JsonSerializer.SerializeToUtf8Bytes(value, SmartBleProtocolJsonContext.Default.RemoteError);
    public static byte[] Serialize(DisconnectInfo value) => JsonSerializer.SerializeToUtf8Bytes(value, SmartBleProtocolJsonContext.Default.DisconnectInfo);

    public static HandshakeInfo ReadHandshake(ReadOnlyMemory<byte> data) => JsonSerializer.Deserialize(data.Span, SmartBleProtocolJsonContext.Default.HandshakeInfo)!;
    public static HandshakeAck ReadHandshakeAck(ReadOnlyMemory<byte> data) => JsonSerializer.Deserialize(data.Span, SmartBleProtocolJsonContext.Default.HandshakeAck)!;
    public static RemoteError ReadError(ReadOnlyMemory<byte> data) => JsonSerializer.Deserialize(data.Span, SmartBleProtocolJsonContext.Default.RemoteError)!;

    public static DisconnectInfo ReadDisconnect(ReadOnlyMemory<byte> data)
        => data.Length == 0 ? new DisconnectInfo(null) : JsonSerializer.Deserialize(data.Span, SmartBleProtocolJsonContext.Default.DisconnectInfo)!;
}
