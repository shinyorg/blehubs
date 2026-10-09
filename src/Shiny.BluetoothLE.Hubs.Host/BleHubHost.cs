using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hosting;
using HostedPeripheral = Shiny.BluetoothLE.Hosting.IPeripheral;

namespace Shiny.BluetoothLE.Hubs;

public interface IBleHubHost
{
    /// <summary>
    /// True while at least one hub is running
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// The L2CAP PSM serving file transfers, 0 when not available
    /// </summary>
    ushort FileTransferPsm { get; }

    event EventHandler<BleHubFileTransferredEventArgs>? FileTransferred;
    event EventHandler<BleHubFileProgressEventArgs>? FileTransferProgress;

    /// <summary>
    /// Starts every registered hub - requests BLE access, adds the GATT services, opens the file server (if enabled) and
    /// starts advertising. Start a single hub with IHubContext&lt;THub&gt;.Start().
    /// </summary>
    Task Start(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops every hub - tells their clients to disconnect, then stops advertising and removes the services
    /// </summary>
    Task Stop(string? reason = null);

    /// <summary>
    /// Changes the host's name without stopping anything: sets <see cref="BleHubHostOptions.LocalName"/>, re-advertises
    /// under the new name while hubs are running, and tells every connected client (<see cref="IBleHubConnection.HostRenamed"/>).
    /// </summary>
    Task Rename(string? localName, CancellationToken cancellationToken = default);
}


internal sealed class BleHubHost : IBleHubHost, IDisposable
{
    readonly IBleHostingManager hosting;
    readonly BleHubHostOptions hostOptions;
    readonly IServiceProvider services;
    readonly ILogger<BleHubHost>? logger;
    readonly Dictionary<Type, HubRuntime> runtimes = new();
    readonly Dictionary<HubRuntime, IGattCharacteristic> characteristics = new();
    readonly SemaphoreSlim startLock = new(1, 1);
    L2CapInstance? fileServer;
    Timer? sweepTimer;


    public BleHubHost(
        IBleHostingManager hosting,
        IEnumerable<BleHubRegistration> registrations,
        BleHubHostOptions hostOptions,
        BleHubProtocolOptions options,
        IBleHubSerializer serializer,
        IServiceProvider services,
        ILoggerFactory? loggerFactory = null
    )
    {
        this.hosting = hosting;
        this.hostOptions = hostOptions;
        this.services = services;
        this.logger = loggerFactory?.CreateLogger<BleHubHost>();

        var list = registrations.ToList();
        var duplicate = list.GroupBy(x => x.CharacteristicUuid, StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException($"Hubs {String.Join(", ", duplicate.Select(x => x.HubType.Name))} share characteristic {duplicate.Key} - each hub needs its own");

        foreach (var reg in list)
        {
            var runtime = new HubRuntime(reg, BleHubDispatchers.Get(reg.HubType), services, options, serializer, loggerFactory?.CreateLogger(reg.HubType.FullName!))
            {
                HostName = hostOptions.LocalName
            };
            runtime.Notify = (peer, frame, ct) => this.Notify(runtime, peer, frame, ct);
            this.runtimes.Add(reg.HubType, runtime);
        }
    }


    readonly HashSet<string> addedServices = new(StringComparer.OrdinalIgnoreCase);
    string[] advertisedServices = [];
    bool infrastructureRunning;


    /// <summary>
    /// True while at least one hub is running
    /// </summary>
    public bool IsRunning => this.runtimes.Values.Any(x => x.IsRunning);
    public ushort FileTransferPsm { get; private set; }
    public event EventHandler<BleHubFileTransferredEventArgs>? FileTransferred;
    public event EventHandler<BleHubFileProgressEventArgs>? FileTransferProgress;


    internal HubRuntime GetRuntime(Type hubType) => this.runtimes.TryGetValue(hubType, out var runtime)
        ? runtime
        : throw new InvalidOperationException($"{hubType.Name} is not registered - call AddBleHub<{hubType.Name}>()");


    public Task Start(CancellationToken cancellationToken = default)
    {
        if (this.runtimes.Count == 0)
            throw new InvalidOperationException("No hubs are registered - call AddBleHub<THub>()");

        return this.StartHubs(this.runtimes.Values.ToList(), cancellationToken);
    }


    public Task Stop(string? reason = null) => this.StopHubs(this.runtimes.Values.ToList(), new HubDisconnect(HubDisconnectReason.ServerShutdown, reason));


    public async Task Rename(string? localName, CancellationToken cancellationToken = default)
    {
        await this.startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (String.Equals(this.hostOptions.LocalName, localName, StringComparison.Ordinal))
                return;

            this.hostOptions.LocalName = localName;
            if (this.IsRunning)
                await this.UpdateAdvertising(force: true).ConfigureAwait(false);

            // every hub, running or not - a stopped hub can still have clients on another transport
            await Task.WhenAll(this.runtimes.Values.Select(x => x.SetHostName(localName, cancellationToken))).ConfigureAwait(false);
            this.logger?.LogInformation("Host renamed to '{Name}'", localName);
        }
        finally
        {
            this.startLock.Release();
        }
    }


