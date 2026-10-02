# Shiny.SmartBle — Plan

**SignalR-style hubs over Bluetooth LE**, built on **Shiny.BluetoothLE** (client/central) and **Shiny.BluetoothLE.Hosting** (host/peripheral).

- One device hosts one or more hubs.
- Clients discover the host, connect, and call hub methods through a **source-generated, strongly typed proxy**.
- The host pushes events to all clients, some clients, or groups of clients.
- Files move over a separate **L2CAP** channel, a faster direct link between the devices.

Sample: **tic-tac-toe** in .NET MAUI (iOS + Android) using **Shiny.Maui.Shell**.

> History: v0 dispatched Shiny.Mediator contracts over BLE. On 2026-10-02 it was replaced by the hub model below. Mediator is no longer a dependency. The wire framing, transport, handshake and file transfer were kept.

---

## 1. Developer experience

### Contract (shared by host and client)
```csharp
[BleHubClient]
public interface IGameHub
{
    // client -> host. Each must return Task, Task<T> or IAsyncEnumerable<T>.
    // A trailing CancellationToken parameter is passed through and is never serialized.
    Task<JoinResult> Join(string playerName, string? avatarFile);
    Task<MoveResult> MakeMove(int cell);
    Task Rematch();
    IAsyncEnumerable<int> Countdown(int from, CancellationToken cancellationToken);

    // host -> client, fire-and-forget. Action or Action<T1..T4>.
    event Action<GameState> StateChanged;
    event Action<string, string> Emote;
}
```

### Host
```csharp
services.AddBleSmartHub<GameHub>("<service uuid>", "<characteristic uuid>");
services.ConfigureBleHubHost(o => { o.LocalName = "TTT"; o.EnableFileTransfers(dir); });

public class GameHub(GameEngine engine) : BleHub<IGameHub>
{
    public override Task OnConnectedAsync() => Groups.AddToGroupAsync(Context.ConnectionId, "lobby");
    public override Task OnDisconnectedAsync(string? reason) => ...;

    public async Task<MoveResult> MakeMove(int cell)
    {
        // Context.Client (name, properties, items), Context.Abort("reason")
        await Clients.All.StateChanged(engine.Snapshot());      // generated typed push
        await Clients.Group("spectators").Emote("host", "👀");
        return new MoveResult(true, null);
    }
    ...
}

// outside a hub
public class Something(IHubContext<GameHub> hub)
{
    Task Tick() => hub.Clients.All.StateChanged(state);       // generated extension property
}

await host.Start();   // IBleHubHost - adds every hub's GATT service, L2CAP and advertising
```

### Client
```csharp
services.AddBleHubClient<IGameHub>("<service uuid>", "<characteristic uuid>");

public class GameViewModel(IBleHubClient<IGameHub> client)  // or the generated GameHubClient, or IGameHub
{
    client.Hub.StateChanged += state => ...;
    client.Connected += ...; client.Disconnected += (_, reason) => ...;

    client.Discover().Subscribe(host => ...);
    await client.Connect(host, new BleHubConnectOptions("Allan"));
    var result = await client.Hub.MakeMove(4);
    await foreach (var n in client.Hub.Countdown(3, ct)) { }
    await client.UploadFile(path, "avatar.jpg");
}
```

## 2. GATT layout

- **One characteristic per hub**, with **Write** (client → host) and **Notify** (host → client) on the same characteristic.
- Hubs may share a service UUID, in which case they become separate characteristics in one GATT service, or use their own. Hubs are routed by characteristic UUID, so frames never need to carry a hub name.
- The host advertises every hub's service UUID. A client scans for the service UUID of the hub it wants.
- Several hub clients connected to the same peripheral share one BLE connection, which is reference counted. Disconnecting one hub doesn't drop the others.

### Why notifications instead of reads for responses
- With reads, the client can't tell when a response is ready, so it would have to poll.
- A read value is ambiguous when several calls are in flight.
- Reads are still limited by the MTU and the 512-byte attribute cap.

Correlated, chunked notifications avoid all three problems.

## 3. Wire protocol (v1)

Every write or notification is one **frame**, at most `MTU - 3` bytes.

```
offset size  field
0      1     protocol version (1)
1      1     kind
2      2     message id (uint16 LE)
4      2     sequence (uint16 LE)
6      1     flags: bit0 FIRST, bit1 LAST
7      4     total body length (uint32 LE), FIRST frame only
...          body chunk
```
The body is `[name length:1][name utf8][payload]`. Frames are reassembled per (peer, hub, message id).

