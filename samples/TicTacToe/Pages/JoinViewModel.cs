using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shiny;
using Shiny.SmartBle;
using TicTacToe.Hub;
using TicTacToe.Services;

namespace TicTacToe.Pages;

[ShellMap<JoinPage>("Join")]
public partial class JoinViewModel(
    ShellServices shell,
    IBleHubClient<IGameHub> client,
    GameSession session,
    PlayerSettings settings
) : ObservableObject, IPageLifecycleAware
{
    IDisposable? scan;

    public ObservableCollection<HostItem> Hosts { get; } = new();
    [ObservableProperty] public partial bool IsConnecting { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "Looking for games nearby...";


    public void OnAppearing()
    {
        this.Hosts.Clear();
        this.scan = client
            .Discover()
            .Subscribe(
                found => shell.MainThread.BeginInvokeOnMainThread(() => this.OnFound(found)),
                ex => shell.MainThread.BeginInvokeOnMainThread(() => this.Status = ex.Message)
            );
    }


    public void OnDisappearing()
    {
        this.scan?.Dispose();
        this.scan = null;
    }


    void OnFound(BleHubHostInfo info)
    {
        var existing = this.Hosts.FirstOrDefault(x => x.Info.Id == info.Id);
        if (existing == null)
            this.Hosts.Add(new HostItem(info));
        else
            existing.Update(info);
    }


    [RelayCommand]
    async Task Connect(HostItem item)
    {
        if (this.IsConnecting)
            return;

        this.OnDisappearing();
        this.IsConnecting = true;
        this.Status = $"Joining {item.Name}...";
        try
        {
            await session.JoinHost(item.Info, settings.Name, settings.AvatarPath);
            await shell.Navigator.NavigateToGame();
        }
        catch (Exception ex)
        {
            await shell.Dialogs.Alert("Could not join", ex.Message);
            this.Status = "Looking for games nearby...";
            this.OnAppearing();
        }
        finally
        {
            this.IsConnecting = false;
        }
    }
}


public partial class HostItem(BleHubHostInfo info) : ObservableObject
{
    public BleHubHostInfo Info { get; private set; } = info;
    [ObservableProperty] public partial string Name { get; set; } = info.Name ?? "Unnamed game";
    [ObservableProperty] public partial int Rssi { get; set; } = info.Rssi;

    public void Update(BleHubHostInfo info)
    {
        this.Info = info;
        this.Rssi = info.Rssi;
        if (info.Name != null)
            this.Name = info.Name;
    }
}