    public Task StartHub(HubRuntime runtime, CancellationToken cancellationToken) => this.StartHubs([runtime], cancellationToken);
    public Task StopHub(HubRuntime runtime, string? reason) => this.StopHubs([runtime], new HubDisconnect(HubDisconnectReason.ServerShutdown, reason ?? "Hub stopped"));


    async Task StartHubs(IReadOnlyList<HubRuntime> hubs, CancellationToken cancellationToken)
    {
        await this.startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var starting = hubs.Where(x => !x.IsRunning).ToList();
            if (starting.Count == 0)
                return;

            var startedInfrastructure = false;
            try
            {
                if (!this.infrastructureRunning)
                {
                    var access = await this.hosting.RequestAccess().ConfigureAwait(false);
                    if (access != AccessState.Available)
                        throw new BleHubException($"Bluetooth hosting is not available ({access})");

                    await this.StartFileServer().ConfigureAwait(false);
                    this.sweepTimer = new Timer(_ => this.Sweep(), null, this.hostOptions.ClientSweepInterval, this.hostOptions.ClientSweepInterval);
                    this.infrastructureRunning = true;
                    startedInfrastructure = true;
                }

                // a GATT service holds every hub that shares its UUID - hubs that are stopped stay in it but refuse handshakes,
                // so starting/stopping one hub never yanks the service out from under another hub's clients
                foreach (var serviceUuid in starting.Select(x => x.Registration.ServiceUuid).Distinct(StringComparer.OrdinalIgnoreCase))
                    await this.EnsureService(serviceUuid).ConfigureAwait(false);

                foreach (var runtime in starting)
                {
                    runtime.HostName = this.hostOptions.LocalName;
                    runtime.IsRunning = true;
                }

                await this.UpdateAdvertising().ConfigureAwait(false);
                this.logger?.LogInformation("Started hub(s) {Hubs}", String.Join(", ", starting.Select(x => x.Registration.HubType.Name)));
            }
            catch
            {
                foreach (var runtime in starting)
                    runtime.IsRunning = false;

                this.RemoveUnusedServices();
                if (startedInfrastructure || !this.IsRunning)
                    await this.StopInfrastructure().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            this.startLock.Release();
        }
    }


    async Task StopHubs(IReadOnlyList<HubRuntime> hubs, HubDisconnect disconnect)
    {
        await this.startLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var stopping = hubs.Where(x => x.IsRunning).ToList();
            if (stopping.Count == 0)
                return;

            // refuse new handshakes first, then ask everyone connected to leave
            foreach (var runtime in stopping)
                runtime.IsRunning = false;

            await Task.WhenAll(stopping.Select(x => x.DisconnectAll(disconnect))).ConfigureAwait(false);

            this.RemoveUnusedServices();
            if (this.IsRunning)
                await this.UpdateAdvertising().ConfigureAwait(false);
            else
                await this.StopInfrastructure().ConfigureAwait(false);

            this.logger?.LogInformation("Stopped hub(s) {Hubs}: {Reason}", String.Join(", ", stopping.Select(x => x.Registration.HubType.Name)), disconnect.Description);
        }
        finally
        {
            this.startLock.Release();
        }
    }


    public void Dispose()
    {
        foreach (var runtime in this.runtimes.Values)
            runtime.IsRunning = false;

        this.RemoveUnusedServices();
        _ = this.StopInfrastructure();
        this.startLock.Dispose();
    }


