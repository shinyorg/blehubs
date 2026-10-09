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
    /// Every hub lives in this one service, and it is what hosts advertise and clients scan for. The library has a default;
    /// an app's own keeps other apps' hosts out of the join list.
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

        // host side - one call serves every hub (and adds the platform BLE hosting stack)
        builder.Services.AddBleHubServer(server => server
            .ServiceUuid(GameServiceUuid)
            .Host(o => o.EnableFileTransfers(GameSession.HostFilesDirectory, ft =>
            {
                ft.MaxUploadSize = 512 * 1024;
                // only avatars move over L2CAP
                ft.Authorize = req => req.Request.FileName.StartsWith("avatar-", StringComparison.Ordinal)
                    && req.Request.FileName.EndsWith(".jpg", StringComparison.Ordinal);
            }))
            .AddHub<GameHub>(GameHubCharacteristicUuid, o =>
            {
                o.MaxClients = 6; // 1 opponent + spectators
                o.ValidateClient = info => String.IsNullOrWhiteSpace(info.Name) ? "A player name is required" : null;
            })
        );

        // client side - registers the generated GameHubClient (and adds the platform BLE stack)
        builder.Services.AddBleHubClient<IGameHub>(GameHubCharacteristicUuid, o => o.ServiceUuid = GameServiceUuid);

        builder.Services.AddSingleton<GameEngine>();
        builder.Services.AddSingleton<GameSession>();
        builder.Services.AddSingleton<PlayerSettings>();

        return builder.Build();
    }
}
