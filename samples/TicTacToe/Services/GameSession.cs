using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hubs;
using TicTacToe.Game;
using TicTacToe.Hub;

namespace TicTacToe.Services;

/// <summary>
/// Hides whether this device is the host or a client from the UI.
/// The host plays through the GameEngine directly and pushes to clients with IHubContext&lt;GameHub&gt;;
/// a client plays through the generated IGameHub proxy.
/// </summary>
public class GameSession
{
    public const string HostAvatarFile = "avatar-host.jpg";
    public const int MaxChatLength = 200;

    readonly BleHubHostOptions hostOptions;
    readonly IHubContext<GameHub> hub;
    readonly IBleHubClient<IGameHub> client;
    readonly GameEngine engine;
    readonly ILogger<GameSession> logger;


    public GameSession(
        BleHubHostOptions hostOptions,
        IHubContext<GameHub> hub,
        IBleHubClient<IGameHub> client,
        GameEngine engine,
        ILogger<GameSession> logger
    )
    {
        this.hostOptions = hostOptions;
        this.hub = hub;
        this.client = client;
        this.engine = engine;
        this.logger = logger;
    }


    public bool IsActive { get; private set; }
    public bool IsHost { get; private set; }
    public Mark MyMark { get; private set; }
    public string? MyName { get; private set; }
    public GameState? State { get; private set; }

    public event EventHandler<GameState>? StateChanged;
    public event EventHandler<(string From, string Emoji)>? EmoteReceived;
    public event EventHandler<ChatMessage>? ChatReceived;

    /// <summary>
    /// A client's session ended - the host left, removed us or the connection dropped
    /// </summary>
    public event EventHandler<string?>? Ended;


    /// <summary>
    /// Where the host serves files from (and where client uploads land)
    /// </summary>
    public static string HostFilesDirectory => Path.Combine(FileSystem.AppDataDirectory, "host-files");

    static string ClientCacheDirectory => Path.Combine(FileSystem.CacheDirectory, "remote-avatars");


    // ---------- host ----------

    public async Task StartHosting(string playerName, string? avatarPath)
    {
        Directory.CreateDirectory(HostFilesDirectory);
        var hostAvatar = Path.Combine(HostFilesDirectory, HostAvatarFile);
        if (avatarPath != null && File.Exists(avatarPath))
            File.Copy(avatarPath, hostAvatar, true);
        else if (File.Exists(hostAvatar))
            File.Delete(hostAvatar);

        // the advertisement only has room for a short name next to a 128-bit service UUID
        this.hostOptions.LocalName = playerName.Length > 8 ? playerName[..8] : playerName;
        this.engine.NewSession(playerName, avatarPath == null ? null : HostAvatarFile);
        await this.hub.Start();

        this.IsHost = true;
        this.IsActive = true;
        this.MyMark = Mark.X;
        this.MyName = playerName;
        this.State = this.engine.Snapshot();
    }


    /// <summary>
    /// Host only - pushes the current board to every client and to the host's own UI
    /// </summary>
    public async Task BroadcastState()
    {
        var state = this.engine.Snapshot();
        this.State = state;
        this.StateChanged?.Invoke(this, state);
        await this.hub.Clients.All.StateChanged(state);
    }


    public async Task BroadcastEmote(string from, string emoji)
    {
        this.ShowEmoteLocally(from, emoji);
        await this.hub.Clients.All.Emote(from, emoji);
    }


    public void ShowEmoteLocally(string from, string emoji) => this.EmoteReceived?.Invoke(this, (from, emoji));


    /// <summary>
    /// Host only - the host stamps and trims every line so clients can't spoof the sender or flood the board
    /// </summary>
    public async Task BroadcastChat(string from, Mark mark, string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            throw new ArgumentException("Message is empty");

        if (text.Length > MaxChatLength)
            text = text[..MaxChatLength];

        var msg = new ChatMessage(from, mark, text, DateTimeOffset.UtcNow);
        this.ChatReceived?.Invoke(this, msg);
        await this.hub.Clients.All.ChatReceived(msg);
    }


    /// <summary>
    /// Disconnects everyone in the spectators group - shows off host initiated disconnects from outside a hub
    /// </summary>
    public async Task<int> KickSpectators()
    {
        var spectators = this.hub.Groups.GetMembers(GameHub.Spectators);
        foreach (var id in spectators)
            await this.hub.Disconnect(id, $"{this.State?.XPlayer} removed spectators");

        return spectators.Count;
    }


    // ---------- client pushes ----------

