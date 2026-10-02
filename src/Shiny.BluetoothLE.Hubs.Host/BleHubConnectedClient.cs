using System.Collections.Concurrent;

namespace Shiny.BluetoothLE.Hubs;

/// <summary>
/// A client that has completed the handshake with a hub
/// </summary>
public sealed class BleHubConnectedClient
{
    internal BleHubConnectedClient(string id, string? name, string? appVersion, IReadOnlyDictionary<string, string> properties, int mtu)
    {
        this.Id = id;
        this.Name = name;
        this.AppVersion = appVersion;
        this.Properties = properties;
        this.Mtu = mtu;
    }

    /// <summary>
    /// The connection id - the platform's identifier for the central
    /// </summary>
    public string Id { get; }
    public string? Name { get; }
    public string? AppVersion { get; }
    public IReadOnlyDictionary<string, string> Properties { get; }
    public int Mtu { get; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Your own per connection state
    /// </summary>
    public ConcurrentDictionary<string, object> Items { get; } = new();

    public override string ToString() => $"{this.Name ?? "(unnamed)"} [{this.Id}]";
}
