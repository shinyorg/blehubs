using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.BluetoothLE.Hubs;

public static class BleHubServiceCollectionExtensions
{
    /// <summary>
    /// Adjusts protocol limits (payload size, timeouts). Optional - defaults apply otherwise.
    /// </summary>
    public static IServiceCollection ConfigureBleHubProtocol(this IServiceCollection services, Action<BleHubProtocolOptions> configure)
    {
        services.AddBleHubCore();
        services.AddSingleton<IConfigureBleHubProtocol>(new ConfigureBleHubProtocol(configure));
        return services;
    }


    /// <summary>
    /// Registers the shared protocol services. Called by AddBleHub / AddBleHubClient.
    /// </summary>
    public static IServiceCollection AddBleHubCore(this IServiceCollection services)
    {
        services.TryAddSingleton(sp =>
        {
            var options = new BleHubProtocolOptions();
            foreach (var c in sp.GetServices<IConfigureBleHubProtocol>())
                c.Configure(options);
            return options;
        });
        services.TryAddSingleton<IBleHubSerializer>(sp => new ShinyJsonBleHubSerializer(sp.GetService<ISerializer>()));
        return services;
    }
}


internal interface IConfigureBleHubProtocol
{
    void Configure(BleHubProtocolOptions options);
}

internal sealed class ConfigureBleHubProtocol(Action<BleHubProtocolOptions> action) : IConfigureBleHubProtocol
{
    public void Configure(BleHubProtocolOptions options) => action(options);
}
