using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE;

namespace Shiny.SmartBle;

public enum BleHubClientStatus
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting
}


/// <summary>
/// A host found while scanning
/// </summary>
public sealed record BleHubHostInfo(IPeripheral Peripheral, string? Name, int Rssi)
{
    public string Id => this.Peripheral.Uuid;
}


/// <summary>
/// What the client tells the host about itself during the handshake
/// </summary>
public sealed record BleHubConnectOptions(
    string? Name = null,
    string? AppVersion = null,
    Dictionary<string, string>? Properties = null
);


public sealed record BleHubStatusChangedEventArgs(BleHubClientStatus Status, string? Reason);


/// <summary>
/// Connection management shared by every generated hub proxy
/// </summary>
public interface IBleHubConnection
{
    BleHubClientStatus Status { get; }
    BleHubHostInfo? Host { get; }

    /// <summary>
    /// The host's advertised name, from the handshake
    /// </summary>
    string? HostName { get; }

    /// <summary>
    /// Whether the host serves L2CAP file transfers (and this device supports L2CAP)
    /// </summary>
    bool CanTransferFiles { get; }

    event EventHandler<BleHubStatusChangedEventArgs>? StatusChanged;
    event EventHandler? Connected;

    /// <summary>
    /// Raised with the reason (host disconnect, connection lost, you called Disconnect...)
    /// </summary>
    event EventHandler<string?>? Disconnected;

    /// <summary>
    /// Scans for hosts serving this hub. Dispose the subscription to stop scanning.
    /// </summary>
    IObservable<BleHubHostInfo> Discover();

    Task Connect(BleHubHostInfo host, BleHubConnectOptions? options = null, CancellationToken cancellationToken = default);
    Task Disconnect();

    Task<L2CapTransferResult> UploadFile(string localFilePath, string? remoteFileName = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<L2CapTransferResult> UploadStream(Stream source, long length, string remoteFileName, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<L2CapTransferResult> DownloadFile(string remoteFileName, string localFilePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
}


/// <summary>
/// A connection to one hub plus its typed proxy
/// </summary>
public interface IBleHubClient<out TContract> : IBleHubConnection where TContract : class
{
    /// <summary>
    /// Call hub methods and subscribe to hub events here
    /// </summary>
    TContract Hub { get; }
}


/// <summary>
/// Services the generated proxies need - registered by AddBleHubClient
/// </summary>
public sealed class BleHubClientServices(
    SmartBleOptions options,
    ISmartBleSerializer serializer,
    IBleManager? bleManager = null,
    ILoggerFactory? loggerFactory = null
)
{
    public SmartBleOptions Options { get; } = options;
    public ISmartBleSerializer Serializer { get; } = serializer;
    public IBleManager? BleManager { get; } = bleManager;
    public ILoggerFactory? LoggerFactory { get; } = loggerFactory;
    internal BleConnectionTracker Connections { get; } = new();
}


/// <summary>
/// Several hub clients can share one BLE connection to a host - only the last one out disconnects
/// </summary>
internal sealed class BleConnectionTracker
{
    readonly Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);

    public void Acquire(string peripheralId)
    {
        lock (this.counts)
            this.counts[peripheralId] = this.counts.TryGetValue(peripheralId, out var c) ? c + 1 : 1;
    }

    /// <summary>
    /// Returns true when nobody else is using the connection
    /// </summary>
    public bool Release(string peripheralId)
    {
        lock (this.counts)
        {
            if (!this.counts.TryGetValue(peripheralId, out var c) || c <= 1)
            {
                this.counts.Remove(peripheralId);
                return true;
            }
            this.counts[peripheralId] = c - 1;
            return false;
        }
    }
}
