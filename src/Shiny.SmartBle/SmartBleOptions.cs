namespace Shiny.SmartBle;

/// <summary>
/// Protocol limits shared by hosts and clients. Register with ConfigureSmartBle.
/// </summary>
public class SmartBleOptions
{
    /// <summary>
    /// Largest payload accepted for a single message. Bigger content belongs in a file transfer.
    /// </summary>
    public int MaxPayloadSize { get; set; } = 256 * 1024;

    /// <summary>
    /// How long a partially received message is kept before it is dropped
    /// </summary>
    public TimeSpan ReassemblyTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Default timeout for a hub invocation (streams are not timed out)
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Partial messages allowed in flight per peer
    /// </summary>
    public int MaxPartialMessages { get; set; } = 16;
}