| Value | Kind | Dir | Name | Payload |
|---|---|---|---|---|
| 0x01 | Handshake | C→H | – | `HandshakeInfo` (protocol version, client name, app version, properties) |
| 0x02 | HandshakeAck | H→C | – | `HandshakeAck` (accepted, reason, host name, file PSM, file secure) |
| 0x10 | Invoke | C→H | method | arguments |
| 0x12 | StreamInvoke | C→H | method | arguments |
| 0x13 | Cancel | C→H | – | (cancels the invocation or stream with that id) |
| 0x21 | Completion | H→C | – | serialized result, empty for `Task` |
| 0x22 | StreamItem | H→C | – | serialized item |
| 0x23 | StreamEnd | H→C | – | – |
| 0x24 | Error | H→C | – | `RemoteError` (type, message) |
| 0x30 | Push | H→C | event | arguments |
| 0x40 | Disconnect | H→C | – | `DisconnectInfo` (reason) |

**Arguments**: `[count:1]` followed by `count × ([length:4][serialized value])`. Each value is serialized with its static type through `ISmartBleSerializer`, which keeps it AOT-safe.

**Rules**
- Client message ids are 1..0x7FFF. Host-allocated ids (pushes, disconnect) have the high bit set.
- Reassembly limits:
  - `MaxPayloadSize` (default 256 KB)
  - reassembly timeout (default 30 s)
  - at most 16 partial messages per peer

  A violation fails the GATT write and sends an `Error` frame.
- The host sends each message to a peer under a lock, so frames for one peer never interleave. The client also serializes its writes per message.
- The host refuses any hub call made before the handshake, and refuses unknown protocol versions.

## 4. Source generator (`Shiny.SmartBle.SourceGenerators`, shipped in the core package)

For each `[BleHubClient]` interface, the generator emits:
- **Client proxy**, emitted when `Shiny.SmartBle.Client` is referenced. `GameHubClient : BleHubClient, IGameHub, IBleHubClient<IGameHub>` implements each method through `Invoke` / `Stream`, implements the events, and dispatches pushes to them. A `[ModuleInitializer]` registers a factory so `AddBleHubClient<IGameHub>()` can create the proxy without reflection.
- **Typed push senders**, emitted when `Shiny.SmartBle.Host` is referenced and a hub uses the contract. These are extension methods on `BleHubPush<IGameHub>`, such as `StateChanged(GameState)`.

For each `class X : BleHub<TContract>`, the generator emits:
- **Dispatcher**: a switch on the method name, typed argument reads, a call into the hub, and result serialization. It is registered through a `[ModuleInitializer]`.
- **`IHubContext<X>.Clients`**: a C# 14 extension property typed to `IHubClients<BleHubPush<TContract>>`.

Hubs don't need to be `partial`. The generator checks the hub's methods against the contract and reports any mismatch.

Contracts declared in a referenced assembly are supported. Proxies and senders generated for them are `internal` to avoid clashes.

**Diagnostics**

| Id | Problem |
|---|---|
| SBH001 | The hub is missing a contract method, or its signature doesn't match |
| SBH002 | Unsupported return type (must be `Task`, `Task<T>` or `IAsyncEnumerable<T>`) |
| SBH003 | Overloaded method names |
| SBH004 | Unsupported event delegate (must be `Action` / `Action<…>`, at most 4 arguments) |
| SBH005 | `BleHub<T>` where `T` isn't a `[BleHubClient]` interface |
| SBH006 | `ref` / `out` / `in` parameters, or more than 255 parameters |

## 5. Host (`Shiny.SmartBle.Host`)

- **`AddBleSmartHub<THub>(serviceUuid, characteristicUuid)`**:
  - registers the hub as transient, created in a new DI scope for each invocation (like SignalR)
  - registers `IHubContext<THub>`
  - registers the shared `IBleHubHost`
- **`IBleHubHost`**:
  - `Start()` / `Stop(reason)` start or stop **every** hub.
  - `IsRunning` is true while any hub runs.
- **Per-hub start/stop**: `IHubContext<THub>.Start()` / `Stop(reason)` / `IsRunning`.
  - A stopped hub tells its clients to disconnect and refuses new handshakes ("Hub is not running").
  - A GATT service holds every hub that shares its UUID. It is added when the first of those hubs starts and removed only when the last one stops, so stopping one hub never drops another hub's clients.
  - Advertising always lists exactly the services that have a running hub.
  - BLE access, the L2CAP file server and the cleanup sweep start with the first running hub and stop with the last.
  - Also: `IsRunning`, `FileTransferPsm`, `FileTransferred` / `FileTransferProgress` events.
