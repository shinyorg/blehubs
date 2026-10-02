using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.SmartBle;

public static class SmartBleServiceCollectionExtensions
{
    /// <summary>
    /// Adjusts protocol limits (payload size, timeouts). Optional - defaults apply otherwise.
    /// </summary>
    public static IServiceCollection ConfigureSmartBle(this IServiceCollection services, Action<SmartBleOptions> configure)
    {
        services.AddSmartBleCore();
        services.AddSingleton<IConfigureSmartBle>(new ConfigureSmartBle(configure));
        return services;
    }


    /// <summary>
    /// Registers the shared protocol services. Called by AddBleSmartHub / AddBleHubClient.
    /// </summary>
    public static IServiceCollection AddSmartBleCore(this IServiceCollection services)
    {
        services.TryAddSingleton(sp =>
        {
            var options = new SmartBleOptions();
            foreach (var c in sp.GetServices<IConfigureSmartBle>())
                c.Configure(options);
            return options;
        });
        services.TryAddSingleton<ISmartBleSerializer>(sp => new ShinyJsonSmartBleSerializer(sp.GetService<ISerializer>()));
        return services;
    }
}


internal interface IConfigureSmartBle
{
    void Configure(SmartBleOptions options);
}

internal sealed class ConfigureSmartBle(Action<SmartBleOptions> action) : IConfigureSmartBle
{
    public void Configure(SmartBleOptions options) => action(options);
}
