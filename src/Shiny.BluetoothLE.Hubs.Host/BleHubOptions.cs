using Shiny.BluetoothLE;
using Shiny.BluetoothLE.Hubs.Protocol;

namespace Shiny.BluetoothLE.Hubs;

/// <summary>
/// Per hub settings
/// </summary>
public class BleHubOptions
{
    /// <summary>
    /// Maximum number of connected clients for this hub, null for no limit
    /// </summary>
    public int? MaxClients { get; set; } = 8;

    /// <summary>
    /// Optional gate for incoming clients - return a rejection reason to refuse, or null to accept
    /// </summary>
    public Func<HandshakeInfo, string?>? ValidateClient { get; set; }
}


/// <summary>
/// Settings shared by every hub on this device
/// </summary>
public class BleHubHostOptions
{
    /// <summary>
    /// Advertised local name, also sent to clients in the handshake. Keep it short - a 128-bit service UUID leaves little room
    /// in a 31 byte advertisement. Null to advertise without a name.
    /// </summary>
    public string? LocalName { get; set; }

    /// <summary>
    /// How often to sweep for clients that dropped without unsubscribing
    /// </summary>
    public TimeSpan ClientSweepInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// L2CAP file transfer settings, null when disabled
    /// </summary>
    public BleHubFileTransferOptions? FileTransfers { get; private set; }

    /// <summary>
    /// Serves file uploads and downloads over L2CAP. Uses a directory server unless an <see cref="IBleHubFileHandler"/> is registered.
    /// </summary>
    public BleHubHostOptions EnableFileTransfers(string rootDirectory, Action<BleHubFileTransferOptions>? configure = null)
    {
        var options = new BleHubFileTransferOptions(rootDirectory);
        configure?.Invoke(options);
        this.FileTransfers = options;
        return this;
    }
}


public class BleHubFileTransferOptions(string rootDirectory)
{
    /// <summary>
    /// Uploads land here and downloads are served from here. Peer supplied names can never escape it.
    /// </summary>
    public string RootDirectory { get; } = rootDirectory;

    public bool Secure { get; set; }
    public bool AllowUploads { get; set; } = true;
    public bool AllowDownloads { get; set; } = true;
    public bool OverwriteExistingUploads { get; set; } = true;
    public long? MaxUploadSize { get; set; }
    public L2CapTransferOptions? Transfer { get; set; }

    /// <summary>
    /// Return false to refuse a transfer (the peer gets NotPermitted)
    /// </summary>
    public Func<BleHubFileRequest, bool>? Authorize { get; set; }
}


/// <summary>
/// A file transfer request. <see cref="Client"/> is the hub client on the same device, when the L2CAP peer could be matched.
/// </summary>
public sealed record BleHubFileRequest(
    L2CapFileRequest Request,
    BleHubConnectedClient? Client
);


/// <summary>
/// Register to take over file transfers entirely (serve from a database, generate content, route per client...).
/// Every request must be answered with an Accept* or Reject call.
/// </summary>
public interface IBleHubFileHandler
{
    Task Handle(BleHubFileRequest request, CancellationToken cancellationToken);
}


public sealed record BleHubFileTransferredEventArgs(
    BleHubConnectedClient? Client,
    string PeerIdentifier,
    L2CapTransferType Type,
    string FileName,
    string LocalFilePath,
    long BytesTransferred,
    TimeSpan Elapsed
);

public sealed record BleHubFileProgressEventArgs(
    BleHubConnectedClient? Client,
    string PeerIdentifier,
    L2CapTransferType Type,
    string FileName,
    TransferProgress Progress
);


public sealed record BleHubRegistration(Type HubType, string ServiceUuid, string CharacteristicUuid, BleHubOptions Options);