- **`BleHub<TContract>`**:
  - `Context`: `ConnectionId`, `Client` (name, app version, properties, `Items`, MTU), `Abort(reason)`, and `ConnectionAborted`
  - `Clients`: `All`, `Others`, `Caller`, `Client(id)`, `Clients(ids)`, `AllExcept(ids)`, `Group(name)`, `Groups(names)`, `GroupExcept(name, ids)`, `OthersInGroup(name)`
  - `Groups`: `AddToGroupAsync` / `RemoveFromGroupAsync`
  - `OnConnectedAsync` / `OnDisconnectedAsync(reason)`
- **`IHubContext<THub>`**:
  - `Start()` / `Stop(reason)` / `IsRunning`
  - `Clients`, through the generated extension property
  - `Groups`
  - `ConnectedClients`
  - `Disconnect(connectionId, reason)`
- **Disconnect is cooperative.** iOS `CBPeripheralManager` can't drop a central, so the host sends `Disconnect` and forgets the client. The client library disconnects itself when it receives it.
- **Client options**: `MaxClients` and `ValidateClient` (return a rejection reason) are set per hub through `AddBleSmartHub(..., o => ...)`.
- **Cleanup**: a peer that unsubscribes is removed right away. A periodic sweep also removes peers that no longer appear in `SubscribedCentrals`, because Android doesn't always report the unsubscribe.

## 6. Client (`Shiny.SmartBle.Client`)

- **`BleHubClient`** is the base class for the generated proxies. `IBleHubClient<TContract>` exposes:
  - `Hub`
  - `Status`, plus the `StatusChanged`, `Connected` and `Disconnected` events
  - `Discover()`: scans by the hub's service UUID
  - `Connect(host, options, ct)`:
    1. Connect.
    2. Request MTU 512.
    3. Subscribe to notifications.
    4. Handshake.
    5. Become `Ready`.
  - `Disconnect()`
  - `HostName`, `CanTransferFiles`
  - `UploadFile` / `UploadStream` / `DownloadFile`
- **Calls**:
  - A per-call timeout comes from `SmartBleOptions.RequestTimeout`. Streams have no overall timeout.
  - A `CancellationToken` sends `Cancel` to the host.
  - A host exception becomes `SmartBleRemoteException`.
  - A dropped connection fails every pending call with `SmartBleDisconnectedException`.
- Pushes are raised on a background thread. UI code marshals them to the main thread.

## 6a. File transfer (L2CAP)

Files don't go through hub framing. They use a separate L2CAP channel, and the client exposes them as their own methods.

- **Host**: `ConfigureBleHubHost(o => o.EnableFileTransfers(dir, ft => ...))`.
  - **Directory mode**, the default, wraps `OpenL2CapFileServer`: upload and download flags, `MaxUploadSize`, overwrite rules and an `Authorize` hook. Shiny refuses path traversal.
  - **Custom mode** registers `ISmartBleFileHandler`, which wraps `HandleL2CapRequests`.
- **PSM discovery**: the platform assigns the PSM at runtime, and it reaches the client in every hub's `HandshakeAck` together with the secure flag. On Android the secure and insecure channels listen separately, so the client must open the matching one. A PSM of 0 means file transfer is unavailable, and the file calls throw `SmartBleFileTransferNotSupportedException`.
- **Client**: `UploadFile`, `UploadStream` and `DownloadFile` map to Shiny's `IPeripheral.UploadFile` / `DownloadFile` and to the L2CAP channel extensions.
- **Platforms**: iOS/macOS, and Android API 29+. Elsewhere the host serves PSM 0 and logs a warning.
- **Identifying the client**: the L2CAP peer is matched to the hub's connected client by peer id where possible.

## 7. Repo layout

```
SmartBle.slnx
Directory.Build.props, Directory.Packages.props (central package management)
src/Shiny.SmartBle/                    net10.0: framing, arguments codec, serializer, options, [BleHubClient], exceptions
src/Shiny.SmartBle.SourceGenerators/   netstandard2.0 Roslyn generator (packed into the core package's analyzers folder)
src/Shiny.SmartBle.Host/               BleHub, IBleHubHost, IHubContext, groups, L2CAP file server
src/Shiny.SmartBle.Client/             BleHubClient, discovery, connection sharing, file transfer
tests/Shiny.SmartBle.Tests/            xUnit: framing, codec, generated hub + proxy end to end over an in-memory radio
samples/TicTacToe/                     .NET MAUI (iOS + Android), Shiny.Maui.Shell
```