    /// <summary>
    /// Starts relaying pushes from the host - call from the game page's OnAppearing
    /// </summary>
    public void Attach()
    {
        this.Detach(); // guard against a double attach
        this.client.Hub.StateChanged += this.OnHubStateChanged;
        this.client.Hub.Emote += this.OnHubEmote;
        this.client.Hub.ChatReceived += this.OnHubChat;
        this.client.Disconnected += this.OnClientDisconnected;
    }


    /// <summary>
    /// Stops relaying pushes from the host - call from the game page's OnDisappearing
    /// </summary>
    public void Detach()
    {
        this.client.Hub.StateChanged -= this.OnHubStateChanged;
        this.client.Hub.Emote -= this.OnHubEmote;
        this.client.Hub.ChatReceived -= this.OnHubChat;
        this.client.Disconnected -= this.OnClientDisconnected;
    }


    // pushes from the host arrive as plain .NET events on the generated proxy
    void OnHubStateChanged(GameState state)
    {
        this.State = state;
        this.StateChanged?.Invoke(this, state);
    }

    void OnHubEmote(string from, string emoji) => this.EmoteReceived?.Invoke(this, (from, emoji));

    void OnHubChat(ChatMessage msg) => this.ChatReceived?.Invoke(this, msg);

    void OnClientDisconnected(object? sender, HubDisconnect reason)
    {
        if (!this.IsHost && this.IsActive)
        {
            this.IsActive = false;
            this.Ended?.Invoke(this, reason.Description);
        }
    }


    // ---------- client ----------

    public async Task JoinHost(BleHubHostInfo hostInfo, string playerName, string? avatarPath)
    {
        await this.client.Connect(hostInfo, new BleHubConnectOptions(playerName, AppInfo.Current.VersionString));
        try
        {
            string? avatarFile = null;
            if (avatarPath != null && File.Exists(avatarPath) && this.client.CanTransferFiles)
            {
                try
                {
                    // a stalled transfer must not hold up joining the game
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    avatarFile = $"avatar-{Guid.NewGuid():N}.jpg";
                    await this.client.UploadFile(avatarPath, avatarFile, cancellationToken: cts.Token);
                }
                catch (Exception ex)
                {
                    // the game doesn't need an avatar
                    this.logger.LogWarning(ex, "Avatar upload failed");
                    avatarFile = null;
                }
            }

            var result = await this.client.Hub.Join(playerName, avatarFile);
            this.IsHost = false;
            this.IsActive = true;
            this.MyMark = result.YouAre;
            this.MyName = playerName;
            this.State = result.State;
        }
        catch
        {
            await this.client.Disconnect();
            throw;
        }
    }


    // ---------- both ----------

    public async Task<MoveResult> MakeMove(int cell)
    {
        if (!this.IsHost)
            return await this.client.Hub.MakeMove(cell);

        var error = this.engine.TryMove(Mark.X, cell);
        if (error == null)
            await this.BroadcastState();

        return new MoveResult(error == null, error);
    }


    public async Task Rematch()
    {
        if (!this.IsHost)
        {
            await this.client.Hub.Rematch();
            return;
        }
        this.engine.Rematch();
        await this.BroadcastState();
    }


    public Task Emote(string emoji) => this.IsHost
        ? this.BroadcastEmote(this.engine.Snapshot().XPlayer, emoji)
        : this.client.Hub.SendEmote(emoji);


    public Task Chat(string text) => this.IsHost
        ? this.BroadcastChat(this.engine.Snapshot().XPlayer, Mark.X, text)
        : this.client.Hub.SendChat(text);


    public async Task Leave()
    {
        this.IsActive = false;
        if (this.IsHost)
            await this.hub.Stop("The host ended the game");
        else
            await this.client.Disconnect();
    }


    /// <summary>
    /// Resolves an avatar file name from the game state to a local path - downloading it from the host over L2CAP when needed
    /// </summary>
    public async Task<string?> GetAvatar(string? file)
    {
        if (file == null)
            return null;

        if (this.IsHost)
        {
            var hostPath = Path.Combine(HostFilesDirectory, file);
            return File.Exists(hostPath) ? hostPath : null;
        }

        var local = Path.Combine(ClientCacheDirectory, file);
        if (File.Exists(local))
            return local;

        if (!this.client.CanTransferFiles)
            return null;

        try
        {
            Directory.CreateDirectory(ClientCacheDirectory);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await this.client.DownloadFile(file, local, cancellationToken: cts.Token);
            return local;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Could not download avatar {File}", file);
            return null;
        }
    }
}
