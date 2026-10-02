# Shiny.BluetoothLE.Hubs

Shiny.BluetoothLE.Hubs gives you SignalR-style hubs over Bluetooth LE. One device hosts a hub, and nearby devices discover it, connect, and call it through a source-generated, strongly typed proxy. The host pushes events back to everyone, to some clients, or to groups. Files move over L2CAP, a faster direct channel between the devices.

It's built on Shiny.BluetoothLE and Shiny.BluetoothLE.Hosting, and it is AOT- and trim-safe with no reflection.

```csharp
// shared contract
[BleHubClient]
public interface IGameHub
{
    Task<MoveResult> MakeMove(int cell);                          // client -> host
    IAsyncEnumerable<int> Countdown(int from, CancellationToken ct);
    event Action<GameState> StateChanged;                         // host -> clients
}

// host
public class GameHub(GameEngine engine) : BleHub<IGameHub>
{
    public async Task<MoveResult> MakeMove(int cell)
    {
        // Context.Client, Context.Abort(), Groups, OnConnectedAsync/OnDisconnectedAsync
        await Clients.All.StateChanged(engine.Snapshot());        // generated typed push
        return new MoveResult(true, null);
    }
    ...
}

// client - GameHubClient is generated
client.Hub.StateChanged += state => ...;
await client.Connect(host, new BleHubConnectOptions("Allan"));
var result = await client.Hub.MakeMove(4);
await client.UploadFile(path, "avatar.jpg");                      // L2CAP
```

## Packages

| Project | Purpose |
|---|---|
| `Shiny.BluetoothLE.Hubs` | Wire protocol (framing, chunking, reassembly), argument codec, serializer, `[BleHubClient]`. Ships the source generator |
| `Shiny.BluetoothLE.Hubs.Host` | `BleHub<T>`, `IBleHubHost`, `IHubContext<THub>`, groups, L2CAP file server |
| `Shiny.BluetoothLE.Hubs.Client` | `BleHubClient` (base of the generated proxies), discovery, shared connections, file upload and download |

## Setup

```csharp
Json.AddContext(MyJsonContext.Default);     // hub arguments/results are AOT-safe JSON by default

// host
services.AddBluetoothLeHosting();
services.AddBleHub<GameHub>(ServiceUuid, CharacteristicUuid, o => o.MaxClients = 6);
services.ConfigureBleHubHost(o => o.EnableFileTransfers(Path.Combine(FileSystem.AppDataDirectory, "files")));
await serviceProvider.GetRequiredService<IBleHubHost>().Start();

// client
services.AddBluetoothLE();
services.AddBleHubClient<IGameHub>(ServiceUuid, CharacteristicUuid);   // inject IBleHubClient<IGameHub>, GameHubClient or IGameHub
```

- **Contract**: hub methods return `Task`, `Task<T>` or `IAsyncEnumerable<T>`. A trailing `CancellationToken` is passed through to the host. Events are `Action` / `Action<T1..T4>`. Compile errors are reported as `SBH001`–`SBH006`.
- **Hubs**: a new hub instance runs, in its own DI scope, for every call, like SignalR. The hub may take the contract's `CancellationToken` or leave it out.
- **Outside a hub**: inject `IHubContext<GameHub>` and call `context.Clients.All.StateChanged(state)`. `Clients` is a generated C# 14 extension property.
- **Start/stop**:
  - `IBleHubHost.Start()` / `Stop()` start or stop every hub.
  - `IHubContext<THub>.Start()` / `Stop(reason)` control one hub. A stopped hub disconnects its clients and refuses new ones.
  - A GATT service shared by several hubs stays up while any of them is running.
  - The advertisement follows the running hubs.
- **Disconnect is cooperative**, because iOS peripherals can't drop a central. `Context.Abort()` and `IHubContext.Disconnect()` ask the client to leave. The client library does so and raises `Disconnected` with the reason.
- **Multiple hubs**: each hub needs its own characteristic. Sharing one service UUID is recommended so the advertisement holds only one 128-bit UUID. Hub clients on the same device share one BLE connection.

See [PLAN.md](PLAN.md) for the wire protocol, the design decisions and the roadmap.

## Sample: Tic Tac Toe

`samples/TicTacToe` is a .NET MAUI app for iOS and Android that uses Shiny.Maui.Shell.

- One phone taps **Host a game** and plays X.
- The next phone to tap **Join a game** plays O. Later phones join the `spectators` group.
- Moves are hub calls, and board updates and emotes are hub pushes.
- Avatars are uploaded and downloaded over L2CAP.

BLE needs two physical devices: simulators and emulators have no usable Bluetooth.

```bash
dotnet build samples/TicTacToe -f net10.0-android -t:Run
dotnet build samples/TicTacToe -f net10.0-ios -t:Run
```

## Tests

```bash
dotnet test tests/Shiny.BluetoothLE.Hubs.Tests
```

The tests run the generated hub dispatcher and the generated client proxy against each other through an in-memory "radio", plus diagnostic tests for the generator. No hardware is needed.

## Changelog

### 1.0.0-alpha - TBD
- Initial release: source generated BLE hubs (calls, streams, typed pushes, groups, lifecycle hooks, per-hub start/stop), generated client proxies with shared connections, and L2CAP file transfers.