The libraries target `net10.0` and reference only the Shiny abstractions. The app registers the platform stacks with `AddBluetoothLE()` / `AddBluetoothLeHosting()`.

## 8. Tic-tac-toe sample

- **Hub**: `GameHub : BleHub<IGameHub>`
  - `Join(name, avatar)` → `JoinResult(mark, state)`. The first client is O, and later clients are spectators placed in the `"spectators"` group.
  - `MakeMove(cell)` → `MoveResult`
  - `Rematch()`
  - `SendEmote(emoji)`
  - pushes `StateChanged(GameState)` and `Emote(from, emoji)`
  - `OnDisconnectedAsync` frees the O seat.
- **Host-local play**: the host's own UI calls `GameEngine` directly, then broadcasts through `IHubContext<GameHub>`. That shows hub usage from outside a hub.
- **Spectators**: "Remove spectators" uses `IHubContext.Disconnect` for every member of the spectators group.
- **Avatars**: the client uploads its avatar over L2CAP before calling `Join`. Each side downloads the other's avatar using the file name carried in `GameState`.
- **Pages**: `HomePage` (name, avatar, Host or Join), `JoinPage` (live discovery list), and `GamePage` (board, avatars, score, emotes, rematch, leave). They use `[ShellMap]` with `AddGeneratedMaps()`, plus `INavigator` and `IDialogs`.
- **Platform setup**:
  - Android: `BLUETOOTH_SCAN` (`neverForLocation`), `BLUETOOTH_CONNECT` and `BLUETOOTH_ADVERTISE`, plus the legacy permissions with `maxSdkVersion=30`.
  - iOS: `NSBluetoothAlwaysUsageDescription`.

## 9. Decisions

| Question | Decision |
|---|---|
| Programming model | SignalR-style hubs. Mediator was dropped (2026-10-02) |
| Binding | Source generated: AOT/trim safe, with compile-time diagnostics |
| Contract | One `[BleHubClient]` interface. Methods are client → host, `event`s are host → client |
| Host → client | Fire-and-forget pushes. No client results in v1 |
| v1 hub features | Groups, streaming (`IAsyncEnumerable<T>`), `OnConnected` / `OnDisconnected`, `Context.Abort`, multiple hubs |
| GATT | One write+notify characteristic per hub, routed by characteristic UUID |
| Platforms | iOS + Android in both roles. Windows can't host |
| Payload size | Hub messages are chunked over GATT, 256 KB max by default. Files go over L2CAP |
| Serialization | Pluggable `ISmartBleSerializer`. The default is Shiny's AOT JSON, using contexts registered with `Json.AddContext` |
| Background | Foreground only |
| Auth / security | None in v1, apart from opt-in exposure and the `ValidateClient` handshake hook |

## 10. Status

- **2026-10-02**: the Mediator-based v0 was built and tested (42 tests), then superseded by this hub design.
- **2026-10-02**: hub rewrite done. Generator, host, client and sample are migrated.
  - 55 tests pass: framing, argument codec, generator diagnostics, and generated hub + proxy end to end. The end-to-end tests cover calls, streams, cancellation, timeouts, errors, pushes in order, Others and groups, `IHubContext`, cooperative disconnect, connection loss and client limits.
  - The sample builds for iOS and Android.
  - `Context.Abort()` inside a hub method takes effect after that method's reply is sent.
  - Per-hub start/stop added. A fake `IBleHostingManager` now tests `BleHubHost` itself: GATT writes and notifications, shared and separate services, advertising updates and restarts. 63 tests pass.
  - **Not yet verified:** a real two-device run.

## 11. Future

- Client results (the host awaits a value from a client)
- Client → host streaming (`IAsyncEnumerable<T>` parameters)
- Sending oversized hub payloads over L2CAP automatically
- Auth and bonding options
- Background support
- Write-without-response with flow control for throughput
- An optional Mediator adapter package

## 12. Known limitations and risks

- BLE needs **two physical devices**. The iOS simulator has no Bluetooth.
- When an iOS host is backgrounded, it advertises only through the overflow area and without a local name. This is out of scope (foreground only).
- 31-byte advertisements: a 128-bit UUID plus a long local name may not fit. Keep `LocalName` short.
- Throughput with write-with-response is a few KB/s, which is fine for game- and command-sized messages.
