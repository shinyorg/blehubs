using Microsoft.Extensions.Logging;
using Shiny;
using Shiny.BluetoothLE.Hubs;
using TicTacToe.Game;
using TicTacToe.Hub;
using TicTacToe.Services;

namespace TicTacToe;

public static class MauiProgram
{
    /// <summary>
    /// Every TicTacToe host advertises this service - clients only see devices advertising it
    /// </summary>
    public const string GameServiceUuid = "37625845-7287-4de0-92f8-0b8a25e755ec";

    /// <summary>
    /// The write+notify characteristic that carries the GameHub
    /// </summary>
    public const string GameHubCharacteristicUuid = "37625846-7287-4de0-92f8-0b8a25e755ec";


    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseShiny()
            .UseShinyShell(x => x.AddGeneratedMaps())
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddDebug();
#endif

        // hub arguments and results travel as JSON - register the source generated context (AOT safe)
        Json.AddContext(GameJsonContext.Default);

        // the platform BLE stacks - this app can be either the host or a client
        builder.Services.AddBluetoothLE();
        builder.Services.AddBluetoothLeHosting();

        // host side
        builder.Services.AddBleHub<GameHub>(GameServiceUuid, GameHubCharacteristicUuid, o =>
        {
            o.MaxClients = 6; // 1 opponent + spectators
            o.ValidateClient = info => String.IsNullOrWhiteSpace(info.Name) ? "A player name is required" : null;
        });
        builder.Services.ConfigureBleHubHost(o => o.EnableFileTransfers(GameSession.HostFilesDirectory, ft =>
        {
            ft.MaxUploadSize = 512 * 1024;
            // only avatars move over L2CAP
            ft.Authorize = req => req.Request.FileName.StartsWith("avatar-", StringComparison.Ordinal)
                && req.Request.FileName.EndsWith(".jpg", StringComparison.Ordinal);
        }));

        // client side - registers the generated GameHubClient
        builder.Services.AddBleHubClient<IGameHub>(GameServiceUuid, GameHubCharacteristicUuid);

        builder.Services.AddSingleton<GameEngine>();
        builder.Services.AddSingleton<GameSession>();
        builder.Services.AddSingleton<PlayerSettings>();

        return builder.Build();
    }
}
