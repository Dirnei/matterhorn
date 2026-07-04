using System.Net;
using System.Net.Http.Json;
using Akka.TestKit.Xunit2;
using Matter2Mqtt.Api;
using Matter2Mqtt.Bridge;
using Matter2Mqtt.Devices;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Matter2Mqtt.Test.Api;

public class ApiEndpointsTests : TestKit, IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public ApiEndpointsTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Get_devices_requires_api_key()
    {
        // A key is configured, but the request omits it -> 401 (rejected before the gateway).
        var client = _factory
            .WithWebHostBuilder(b => b.UseSetting("Rest:ApiKey", "secret"))
            .CreateClient();

        var resp = await client.GetAsync("/api/devices");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Get_devices_returns_gateway_list_with_valid_key()
    {
        var probe = CreateTestProbe();
        var client = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.AddSingleton(new GatewayRef(probe.Ref));
                s.AddSingleton<IConfigureApiKey>(new StaticApiKey("secret"));
            })).CreateClient();

        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");
        var task = client.GetFromJsonAsync<List<DeviceDescriptor>>("/api/devices");

        probe.ExpectMsg<GetDevices>();
        probe.Reply((IReadOnlyList<DeviceDescriptor>)new List<DeviceDescriptor>());
        Assert.NotNull(await task);
    }

    [Fact]
    public async Task Patch_device_forwards_SetDevice()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGateway(probe.Ref);

        var task = client.PatchAsJsonAsync("/api/devices/lamp", new Dictionary<string, string> { ["state"] = "OFF" });

        var msg = probe.ExpectMsg<SetDevice>();
        Assert.Equal("lamp", msg.FriendlyName);
        Assert.Equal("OFF", msg.Payload["state"].GetString());
        Assert.Equal(HttpStatusCode.Accepted, (await task).StatusCode);
    }

    [Fact]
    public async Task Get_device_returns_state()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGateway(probe.Ref);

        var task = client.GetAsync("/api/devices/lamp");

        Assert.Equal("lamp", probe.ExpectMsg<GetDeviceState>().FriendlyName);
        probe.Reply(new DeviceStateSnapshot(true, new Dictionary<string, object?> { ["state"] = "ON" }));

        var resp = await task;
        resp.EnsureSuccessStatusCode();
        Assert.Contains("\"state\":\"ON\"", await resp.Content.ReadAsStringAsync());
    }

    private HttpClient ClientWithGateway(Akka.Actor.IActorRef gateway)
    {
        var client = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.AddSingleton(new GatewayRef(gateway));
                s.AddSingleton<IConfigureApiKey>(new StaticApiKey("secret"));
            })).CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");
        return client;
    }
}
