using System.ComponentModel;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE;
using Shiny.SmartBle.Protocol;

namespace Shiny.SmartBle;

/// <summary>
/// Base class for the generated hub proxies - owns the BLE connection, handshake, call correlation, push dispatch and files
/// </summary>
public abstract class BleHubClient : IBleHubConnection, IDisposable
{
    readonly BleHubClientServices services;
    readonly ILogger? logger;
    readonly HubClientProtocol protocol;
    readonly SemaphoreSlim connectLock = new(1, 1);
    Connection? connection;


    protected BleHubClient(BleHubClientServices services, string serviceUuid, string characteristicUuid)
    {
        this.services = services;
        this.ServiceUuid = BleUuid.Normalize(serviceUuid, nameof(serviceUuid));
        this.CharacteristicUuid = BleUuid.Normalize(characteristicUuid, nameof(characteristicUuid));
        this.logger = services.LoggerFactory?.CreateLogger(this.GetType().FullName!);

        this.protocol = new HubClientProtocol(services.Options, this.Write, this.logger);
        this.protocol.Pushed += message => this.connection?.Pushes.Writer.TryWrite(message);
        this.protocol.DisconnectRequested += info =>
        {
            this.logger?.LogInformation("Host ended the session: {Reason}", info.Reason);
            _ = this.Teardown(info.Reason ?? "Disconnected by host", true);
        };
    }


    public string ServiceUuid { get; }
    public string CharacteristicUuid { get; }
    public BleHubClientStatus Status { get; private set; } = BleHubClientStatus.Disconnected;
    public BleHubHostInfo? Host => this.connection?.Host;
    public string? HostName => this.connection?.Ack?.HostName;

    public bool CanTransferFiles =>
        this.Status == BleHubClientStatus.Connected
        && this.connection?.Ack?.FileTransferPsm > 0
        && this.connection.Host?.Peripheral.IsL2CapAvailable() == true;

    public event EventHandler<BleHubStatusChangedEventArgs>? StatusChanged;
    public event EventHandler? Connected;
    public event EventHandler<string?>? Disconnected;


    public IObservable<BleHubHostInfo> Discover()
    {
        var ble = this.services.BleManager ?? throw new InvalidOperationException("IBleManager is not registered - call AddBluetoothLE()");
        return Observable
            .FromAsync(() => ble.RequestAccessAsync())
            .SelectMany(access => access == AccessState.Available
                ? ble.Scan(new ScanConfig(this.ServiceUuid))
                : Observable.Throw<ScanResult>(new SmartBleException($"Bluetooth is not available ({access})"))
            )
            .Select(x => new BleHubHostInfo(x.Peripheral, x.AdvertisementData?.LocalName ?? x.Peripheral.Name, x.Rssi));
    }