    async Task EnsureService(string serviceUuid)
    {
        if (this.addedServices.Contains(serviceUuid))
            return;

        // a previous run that crashed may have left the service behind
        this.hosting.RemoveService(serviceUuid);
        var hubs = this.runtimes.Values.Where(x => String.Equals(x.Registration.ServiceUuid, serviceUuid, StringComparison.OrdinalIgnoreCase)).ToList();

        await this.hosting.AddService(serviceUuid, true, sb =>
        {
            foreach (var runtime in hubs)
            {
                var rt = runtime;
                this.characteristics[rt] = sb.AddCharacteristic(rt.Registration.CharacteristicUuid, cb => cb
                    .SetWrite(req => this.OnWrite(rt, req), WriteOptions.Write)
                    .SetNotification(sub => this.OnSubscription(rt, sub), NotificationOptions.Notify)
                );
            }
        }).ConfigureAwait(false);
        this.addedServices.Add(serviceUuid);
    }


    /// <summary>
    /// Removes GATT services that no longer have a running hub
    /// </summary>
    void RemoveUnusedServices()
    {
        foreach (var serviceUuid in this.addedServices.ToList())
        {
            var hubs = this.runtimes.Values.Where(x => String.Equals(x.Registration.ServiceUuid, serviceUuid, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hubs.Any(x => x.IsRunning))
                continue;

            this.hosting.RemoveService(serviceUuid);
            this.addedServices.Remove(serviceUuid);
            foreach (var hub in hubs)
                this.characteristics.Remove(hub);
        }
    }


    /// <summary>
    /// Advertises exactly the services that have a running hub. <paramref name="force"/> restarts the advertisement even
    /// when those services haven't changed (the name has).
    /// </summary>
    async Task UpdateAdvertising(bool force = false)
    {
        var wanted = this.runtimes.Values
            .Where(x => x.IsRunning)
            .Select(x => x.Registration.ServiceUuid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!force && this.hosting.IsAdvertising && wanted.SequenceEqual(this.advertisedServices, StringComparer.OrdinalIgnoreCase))
            return;

        if (this.hosting.IsAdvertising)
            this.hosting.StopAdvertising();

        this.advertisedServices = wanted;
        if (wanted.Length == 0)
            return;

        if (wanted.Length > 1)
            this.logger?.LogWarning("Advertising {Count} 128-bit service UUIDs will likely overflow the advertisement packet - consider giving your hubs one service UUID", wanted.Length);

        await this.hosting.StartAdvertising(new AdvertisementOptions(this.hostOptions.LocalName, wanted)).ConfigureAwait(false);
    }


    Task StopInfrastructure()
    {
        this.infrastructureRunning = false;
        this.sweepTimer?.Dispose();
        this.sweepTimer = null;

        this.fileServer?.Dispose();
        this.fileServer = null;
        this.SetFileTransfer(0, false);

        if (this.hosting.IsAdvertising)
            this.hosting.StopAdvertising();

        this.advertisedServices = [];
        return Task.CompletedTask;
    }


    Task OnWrite(HubRuntime runtime, WriteRequest request)
    {
        if (request.Offset != 0)
        {
            Respond(request, GattState.InvalidOffset);
            return Task.CompletedTask;
        }

        var ok = runtime.OnFrame(request.Peripheral.Uuid, request.Peripheral.Mtu, request.Peripheral, request.Data);
        Respond(request, ok ? GattState.Success : GattState.Failure);
        return Task.CompletedTask;
    }


    Task OnSubscription(HubRuntime runtime, CharacteristicSubscription sub)
    {
        if (!sub.IsSubscribing)
            // a client leaving on purpose says so first (a Disconnect frame), so an unsubscribe on its own is a dropped link -
            // both iOS and Android report a central that vanished as an unsubscribe
            runtime.OnPeerGone(sub.Peripheral.Uuid, new HubDisconnect(HubDisconnectReason.ClientTimeout, "Unsubscribed"));

        return Task.CompletedTask;
    }


    Task Notify(HubRuntime runtime, HostPeer peer, byte[] frame, CancellationToken cancellationToken)
    {
        if (!this.characteristics.TryGetValue(runtime, out var ch))
            throw new BleHubException("Hub host is not running");

        return ch.Notify(frame, cancellationToken, (HostedPeripheral)peer.Native!);
    }


    void Sweep()
    {
        // not every platform reports an unsubscribe when a central simply drops - reap anyone no longer subscribed
        foreach (var (runtime, ch) in this.characteristics.ToList())
        {
            try
            {
                var subscribed = ch.SubscribedCentrals.Select(x => x.Uuid).ToHashSet(StringComparer.OrdinalIgnoreCase);
                // clients on another transport aren't GATT subscribers - that transport reports when they leave
                foreach (var peer in runtime.Peers.Where(x => x.Channel == null).ToList())
                {
                    if (!subscribed.Contains(peer.Id))
                        runtime.OnPeerGone(peer.Id, new HubDisconnect(HubDisconnectReason.ClientTimeout));
                }
            }
            catch (Exception ex)
            {
                this.logger?.LogWarning(ex, "Client sweep failed");
            }
        }
    }


    BleHubConnectedClient? FindClient(string peerId) => this.runtimes.Values
        .Select(x => x.FindClient(peerId))
        .FirstOrDefault(x => x != null);


    void SetFileTransfer(ushort psm, bool secure)
    {
        this.FileTransferPsm = psm;
        foreach (var runtime in this.runtimes.Values)
        {
            runtime.FileTransferPsm = psm;
            runtime.FileTransferSecure = secure;
        }
    }


    async Task StartFileServer()
    {
        var ft = this.hostOptions.FileTransfers;
        if (ft == null)
            return;

        try
        {
            var handler = this.services.GetService<IBleHubFileHandler>();
            if (handler != null)
            {
                this.fileServer = await this.hosting.HandleL2CapRequests(
                    ft.Secure,
                    (req, ct) => handler.Handle(new BleHubFileRequest(req, this.FindClient(req.PeerIdentifier)), ct),
                    ft.Transfer,
                    (req, ex) => this.logger?.LogWarning(ex, "File transfer {File} failed", req?.FileName)
                ).ConfigureAwait(false);
            }
            else
            {
                Directory.CreateDirectory(ft.RootDirectory);
                var serverOptions = new L2CapFileServerOptions(ft.RootDirectory)
                {
                    Secure = ft.Secure,
                    AllowUploads = ft.AllowUploads,
                    AllowDownloads = ft.AllowDownloads,
                    OverwriteExistingUploads = ft.OverwriteExistingUploads,
                    MaxUploadSize = ft.MaxUploadSize,
                    Authorize = ft.Authorize == null ? null : req => ft.Authorize(new BleHubFileRequest(req, this.FindClient(req.PeerIdentifier))),
                    OnProgress = e => this.FileTransferProgress?.Invoke(this, new BleHubFileProgressEventArgs(
                        this.FindClient(e.PeerIdentifier), e.PeerIdentifier, e.Type, e.FileName, e.Progress
                    )),
                    OnCompleted = r => this.FileTransferred?.Invoke(this, new BleHubFileTransferredEventArgs(
                        this.FindClient(r.PeerIdentifier), r.PeerIdentifier, r.Result.Type, r.Result.FileName, r.LocalFilePath, r.Result.BytesTransferred, r.Result.Elapsed
                    )),
                    OnError = (req, ex) => this.logger?.LogWarning(ex, "File transfer {File} failed", req?.FileName)
                };
                if (ft.Transfer != null)
                    serverOptions.Transfer = ft.Transfer;

                this.fileServer = await this.hosting.OpenL2CapFileServer(serverOptions).ConfigureAwait(false);
            }
            this.SetFileTransfer(this.fileServer.Value.Psm, ft.Secure);
            this.logger?.LogInformation("File transfers served on PSM {Psm}", this.FileTransferPsm);
        }
        catch (Exception ex)
        {
            // L2CAP needs iOS 11+/Android 10+ - the hubs still work without it
            this.logger?.LogWarning(ex, "File transfers are unavailable on this device");
            this.SetFileTransfer(0, false);
        }
    }


    static void Respond(WriteRequest request, GattState state)
    {
        if (request.IsReplyNeeded)
            request.Respond(state);
    }
}
