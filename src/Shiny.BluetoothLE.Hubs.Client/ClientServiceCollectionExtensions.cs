using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE;

namespace Shiny.BluetoothLE.Hubs;

public static class ClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers the generated proxy for <typeparamref name="TContract"/> as a singleton. Resolve it as
    /// IBleHubClient&lt;TContract&gt; (connection + Hub) or as TContract. Requires AddBluetoothLE.
    /// </summary>
    public static IServiceCollection AddBleHubClient<TContract>(this IServiceCollection services, string serviceUuid, string characteristicUuid)
        where TContract : class
    {
        services.AddBleHubCore();
        services.TryAddSingleton(sp => new BleHubClientServices(
            sp.GetRequiredService<BleHubProtocolOptions>(),
            sp.GetRequiredService<IBleHubSerializer>(),
            sp.GetService<IBleManager>(),
            sp.GetService<ILoggerFactory>()
        ));

        services.AddSingleton<IBleHubClient<TContract>>(sp => (IBleHubClient<TContract>)BleHubClientFactories.Create<TContract>(
            sp.GetRequiredService<BleHubClientServices>(),
            serviceUuid,
            characteristicUuid
        ));
        services.AddSingleton(sp => (TContract)sp.GetRequiredService<IBleHubClient<TContract>>());
        return services;
    }
}