    public async Task Connect(BleHubHostInfo host, BleHubConnectOptions? options = null, CancellationToken cancellationToken = default)
    {
        var ble = this.services.BleManager ?? throw new InvalidOperationException("IBleManager is not registered - call AddBluetoothLE()");
        await this.connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.AssertDisconnected();
            if (ble.IsScanning)
                ble.StopScan();

            var p = host.Peripheral;
            var conn = this.Begin(host);
            this.services.Connections.Acquire(p.Uuid);
            conn.OwnsBleConnection = true;

            await p.ConnectAsync(new ConnectionConfig(false), cancellationToken).ConfigureAwait(false);
            try
            {
                await p.TryRequestMtuAsync(512, cancelToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger?.LogDebug(ex, "MTU request failed - continuing with {Mtu}", p.Mtu);
            }

            // fail fast if the host does not serve this hub
            await p.GetCharacteristicAsync(this.ServiceUuid, this.CharacteristicUuid, cancellationToken).ConfigureAwait(false);

            var subscribed = p.WaitForCharacteristicSubscriptionAsync(this.ServiceUuid, this.CharacteristicUuid, cancellationToken);
            conn.Subscriptions.Add(p
                .NotifyCharacteristic(this.ServiceUuid, this.CharacteristicUuid, false)
                .Subscribe(
                    r =>
                    {
                        if (r.Data is { Length: > 0 } data)
                            this.protocol.OnFrame(data);
                    },
                    ex => this.logger?.LogWarning(ex, "Notification subscription failed")
                ));
            await subscribed.ConfigureAwait(false);

            conn.Subscriptions.Add(p
                .WhenStatusChanged()
                .Where(x => x == ConnectionState.Disconnected)
                .Subscribe(__ => { _ = this.Teardown("Connection lost", false); }));

            conn.Write = (frame, ct) => p.WriteCharacteristicAsync(this.ServiceUuid, this.CharacteristicUuid, frame, true, ct, 10000);
            await this.Complete(conn, p.Mtu, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await this.Teardown(ex.Message, true).ConfigureAwait(false);
            throw;
        }
        finally
        {
            this.connectLock.Release();
        }
    }


    /// <summary>
    /// Connects over a caller supplied transport (tests / in-memory). Frames from the host go to <see cref="ReceiveFrame"/>.
    /// </summary>
    internal async Task ConnectTransport(Func<byte[], CancellationToken, Task> write, int mtu, BleHubConnectOptions? options, CancellationToken cancellationToken)
    {
        await this.connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.AssertDisconnected();
            var conn = this.Begin(null);
            conn.Write = write;
            await this.Complete(conn, mtu, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await this.Teardown(ex.Message, false).ConfigureAwait(false);
            throw;
        }
        finally
        {
            this.connectLock.Release();
        }
    }


    internal void ReceiveFrame(ReadOnlySpan<byte> frame) => this.protocol.OnFrame(frame);

    /// <summary>
    /// Simulates the link dropping (tests)
    /// </summary>
    internal Task LoseConnection(string reason) => this.Teardown(reason, false);


    public Task Disconnect() => this.Teardown("Disconnected", true);


    public Task<L2CapTransferResult> UploadFile(string localFilePath, string? remoteFileName = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var (p, ack) = this.AssertFiles();
        return p.UploadFile(ack.FileTransferPsm, localFilePath, remoteFileName, ack.FileTransferSecure, ToAction(progress), null, cancellationToken);
    }


    public async Task<L2CapTransferResult> UploadStream(Stream source, long length, string remoteFileName, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var (p, ack) = this.AssertFiles();
        using var channel = await p.OpenL2CapChannelAsync(ack.FileTransferPsm, ack.FileTransferSecure, cancellationToken).ConfigureAwait(false);
        return await channel.UploadFile(source, remoteFileName, length, ToAction(progress), null, cancellationToken).ConfigureAwait(false);
    }


    public Task<L2CapTransferResult> DownloadFile(string remoteFileName, string localFilePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var (p, ack) = this.AssertFiles();
        return p.DownloadFile(ack.FileTransferPsm, remoteFileName, localFilePath, ack.FileTransferSecure, ToAction(progress), null, cancellationToken);
    }


    public void Dispose() => _ = this.Teardown("Disposed", true);


    // ---- used by generated proxies ----

    [EditorBrowsable(EditorBrowsableState.Never)]
    protected BleHubArgumentWriter CreateArguments() => new(this.services.Serializer);


    [EditorBrowsable(EditorBrowsableState.Never)]
    protected async Task<T> InvokeCore<T>(string method, BleHubArgumentWriter arguments, CancellationToken cancellationToken)
    {
        this.AssertConnected();
        var result = await this.protocol.Invoke(method, arguments.ToArray(), cancellationToken).ConfigureAwait(false);
        return this.services.Serializer.Deserialize<T>(result.Span);
    }


    [EditorBrowsable(EditorBrowsableState.Never)]
    protected async Task InvokeVoidCore(string method, BleHubArgumentWriter arguments, CancellationToken cancellationToken)
    {
        this.AssertConnected();
        await this.protocol.Invoke(method, arguments.ToArray(), cancellationToken).ConfigureAwait(false);
    }


    [EditorBrowsable(EditorBrowsableState.Never)]
    protected async IAsyncEnumerable<T> StreamCore<T>(string method, BleHubArgumentWriter arguments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        this.AssertConnected();
        await foreach (var item in this.protocol.Stream(method, arguments.ToArray(), cancellationToken).ConfigureAwait(false))
            yield return this.services.Serializer.Deserialize<T>(item.Span);
    }


    /// <summary>
    /// Raise the matching hub event - implemented by the generated proxy
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    protected abstract void OnPush(string name, BleHubArgumentReader args);


    [EditorBrowsable(EditorBrowsableState.Never)]
    protected void OnUnknownPush(string name)
        => this.logger?.LogWarning("Host pushed '{Event}' which this contract does not declare - are both sides on the same contract?", name);


    // ---- connection lifecycle ----

    Connection Begin(BleHubHostInfo? host)
    {
        var conn = new Connection(host);
        this.connection = conn;
        this.SetStatus(BleHubClientStatus.Connecting, null);
        _ = Task.Run(() => this.PumpPushes(conn));
        return conn;
    }


    async Task Complete(Connection conn, int mtu, BleHubConnectOptions? options, CancellationToken cancellationToken)
    {
        this.protocol.Mtu = mtu;
        var ack = await this.protocol.Handshake(new HandshakeInfo(
            FrameCodec.ProtocolVersion,
            options?.Name,
            options?.AppVersion,
            options?.Properties
        ), cancellationToken).ConfigureAwait(false);

        if (!ack.Accepted)
            throw new SmartBleException($"Host refused the connection: {ack.Reason}");

        conn.Ack = ack;
        this.SetStatus(BleHubClientStatus.Connected, null);
        this.Connected?.Invoke(this, EventArgs.Empty);
        this.logger?.LogInformation("Connected to '{Host}' (MTU {Mtu}, file PSM {Psm})", ack.HostName, mtu, ack.FileTransferPsm);
    }


    /// <summary>
    /// Pushes are raised one at a time, in the order the host sent them
    /// </summary>
    async Task PumpPushes(Connection conn)
    {
        await foreach (var message in conn.Pushes.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                this.OnPush(message.Name ?? "", new BleHubArgumentReader(this.services.Serializer, message.Payload));
            }
            catch (Exception ex)
            {
                this.logger?.LogError(ex, "Handler for host event '{Event}' failed", message.Name);
            }
        }
    }


