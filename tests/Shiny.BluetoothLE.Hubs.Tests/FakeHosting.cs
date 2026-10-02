using Shiny.BluetoothLE;
using Shiny.BluetoothLE.Hosting;
using HostedPeripheral = Shiny.BluetoothLE.Hosting.IPeripheral;

namespace Shiny.BluetoothLE.Hubs.Tests;

/// <summary>
/// An in-memory GATT server so BleHubHost's orchestration (services, advertising, writes, notifications) can be tested
/// </summary>
public class FakeHostingManager : IBleHostingManager
{
    readonly Dictionary<string, FakeService> services = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Log { get; } = new();
    public string[] AdvertisedServices { get; private set; } = [];
    public string? AdvertisedName { get; private set; }

    public AccessState AdvertisingAccessStatus => AccessState.Available;
    public AccessState GattAccessStatus => AccessState.Available;
    public bool IsAdvertising { get; private set; }
    public IReadOnlyList<IGattService> Services => this.services.Values.ToList();

    public FakeCharacteristic Characteristic(string uuid) => this.services.Values
        .SelectMany(x => x.Characteristics)
        .OfType<FakeCharacteristic>()
        .Single(x => String.Equals(x.Uuid, uuid, StringComparison.OrdinalIgnoreCase));

    public bool HasService(string uuid) => this.services.ContainsKey(uuid);

    public Task<AccessState> RequestAccess(bool advertise = true, bool connect = true) => Task.FromResult(AccessState.Available);

    public Task StartAdvertising(AdvertisementOptions? options = null)
    {
        this.IsAdvertising = true;
        this.AdvertisedServices = options?.ServiceUuids ?? [];
        this.AdvertisedName = options?.LocalName;
        this.Log.Add($"advertise:{String.Join(",", this.AdvertisedServices)}");
        return Task.CompletedTask;
    }

    public void StopAdvertising()
    {
        this.IsAdvertising = false;
        this.AdvertisedServices = [];
        this.Log.Add("stop-advertising");
    }

    public Task<L2CapInstance> OpenL2Cap(bool secure, Action<L2CapChannel> onOpen) => throw new NotSupportedException();
    public Task AdvertiseBeacon(Guid uuid, ushort major, ushort minor, sbyte? txpower = null) => throw new NotSupportedException();

    public Task<IGattService> AddService(string uuid, bool primary, Action<IGattServiceBuilder> serviceBuilder)
    {
        var service = new FakeService(uuid, primary);
        serviceBuilder(service);
        this.services[uuid] = service;
        this.Log.Add($"add-service:{uuid}:{service.Characteristics.Count}");
        return Task.FromResult<IGattService>(service);
    }

    public void RemoveService(string serviceUuid)
    {
        if (this.services.Remove(serviceUuid))
            this.Log.Add($"remove-service:{serviceUuid}");
    }

    public void ClearServices() => this.services.Clear();
}


public class FakeService(string uuid, bool primary) : IGattService, IGattServiceBuilder
{
    readonly List<IGattCharacteristic> characteristics = new();

    public string Uuid { get; } = uuid;
    public bool Primary { get; } = primary;
    public IReadOnlyList<IGattCharacteristic> Characteristics => this.characteristics;

    public IGattCharacteristic AddCharacteristic(string uuid, Action<IGattCharacteristicBuilder> characteristicBuilder)
    {
        var ch = new FakeCharacteristic(uuid);
        characteristicBuilder(ch);
        this.characteristics.Add(ch);
        return ch;
    }
}


public class FakeCharacteristic(string uuid) : IGattCharacteristic, IGattCharacteristicBuilder
{
    readonly List<HostedPeripheral> subscribed = new();
    Func<WriteRequest, Task>? onWrite;
    Func<CharacteristicSubscription, Task>? onSubscribe;

    public string Uuid { get; } = uuid;
    public CharacteristicProperties Properties => CharacteristicProperties.Write | CharacteristicProperties.Notify;
    public IReadOnlyList<HostedPeripheral> SubscribedCentrals => this.subscribed;

    /// <summary>
    /// Where notifications go - central id + frame
    /// </summary>
    public Action<string, byte[]>? Notified { get; set; }

    public IGattCharacteristicBuilder SetNotification(Func<CharacteristicSubscription, Task>? onSubscribe = null, NotificationOptions options = NotificationOptions.Notify)
    {
        this.onSubscribe = onSubscribe;
        return this;
    }

    public IGattCharacteristicBuilder SetWrite(Func<WriteRequest, Task> request, WriteOptions options = WriteOptions.Write)
    {
        this.onWrite = request;
        return this;
    }

    public IGattCharacteristicBuilder SetRead(Func<ReadRequest, Task<GattResult>> request, bool encrypted = false) => this;

    public Task Notify(byte[] data, params HostedPeripheral[] centrals) => this.Notify(data, CancellationToken.None, centrals);

    public Task Notify(byte[] data, CancellationToken cancellationToken, params HostedPeripheral[] centrals)
    {
        foreach (var central in centrals.Length == 0 ? this.subscribed.ToArray() : centrals)
            this.Notified?.Invoke(central.Uuid, data);
        return Task.CompletedTask;
    }

    public Task Subscribe(HostedPeripheral central)
    {
        this.subscribed.Add(central);
        return this.onSubscribe?.Invoke(new CharacteristicSubscription(this, central, true)) ?? Task.CompletedTask;
    }

    public Task Unsubscribe(HostedPeripheral central)
    {
        this.subscribed.Remove(central);
        return this.onSubscribe?.Invoke(new CharacteristicSubscription(this, central, false)) ?? Task.CompletedTask;
    }

    public async Task Write(HostedPeripheral central, byte[] data)
    {
        var state = GattState.Success;
        await this.onWrite!(new WriteRequest(this, central, data, 0, true, s => state = s));
        if (state != GattState.Success)
            throw new InvalidOperationException($"GATT write failed: {state}");
    }
}


public class FakeCentral(string uuid, int mtu = 185) : HostedPeripheral
{
    public string Uuid { get; } = uuid;
    public int Mtu { get; } = mtu;
    public object? Context { get; set; }
}
