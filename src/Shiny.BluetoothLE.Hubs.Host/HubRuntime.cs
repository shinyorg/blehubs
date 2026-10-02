using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hubs.Protocol;

namespace Shiny.BluetoothLE.Hubs;

/// <summary>
/// Transport independent runtime for one hub - reassembles client frames, runs hub methods, frames replies and pushes,
/// and tracks groups. The BLE host feeds it frames; tests feed it from an in-memory radio.
/// </summary>
internal sealed class HubRuntime : IGroupManager
{
    readonly ConcurrentDictionary<string, HostPeer> peers = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> groups = new(StringComparer.Ordinal);
    readonly BleHubRegistration registration;
    readonly IBleHubDispatcher dispatcher;
    readonly IServiceProvider services;
    readonly BleHubProtocolOptions options;
    readonly ILogger? logger;
    CancellationTokenSource lifetime = new();


    public HubRuntime(
        BleHubRegistration registration,
        IBleHubDispatcher dispatcher,
        IServiceProvider services,
        BleHubProtocolOptions options,
        IBleHubSerializer serializer,
        ILogger? logger
    )
    {
        this.registration = registration;
        this.dispatcher = dispatcher;
        this.services = services;
        this.options = options;
        this.Serializer = serializer;
        this.logger = logger;
    }


    public BleHubRegistration Registration => this.registration;
    public Type ContractType => this.dispatcher.ContractType;
    public IBleHubSerializer Serializer { get; }
    public IGroupManager Groups => this;

    /// <summary>
    /// Sends one frame to a peer - set by whoever owns the transport
    /// </summary>
    public Func<HostPeer, byte[], CancellationToken, Task>? Notify { get; set; }

    /// <summary>
    /// A stopped hub refuses handshakes - its characteristic may still exist when it shares a service with a running hub
    /// </summary>
    public bool IsRunning { get; set; }

    public ushort FileTransferPsm { get; set; }
    public bool FileTransferSecure { get; set; }
    public string? HostName { get; set; }

    public IEnumerable<HostPeer> Peers => this.peers.Values;
    public IEnumerable<HostPeer> ReadyPeers => this.peers.Values.Where(x => x.Client != null);
    public IReadOnlyList<BleHubConnectedClient> Clients => this.ReadyPeers.Select(x => x.Client!).ToList();

    public event EventHandler<BleHubConnectedClient>? ClientConnected;
    public event EventHandler<BleHubClientDisconnectedEventArgs>? ClientDisconnected;


    public BleHubConnectedClient? FindClient(string connectionId)
        => this.peers.TryGetValue(connectionId, out var peer) ? peer.Client : null;


    /// <summary>
    /// Feeds a frame written by a peer. Returns false when the frame is malformed (the GATT write should be failed).
    /// Complete messages are dispatched in the background.
    /// </summary>
    public bool OnFrame(string peerId, int mtu, object? native, ReadOnlySpan<byte> frame)
    {
        var peer = this.peers.GetOrAdd(peerId, id => new HostPeer(id, native, new MessageReassembler(
            this.options.MaxPayloadSize,
            this.options.ReassemblyTimeout,
            this.options.MaxPartialMessages
        )));
        peer.Mtu = mtu;
        if (native != null)
            peer.Native = native;

        BleHubMessage? message;
        try
        {
            lock (peer.Reassembler)
                message = peer.Reassembler.Add(frame);
        }
        catch (BleHubProtocolException ex)
        {
            this.logger?.LogWarning(ex, "Bad frame from {Peer}", peerId);
            if (ex.MessageId is { } id && peer.Client != null)
                _ = this.SendError(peer, id, nameof(BleHubProtocolException), ex.Message);

            return false;
        }

        if (message != null)
            _ = Task.Run(() => this.Dispatch(peer, message));

        return true;
    }


    /// <summary>
    /// The peer unsubscribed, disconnected or was removed
    /// </summary>
    public void OnPeerGone(string peerId, string? reason)
    {
        if (!this.peers.TryRemove(peerId, out var peer))
            return;

        peer.Abort();
        if (peer.Client == null)
            return;

        var client = peer.Client;
        this.logger?.LogInformation("Client {Client} left {Hub}: {Reason}", client, this.registration.HubType.Name, reason);
        _ = Task.Run(async () =>
        {
            try
            {
                await this.RunLifecycle(peer, client, hub => hub.OnDisconnectedAsync(reason)).ConfigureAwait(false);
            }
            finally
            {
                foreach (var group in this.groups.Values)
                    group.TryRemove(client.Id, out _);

                this.ClientDisconnected?.Invoke(this, new BleHubClientDisconnectedEventArgs(client, reason));
            }
        });
    }


