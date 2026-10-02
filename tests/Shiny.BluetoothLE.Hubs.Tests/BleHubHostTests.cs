using Microsoft.Extensions.DependencyInjection;

namespace Shiny.BluetoothLE.Hubs.Tests;

/// <summary>
/// Drives the real BleHubHost against an in-memory GATT server - start/stop per hub, shared services, advertising
/// </summary>
public class BleHubHostTests : IAsyncLifetime
{
    const string ServiceA = "6e400001-b5a3-f393-e0a9-e50e24dcca9e";
    const string ServiceB = "7e400001-b5a3-f393-e0a9-e50e24dcca9e";
    const string TestChar = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";
    const string SecondChar = "6e400003-b5a3-f393-e0a9-e50e24dcca9e";

    static BleHubHostTests() => Json.AddContext(TestJsonContext.Default);

    readonly FakeHostingManager hosting = new();
    readonly BleHubProtocolOptions options = new() { RequestTimeout = TimeSpan.FromSeconds(5) };
    readonly IBleHubSerializer serializer = new ShinyJsonBleHubSerializer();
    ServiceProvider services = null!;
    BleHubHost host = null!;
    IHubContext<TestHub> testHub = null!;
    IHubContext<SecondHub> secondHub = null!;


    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await this.services.DisposeAsync();


    void Build(string secondService)
    {
        this.services = new ServiceCollection()
            .AddSingleton<HubLog>()
            .AddTransient<TestHub>()
            .AddTransient<SecondHub>()
            .BuildServiceProvider();

        this.host = new BleHubHost(
            this.hosting,
            [
                new BleHubRegistration(typeof(TestHub), ServiceA, TestChar, new BleHubOptions()),
                new BleHubRegistration(typeof(SecondHub), secondService, SecondChar, new BleHubOptions())
            ],
            new BleHubHostOptions { LocalName = "Host", ClientSweepInterval = TimeSpan.FromMinutes(5) },
            this.options,
            this.serializer,
            this.services
        );
        this.testHub = new HubContext<TestHub>(() => this.host.GetRuntime(typeof(TestHub)), this.host);
        this.secondHub = new HubContext<SecondHub>(() => this.host.GetRuntime(typeof(SecondHub)), this.host);
    }


    /// <summary>
    /// A client proxy wired to the fake GATT server like a real central would be
    /// </summary>
    async Task<TContract> Connect<TContract>(string characteristicUuid, string serviceUuid, string centralId) where TContract : class
    {
        var client = BleHubClientFactories.Create<TContract>(new BleHubClientServices(this.options, this.serializer), serviceUuid, characteristicUuid);
        var central = new FakeCentral(centralId);
        var ch = this.hosting.Characteristic(characteristicUuid);

        var previous = ch.Notified;
        ch.Notified = (id, frame) =>
        {
            if (id == centralId)
                client.ReceiveFrame(frame);
            else
                previous?.Invoke(id, frame);
        };
        await ch.Subscribe(central);
        await client.ConnectTransport((frame, _) => ch.Write(central, frame), central.Mtu, new BleHubConnectOptions(centralId), CancellationToken.None);
        return (TContract)(object)client;
    }


    static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 150 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition(), "condition not met in time");
    }


    [Fact]
    public async Task StartRunsEveryHubInOneSharedService()
    {
        this.Build(ServiceA);
        await this.host.Start();

        Assert.True(this.host.IsRunning);
        Assert.True(this.testHub.IsRunning);
        Assert.True(this.secondHub.IsRunning);
        Assert.Contains($"add-service:{ServiceA}:2", this.hosting.Log);
        Assert.Equal([ServiceA], this.hosting.AdvertisedServices);
        Assert.Equal("Host", this.hosting.AdvertisedName);

        var test = await this.Connect<ITestHub>(TestChar, ServiceA, "c1");
        var second = await this.Connect<ISecondHub>(SecondChar, ServiceA, "c2");
        Assert.Equal(new Payload("x", 1), await test.Echo(new Payload("x", 1)));
        Assert.Equal("pong", await second.Ping());
    }


    [Fact]
    public async Task StartingOneHubLeavesTheOtherRefusingClients()
    {
        this.Build(ServiceA);
        await this.testHub.Start();

        Assert.True(this.testHub.IsRunning);
        Assert.False(this.secondHub.IsRunning);
        Assert.True(this.host.IsRunning);

        // the shared service holds both characteristics, but only the running hub accepts clients
        await this.Connect<ITestHub>(TestChar, ServiceA, "c1");
        var ex = await Assert.ThrowsAsync<BleHubException>(() => this.Connect<ISecondHub>(SecondChar, ServiceA, "c2"));
        Assert.Contains("Hub is not running", ex.Message);
    }


    [Fact]
    public async Task StoppingOneHubKeepsASharedServiceForTheOther()
    {
        this.Build(ServiceA);
        await this.host.Start();

        var test = await this.Connect<ITestHub>(TestChar, ServiceA, "c1");
        var second = await this.Connect<ISecondHub>(SecondChar, ServiceA, "c2");
        string? kickedReason = null;
        ((IBleHubConnection)test).Disconnected += (_, r) => kickedReason = r;

        await this.testHub.Stop("maintenance");

        await WaitFor(() => kickedReason != null);
        Assert.Equal("maintenance", kickedReason);
        Assert.False(this.testHub.IsRunning);
        Assert.True(this.hosting.HasService(ServiceA));
        Assert.Equal([ServiceA], this.hosting.AdvertisedServices);
        Assert.Equal("pong", await second.Ping());
        Assert.DoesNotContain($"remove-service:{ServiceA}", this.hosting.Log);
    }


    [Fact]
    public async Task StoppingTheLastHubTearsEverythingDown()
    {
        this.Build(ServiceA);
        await this.host.Start();

        await this.testHub.Stop();
        await this.secondHub.Stop();

        Assert.False(this.host.IsRunning);
        Assert.False(this.hosting.HasService(ServiceA));
        Assert.False(this.hosting.IsAdvertising);
    }


    [Fact]
    public async Task HubsOnSeparateServicesAreAddedAndRemovedIndependently()
    {
        this.Build(ServiceB);
        await this.host.Start();
        Assert.Equal(2, this.hosting.AdvertisedServices.Length);

        await this.secondHub.Stop();

        Assert.False(this.hosting.HasService(ServiceB));
        Assert.True(this.hosting.HasService(ServiceA));
        Assert.Equal([ServiceA], this.hosting.AdvertisedServices);

        await this.secondHub.Start();
        Assert.True(this.hosting.HasService(ServiceB));
        Assert.Equal(2, this.hosting.AdvertisedServices.Length);
    }


    [Fact]
    public async Task HubCanBeRestarted()
    {
        this.Build(ServiceA);
        await this.testHub.Start();
        await this.testHub.Stop();
        await this.testHub.Start();

        var test = await this.Connect<ITestHub>(TestChar, ServiceA, "c1");
        Assert.Equal("a1b2", await test.Concat("a", 1, new Payload("b", 2)));
    }


    [Fact]
    public async Task HostStopStopsEveryHub()
    {
        this.Build(ServiceA);
        await this.host.Start();
        var test = await this.Connect<ITestHub>(TestChar, ServiceA, "c1");
        var disconnected = false;
        ((IBleHubConnection)test).Disconnected += (_, _) => disconnected = true;

        await this.host.Stop("bye");

        await WaitFor(() => disconnected);
        Assert.False(this.testHub.IsRunning);
        Assert.False(this.secondHub.IsRunning);
        Assert.False(this.hosting.IsAdvertising);
    }
}
