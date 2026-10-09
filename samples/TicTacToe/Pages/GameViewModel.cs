using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shiny;
using Shiny.BluetoothLE.Hubs;
using TicTacToe.Game;
using TicTacToe.Hub;
using TicTacToe.Services;

namespace TicTacToe.Pages;

/// <summary>
/// The same view model serves the host and clients - GameSession raises StateChanged/EmoteReceived whether the change came
/// from this device (host) or arrived as a hub push (client).
/// </summary>
[ShellMap<GamePage>("Game")]
public partial class GameViewModel : ObservableObject,
    IPageLifecycleAware,
    INavigationConfirmation
{
    readonly ShellServices shell;
    readonly GameSession session;
    readonly PlayerSettings settings;
    CancellationTokenSource? emoteTimer;
    string? xAvatarFile;
    string? oAvatarFile;
    bool started;
    bool visible;


    public GameViewModel(ShellServices shell, GameSession session, PlayerSettings settings)
    {
        this.shell = shell;
        this.session = session;
        this.settings = settings;
        for (var i = 0; i < 9; i++)
            this.Cells.Add(new CellViewModel(i));
    }


    /// <summary>
    /// The game to join - null to host a new one
    /// </summary>
    [ShellProperty(required: false)]
    public BleHubHostInfo? Join { get; set; }

    public ObservableCollection<CellViewModel> Cells { get; } = new();
    public bool IsHost => this.session.IsHost;
    public string[] Emotes { get; } = ["👍", "😂", "😱", "🔥", "🤝"];
    public ObservableCollection<ChatLineViewModel> Chat { get; } = new();
    public int MaxChatLength => GameSession.MaxChatLength;

    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial string Role { get; set; } = "";
    [ObservableProperty] public partial string XName { get; set; } = "";
    [ObservableProperty] public partial string OName { get; set; } = "";
    [ObservableProperty] public partial string Score { get; set; } = "";
    [ObservableProperty] public partial ImageSource? XAvatar { get; set; }
    [ObservableProperty] public partial ImageSource? OAvatar { get; set; }
    [ObservableProperty] public partial bool IsXTurn { get; set; }
    [ObservableProperty] public partial bool IsOTurn { get; set; }
    [ObservableProperty] public partial bool CanRematch { get; set; }
    [ObservableProperty] public partial string? Emote { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChatButtonText))]
    public partial bool IsChatOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChatButtonText))]
    public partial int UnreadChat { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendChatCommand))]
    public partial string ChatText { get; set; } = "";

    public string ChatButtonText => this.IsChatOpen
        ? "Back to the board"
        : this.UnreadChat > 0 ? $"💬 Chat ({this.UnreadChat})" : "💬 Chat";


    // GameSession is a singleton - only hold its events while the page is on screen or it roots this view model forever
    public void OnAppearing()
    {
        this.Unhook(); // guard against a double OnAppearing
        this.session.Ended += this.OnSessionEnded;
        this.session.StateChanged += this.OnStateChanged;
        this.session.EmoteReceived += this.OnEmote;
        this.session.ChatReceived += this.OnChat;
        this.session.Attach();
        this.visible = true;

        // the game only starts once we're listening, so no push from the host can slip past us
        if (!this.started)
        {
            this.started = true;
            _ = this.Start();
        }
        else if (this.session.State is { } state)
        {
            // catch up on anything that changed while we weren't listening
            this.Apply(state);
        }
    }

    public void OnDisappearing()
    {
        this.visible = false;
        this.session.Detach();
        this.Unhook();
        this.emoteTimer?.Cancel();
    }


    void Unhook()
    {
        this.session.Ended -= this.OnSessionEnded;
        this.session.StateChanged -= this.OnStateChanged;
        this.session.EmoteReceived -= this.OnEmote;
        this.session.ChatReceived -= this.OnChat;
    }


    async Task Start()
    {
        this.IsBusy = true;
        this.Status = this.Join == null ? "Starting game..." : $"Joining {this.Join.Name ?? "the game"}...";
        try
        {
            if (this.Join == null)
                await this.session.StartHosting(this.settings.Name, this.settings.AvatarPath);
            else
                await this.session.JoinHost(this.Join, this.settings.Name, this.settings.AvatarPath);
        }
        catch (Exception ex)
        {
            this.IsBusy = false;
            await this.shell.Dialogs.Alert(this.Join == null ? "Could not host" : "Could not join", ex.Message);
            await this.shell.Navigator.GoBack();
            return;
        }
        this.IsBusy = false;

        // backed out while we were starting
        if (!this.visible)
        {
            await this.session.Leave();
            return;
        }

        this.OnPropertyChanged(nameof(this.IsHost));
        if (this.session.State is { } state)
            this.Apply(state);
    }


    // hub pushes are raised on a background thread
    void OnStateChanged(object? sender, GameState state)
        => this.shell.MainThread.BeginInvokeOnMainThread(() => this.Apply(state));

    void OnEmote(object? sender, (string From, string Emoji) e)
        => this.shell.MainThread.BeginInvokeOnMainThread(() => this.ShowEmote($"{e.From} {e.Emoji}"));


    void OnChat(object? sender, ChatMessage msg) => this.shell.MainThread.BeginInvokeOnMainThread(() =>
    {
        var mine = msg.From == this.session.MyName && msg.Mark == this.session.MyMark;
        this.Chat.Add(new ChatLineViewModel(msg, mine));

        if (!this.IsChatOpen && !mine)
        {
            this.UnreadChat++;
            this.ShowEmote($"💬 {msg.From}: {msg.Text}");
        }
    });


    [RelayCommand]
    void ToggleChat()
    {
        this.IsChatOpen = !this.IsChatOpen;
        if (this.IsChatOpen)
            this.UnreadChat = 0;
    }


    bool CanSendChat() => !String.IsNullOrWhiteSpace(this.ChatText);

    [RelayCommand(CanExecute = nameof(CanSendChat))]
    async Task SendChat()
    {
        var text = this.ChatText;
        this.ChatText = "";
        try
        {
            // the host echoes it back to everyone, us included
            await this.session.Chat(text);
        }
        catch (Exception ex)
        {
            this.ChatText = text;
            await this.shell.Dialogs.Alert("Chat", ex.Message);
        }
    }


    [RelayCommand]
    async Task Play(CellViewModel cell)
    {
        if (this.IsBusy || !String.IsNullOrEmpty(cell.Text))
            return;

        this.IsBusy = true;
        try
        {
            var result = await this.session.MakeMove(cell.Index);
            if (!result.Accepted)
                this.ShowEmote(result.Error);
        }
        catch (Exception ex)
        {
            await this.shell.Dialogs.Alert("Move failed", ex.Message);
        }
        finally
        {
            this.IsBusy = false;
        }
    }


    [RelayCommand]
    async Task Rematch()
    {
        try
        {
            await this.session.Rematch();
        }
        catch (Exception ex)
        {
            await this.shell.Dialogs.Alert("Rematch", ex.Message);
        }
    }


    [RelayCommand]
    async Task SendEmote(string emoji)
    {
        try
        {
            await this.session.Emote(emoji);
        }
        catch (Exception ex)
        {
            this.ShowEmote(ex.Message);
        }
    }


    [RelayCommand]
    async Task KickSpectators()
    {
        var count = await this.session.KickSpectators();
        this.ShowEmote(count == 0 ? "No spectators to remove" : $"Removed {count} spectator(s)");
    }


    public async Task<bool> CanNavigate()
    {
        if (!this.session.IsActive)
            return true;

        var message = this.session.IsHost
            ? "Everyone connected will be disconnected."
            : "You will leave the game.";

        if (!await this.shell.Dialogs.Confirm("End game?", message))
            return false;

        await this.session.Leave();
        return true;
    }


    void OnSessionEnded(object? sender, string? reason) => this.shell.MainThread.BeginInvokeOnMainThread(async () =>
    {
        await this.shell.Dialogs.Alert("Game over", reason ?? "The connection to the host was lost");
        await this.shell.Navigator.PopToRoot();
    });


    void Apply(GameState state)
    {
        var me = this.session.MyMark;
        for (var i = 0; i < 9; i++)
        {
            this.Cells[i].Text = state.Board[i] switch
            {
                Mark.X => "X",
                Mark.O => "O",
                _ => ""
            };
            this.Cells[i].IsWinning = state.WinningLine?.Contains(i) == true;
        }

        this.XName = state.XPlayer;
        this.OName = state.OPlayer ?? "Waiting...";
        this.Role = me switch
        {
            Mark.X => "You are X",
            Mark.O => "You are O",
            _ => "Spectating"
        };
        this.Score = $"{state.XWins} - {state.OWins}" + (state.Draws > 0 ? $"  ({state.Draws} draw{(state.Draws == 1 ? "" : "s")})" : "")
            + (state.Spectators > 0 ? $"  · {state.Spectators} watching" : "");

        this.IsXTurn = !state.IsOver && state.HasOpponent && state.Turn == Mark.X;
        this.IsOTurn = !state.IsOver && state.HasOpponent && state.Turn == Mark.O;
        this.CanRematch = state.IsOver && me != Mark.None;

        this.Status = state switch
        {
            { HasOpponent: false } => this.session.IsHost ? "Waiting for an opponent to join..." : "Waiting for a player...",
            { Winner: not Mark.None } when state.Winner == me => "You win! 🎉",
            { Winner: Mark.X } => $"{state.XPlayer} wins",
            { Winner: Mark.O } => $"{state.OPlayer} wins",
            { IsDraw: true } => "Draw",
            _ when state.Turn == me => "Your turn",
            _ => $"{(state.Turn == Mark.X ? state.XPlayer : state.OPlayer)}'s turn"
        };

        _ = this.LoadAvatars(state);
    }


    async Task LoadAvatars(GameState state)
    {
        if (state.XAvatar != this.xAvatarFile)
        {
            this.xAvatarFile = state.XAvatar;
            var path = await this.session.GetAvatar(state.XAvatar);
            this.XAvatar = path == null ? null : ImageSource.FromFile(path);
            if (path == null && this.xAvatarFile == state.XAvatar)
                this.xAvatarFile = null; // try again on the next board update
        }
        if (state.OAvatar != this.oAvatarFile)
        {
            this.oAvatarFile = state.OAvatar;
            var path = await this.session.GetAvatar(state.OAvatar);
            this.OAvatar = path == null ? null : ImageSource.FromFile(path);
            if (path == null && this.oAvatarFile == state.OAvatar)
                this.oAvatarFile = null; // try again on the next board update
        }
    }


    async void ShowEmote(string? text)
    {
        this.emoteTimer?.Cancel();
        var cts = this.emoteTimer = new CancellationTokenSource();
        this.Emote = text;
        try
        {
            await Task.Delay(2500, cts.Token);
            this.Emote = null;
        }
        catch (OperationCanceledException) { }
    }
}


public class ChatLineViewModel(ChatMessage msg, bool isMine)
{
    public string From { get; } = msg.Mark == Mark.None ? $"👀 {msg.From}" : $"{msg.From} ({msg.Mark})";
    public string Text { get; } = msg.Text;
    public string Time { get; } = msg.Sent.ToLocalTime().ToString("t");
    public bool IsMine { get; } = isMine;
    public Color NameColor { get; } = msg.Mark switch
    {
        Mark.X => Color.FromArgb("#E53935"),
        Mark.O => Color.FromArgb("#1E88E5"),
        _ => Colors.Gray
    };
}


public partial class CellViewModel(int index) : ObservableObject
{
    public int Index { get; } = index;
    public int Row => this.Index / 3;
    public int Column => this.Index % 3;

    [ObservableProperty] public partial string Text { get; set; } = "";
    [ObservableProperty] public partial bool IsWinning { get; set; }
}