    public async Task Push(string eventName, byte[] payload, IEnumerable<HostPeer> recipients, CancellationToken cancellationToken)
    {
        var targets = recipients.Where(x => x.Client != null).Distinct().ToList();
        await Task.WhenAll(targets.Select(async peer =>
        {
            try
            {
                await this.Send(peer, FrameKind.Push, peer.NextHostMessageId(), eventName, payload, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger?.LogWarning(ex, "Failed to push {Event} to {Client}", eventName, peer.Client);
            }
        })).ConfigureAwait(false);
    }


    public async Task Disconnect(string connectionId, string? reason)
    {
        if (!this.peers.TryGetValue(connectionId, out var peer))
            return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var payload = ProtocolSerializer.Serialize(new DisconnectInfo(reason));
            await this.Send(peer, FrameKind.Disconnect, peer.NextHostMessageId(), null, payload, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger?.LogInformation(ex, "Could not deliver disconnect to {Peer}", connectionId);
        }
        this.OnPeerGone(connectionId, reason ?? "Disconnected by host");
    }


    public async Task DisconnectAll(string? reason)
    {
        await Task.WhenAll(this.peers.Keys.ToList().Select(x => this.Disconnect(x, reason))).ConfigureAwait(false);
        this.lifetime.Cancel();
        this.lifetime = new CancellationTokenSource();
    }


    public IEnumerable<HostPeer> PeersInGroups(IEnumerable<string> groupNames)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in groupNames)
        {
            if (this.groups.TryGetValue(name, out var members))
                ids.UnionWith(members.Keys);
        }
        return this.ReadyPeers.Where(x => ids.Contains(x.Id));
    }


    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        this.groups.GetOrAdd(groupName, _ => new(StringComparer.OrdinalIgnoreCase))[connectionId] = 0;
        return Task.CompletedTask;
    }


    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        if (this.groups.TryGetValue(groupName, out var members))
            members.TryRemove(connectionId, out _);

        return Task.CompletedTask;
    }


    public IReadOnlyList<string> GetMembers(string groupName)
        => this.groups.TryGetValue(groupName, out var members) ? members.Keys.ToList() : [];


    async Task Dispatch(HostPeer peer, BleHubMessage message)
    {
        try
        {
            switch (message.Kind)
            {
                case FrameKind.Handshake:
                    await this.OnHandshake(peer, message).ConfigureAwait(false);
                    break;

                case FrameKind.Cancel:
                    peer.Cancel(message.MessageId);
                    break;

                case FrameKind.Invoke:
                case FrameKind.StreamInvoke:
                    await this.OnInvoke(peer, message).ConfigureAwait(false);
                    break;

                default:
                    await this.SendError(peer, message.MessageId, nameof(BleHubProtocolException), $"Unexpected message kind {message.Kind}").ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            this.logger?.LogError(ex, "Unhandled error dispatching {Kind} {Id} from {Peer}", message.Kind, message.MessageId, peer.Id);
        }
    }


    async Task OnHandshake(HostPeer peer, BleHubMessage message)
    {
        var info = ProtocolSerializer.ReadHandshake(message.Payload);
        var hubOptions = this.registration.Options;
        string? rejection;

        if (!this.IsRunning)
            rejection = "Hub is not running";
        else if (info.ProtocolVersion != FrameCodec.ProtocolVersion)
            rejection = $"Unsupported protocol version {info.ProtocolVersion}";
        else if (peer.Client == null && hubOptions.MaxClients is { } max && this.Clients.Count >= max)
            rejection = "Host is full";
        else
            rejection = hubOptions.ValidateClient?.Invoke(info);

        if (rejection == null && peer.Client == null)
        {
            // registered and OnConnectedAsync completed before the ack goes out, so the client is never told it is in
            // before the hub knows about it (and has had a chance to put it in groups)
            var client = new BleHubConnectedClient(peer.Id, info.Name, info.AppVersion, info.Properties ?? new(), peer.Mtu);
            peer.Client = client;
            try
            {
                await this.RunLifecycle(peer, client, hub => hub.OnConnectedAsync()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                peer.Client = null;
                rejection = ex.Message;
            }

            if (rejection == null)
            {
                this.logger?.LogInformation("Client {Client} connected to {Hub}", client, this.registration.HubType.Name);
                this.ClientConnected?.Invoke(this, client);
            }
        }

        var ack = new HandshakeAck(rejection == null, rejection, this.HostName, this.FileTransferPsm, this.FileTransferSecure);
        await this.Send(peer, FrameKind.HandshakeAck, message.MessageId, null, ProtocolSerializer.Serialize(ack), this.lifetime.Token).ConfigureAwait(false);

        if (rejection != null)
        {
            this.logger?.LogInformation("Rejected client {Peer}: {Reason}", peer.Id, rejection);
            this.peers.TryRemove(peer.Id, out _);
        }
    }


    async Task OnInvoke(HostPeer peer, BleHubMessage message)
    {
        var client = peer.Client;
        if (client == null)
        {
            await this.SendError(peer, message.MessageId, nameof(BleHubProtocolException), "Handshake required").ConfigureAwait(false);
            return;
        }

        var method = message.Name ?? "";
        var kind = this.dispatcher.GetMethodKind(method);
        var expected = message.Kind == FrameKind.StreamInvoke ? BleHubMethodKind.Stream : BleHubMethodKind.Invoke;
        if (kind != expected)
        {
            var error = kind == BleHubMethodKind.NotFound
                ? $"{this.registration.HubType.Name} has no method '{method}'"
                : $"'{method}' is {(kind == BleHubMethodKind.Stream ? "a stream" : "not a stream")}";
            await this.SendError(peer, message.MessageId, "HubMethodNotFound", error).ConfigureAwait(false);
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(this.lifetime.Token, peer.Aborted);
        peer.Track(message.MessageId, cts);
        string? abortReason = null;
        var abortRequested = false;
        try
        {
            await using var scope = this.services.CreateAsyncScope();
            var hub = this.CreateHub(scope.ServiceProvider, peer, client, reason =>
            {
                // deferred so the caller still gets this method's reply
                abortRequested = true;
                abortReason = reason;
            });
            var args = new BleHubArgumentReader(this.Serializer, message.Payload);

            if (kind == BleHubMethodKind.Invoke)
            {
                var result = await this.dispatcher.Invoke(hub, method, args, this.Serializer, cts.Token).ConfigureAwait(false);
                await this.Send(peer, FrameKind.Completion, message.MessageId, null, result ?? ReadOnlyMemory<byte>.Empty, cts.Token).ConfigureAwait(false);
            }
            else
            {
                await foreach (var item in this.dispatcher.Stream(hub, method, args, this.Serializer, cts.Token).WithCancellation(cts.Token).ConfigureAwait(false))
                    await this.Send(peer, FrameKind.StreamItem, message.MessageId, null, item, cts.Token).ConfigureAwait(false);

                await this.Send(peer, FrameKind.StreamEnd, message.MessageId, null, ReadOnlyMemory<byte>.Empty, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // the client cancelled, left, or the host is stopping - nobody is waiting for a reply
        }
        catch (Exception ex)
        {
            this.logger?.LogWarning(ex, "{Hub}.{Method} failed", this.registration.HubType.Name, method);
            await this.SendError(peer, message.MessageId, ex.GetType().Name, ex.Message).ConfigureAwait(false);
        }
        finally
        {
            peer.Untrack(message.MessageId);
        }

        if (abortRequested)
            await this.Disconnect(peer.Id, abortReason).ConfigureAwait(false);
    }


    object CreateHub(IServiceProvider scope, HostPeer peer, BleHubConnectedClient client, Action<string?>? abort = null)
    {
        var hub = scope.GetRequiredService(this.registration.HubType);
        abort ??= reason => _ = this.Disconnect(peer.Id, reason);
        ((IBleHubInternal)hub).Initialize(new BleHubCallerContext(client, abort, peer.Aborted), this);
        return hub;
    }


    async Task RunLifecycle(HostPeer peer, BleHubConnectedClient client, Func<IBleHubInternal, Task> action)
    {
        await using var scope = this.services.CreateAsyncScope();
        var hub = (IBleHubInternal)this.CreateHub(scope.ServiceProvider, peer, client);
        try
        {
            await action(hub).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger?.LogError(ex, "{Hub} lifecycle hook failed for {Client}", this.registration.HubType.Name, client);
            throw;
        }
    }


    async Task SendError(HostPeer peer, ushort messageId, string type, string error)
    {
        try
        {
            var payload = ProtocolSerializer.Serialize(new RemoteError(type, error));
            await this.Send(peer, FrameKind.Error, messageId, null, payload, this.lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger?.LogWarning(ex, "Failed to send error to {Peer}", peer.Id);
        }
    }


    async Task Send(HostPeer peer, FrameKind kind, ushort messageId, string? name, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var notify = this.Notify ?? throw new BleHubException("Hub host is not running");
        var frames = FrameCodec.Encode(kind, messageId, name, payload.Span, FrameCodec.GetFrameSize(peer.Mtu));

        // whole messages go out under the lock so frames for one peer never interleave
        await peer.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var frame in frames)
                await notify(peer, frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            peer.SendLock.Release();
        }
    }
}


internal sealed class HostPeer(string id, object? native, MessageReassembler reassembler)
{
    readonly ConcurrentDictionary<ushort, CancellationTokenSource> inFlight = new();
    readonly CancellationTokenSource aborted = new();
    int nextHostId;

    public string Id { get; } = id;
    public object? Native { get; set; } = native;
    public int Mtu { get; set; } = 23;
    public MessageReassembler Reassembler { get; } = reassembler;
    public SemaphoreSlim SendLock { get; } = new(1, 1);
    public BleHubConnectedClient? Client { get; set; }
    public CancellationToken Aborted => this.aborted.Token;


    /// <summary>
    /// Host allocated ids have the high bit set so they never collide with client ids
    /// </summary>
    public ushort NextHostMessageId() => (ushort)(0x8000 | (Interlocked.Increment(ref this.nextHostId) & 0x7FFF));

    public void Track(ushort id, CancellationTokenSource cts) => this.inFlight[id] = cts;
    public void Untrack(ushort id) => this.inFlight.TryRemove(id, out _);

    public void Cancel(ushort id)
    {
        if (this.inFlight.TryRemove(id, out var cts))
            TryCancel(cts);
    }

    public void Abort() => TryCancel(this.aborted);

    static void TryCancel(CancellationTokenSource cts)
    {
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}


public sealed record BleHubClientDisconnectedEventArgs(BleHubConnectedClient Client, string? Reason);
