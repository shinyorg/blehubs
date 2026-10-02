using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.BluetoothLE.Hubs;

public static class HostServiceCollectionExtensions
{
    /// <summary>
    /// Serves <typeparamref name="THub"/> on a write+notify characteristic. Hubs may share a service UUID (recommended - one
    /// advertised UUID) but each needs its own characteristic. Requires AddBluetoothLeHosting.
    /// </summary>
    public static IServiceCollection AddBleHub<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THub>(
        this IServiceCollection services,
        string serviceUuid,
        string characteristicUuid,
        Action<BleHubOptions>? configure = null
    ) where THub : class
    {
        var options = new BleHubOptions();
        configure?.Invoke(options);

        services.AddBleHubCore();
        services.AddTransient<THub>();
        services.AddSingleton(new BleHubRegistration(
            typeof(THub),
            BleUuid.Normalize(serviceUuid, nameof(serviceUuid)),
            BleUuid.Normalize(characteristicUuid, nameof(characteristicUuid)),
            options
        ));
        services.AddSingleton<IHubContext<THub>>(sp =>
        {
            var host = sp.GetRequiredService<BleHubHost>();
            return new HubContext<THub>(() => host.GetRuntime(typeof(THub)), host);
        });
        services.TryAddSingleton<BleHubHost>();
        services.TryAddSingleton<IBleHubHost>(sp => sp.GetRequiredService<BleHubHost>());
        services.TryAddSingleton(sp =>
        {
            var o = new BleHubHostOptions();
            foreach (var c in sp.GetServices<ConfigureBleHubHostOptions>())
                c.Configure(o);
            return o;
        });
        return services;
    }


    /// <summary>
    /// Settings shared by every hub - advertised name, file transfers...
    /// </summary>
    public static IServiceCollection ConfigureBleHubHost(this IServiceCollection services, Action<BleHubHostOptions> configure)
    {
        services.AddSingleton(new ConfigureBleHubHostOptions(configure));
        return services;
    }
}


internal sealed class ConfigureBleHubHostOptions(Action<BleHubHostOptions> configure)
{
    public void Configure(BleHubHostOptions options) => configure(options);
}