    async Task Teardown(string reason, bool cancelConnection)
    {
        // connection loss, host disconnect and app disconnect can race - only the first one tears down
        var conn = Interlocked.Exchange(ref this.connection, null);
        if (conn == null)
            return;

        var wasConnected = this.Status == BleHubClientStatus.Connected;
        this.SetStatus(BleHubClientStatus.Disconnecting, reason);
        foreach (var sub in conn.Subscriptions)
            sub.Dispose();

        conn.Pushes.Writer.TryComplete();
        this.protocol.FailAll(new SmartBleDisconnectedException("The connection to the host was closed", reason));

        var p = conn.Host?.Peripheral;
        if (p != null && conn.OwnsBleConnection && this.services.Connections.Release(p.Uuid) && cancelConnection)
        {
            try
            {
                await p.DisconnectAsync(timeout: TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger?.LogDebug(ex, "Disconnect did not complete cleanly");
                p.CancelConnection();
            }
        }

        this.SetStatus(BleHubClientStatus.Disconnected, reason);
        if (wasConnected)
            this.Disconnected?.Invoke(this, reason);
    }


    Task Write(byte[] frame, CancellationToken cancellationToken)
    {
        var write = this.connection?.Write ?? throw new SmartBleDisconnectedException("Not connected to a host");
        return write(frame, cancellationToken);
    }


    void AssertDisconnected()
    {
        if (this.Status != BleHubClientStatus.Disconnected)
            throw new InvalidOperationException($"Client is already {this.Status}");
    }


    void AssertConnected()
    {
        if (this.Status != BleHubClientStatus.Connected)
            throw new SmartBleDisconnectedException($"Client is {this.Status}, not Connected");
    }


    (IPeripheral Peripheral, HandshakeAck Ack) AssertFiles()
    {
        this.AssertConnected();
        var conn = this.connection!;
        if (conn.Ack!.FileTransferPsm == 0)
            throw new SmartBleFileTransferNotSupportedException("The host does not serve file transfers");

        var p = conn.Host?.Peripheral;
        if (p == null || !p.IsL2CapAvailable())
            throw new SmartBleFileTransferNotSupportedException("L2CAP is not available on this device");

        return (p, conn.Ack);
    }


    void SetStatus(BleHubClientStatus status, string? reason)
    {
        if (this.Status == status)
            return;

        this.Status = status;
        this.StatusChanged?.Invoke(this, new BleHubStatusChangedEventArgs(status, reason));
    }


    static Action<TransferProgress>? ToAction(IProgress<TransferProgress>? progress)
        => progress == null ? null : progress.Report;


    sealed class Connection(BleHubHostInfo? host)
    {
        public BleHubHostInfo? Host { get; } = host;
        public HandshakeAck? Ack { get; set; }
        public bool OwnsBleConnection { get; set; }
        public Func<byte[], CancellationToken, Task>? Write { get; set; }
        public List<IDisposable> Subscriptions { get; } = new();
        public Channel<SmartBleMessage> Pushes { get; } = Channel.CreateUnbounded<SmartBleMessage>(new UnboundedChannelOptions { SingleReader = true });
    }
}
