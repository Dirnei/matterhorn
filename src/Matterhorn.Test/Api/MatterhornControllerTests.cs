using System.Net;
using System.Net.Http.Json;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Matterhorn.Api;
using Matterhorn.Bridge;
using Matterhorn.Devices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Matterhorn.Test.Api;

public class MatterhornControllerTests : TestKit, IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public MatterhornControllerTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task List_devices_requires_api_key()
    {
        var client = _factory
            .WithWebHostBuilder(b => b.UseSetting("Rest:ApiKey", "secret"))
            .CreateClient();

        var resp = await client.GetAsync("/api/devices");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task List_devices_returns_gateway_list_with_valid_key()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGateway(probe.Ref);

        var task = client.GetAsync("/api/devices");
        probe.ExpectMsg<GetDevices>();
        probe.Reply((IReadOnlyList<DeviceDescriptor>)new List<DeviceDescriptor>());

        var resp = await task;
        resp.EnsureSuccessStatusCode();
        Assert.Equal("[]", await resp.Content.ReadAsStringAsync());
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
    public async Task Get_device_returns_snake_case_state()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGateway(probe.Ref);

        var task = client.GetAsync("/api/devices/lamp");
        Assert.Equal("lamp", probe.ExpectMsg<GetDeviceState>().FriendlyName);
        probe.Reply(new DeviceStateSnapshot(true, new Dictionary<string, object?> { ["state"] = "ON", ["brightness"] = 128 }));

        var resp = await task;
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"state\":\"ON\"", body);
        Assert.Contains("\"brightness\":128", body);
    }

    [Fact]
    public async Task Get_missing_device_returns_404()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGateway(probe.Ref);

        var task = client.GetAsync("/api/devices/nope");
        probe.ExpectMsg<GetDeviceState>();
        probe.Reply(new DeviceStateSnapshot(false, null));

        Assert.Equal(HttpStatusCode.NotFound, (await task).StatusCode);
    }

    [Fact]
    public async Task Patch_device_forwards_hue_and_saturation()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGateway(probe.Ref);

        var task = client.PatchAsJsonAsync("/api/devices/lamp",
            new Dictionary<string, int> { ["hue"] = 100, ["saturation"] = 200 });

        var msg = probe.ExpectMsg<SetDevice>();
        Assert.Equal(100, msg.Payload["hue"].GetInt32());
        Assert.Equal(200, msg.Payload["saturation"].GetInt32());
        Assert.Equal(HttpStatusCode.Accepted, (await task).StatusCode);
    }

    [Fact]
    public async Task Rename_device_returns_200_on_success()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGateway(probe.Ref);

        var task = client.PostAsJsonAsync("/api/devices/bulb_5_1/rename", new Dictionary<string, string> { ["to"] = "lamp" });

        var msg = probe.ExpectMsg<RenameRequest>();
        Assert.Equal("bulb_5_1", msg.FromName);
        Assert.Equal("lamp", msg.ToName);
        probe.Reply(new RenameResult(true, null, "lamp"));

        Assert.Equal(HttpStatusCode.OK, (await task).StatusCode);
    }

    [Fact]
    public async Task Rename_missing_device_returns_404()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGateway(probe.Ref);

        var task = client.PostAsJsonAsync("/api/devices/ghost/rename", new Dictionary<string, string> { ["to"] = "lamp" });
        probe.ExpectMsg<RenameRequest>();
        probe.Reply(new RenameResult(false, "not_found"));

        Assert.Equal(HttpStatusCode.NotFound, (await task).StatusCode);
    }

    [Fact]
    public async Task Rename_conflict_returns_409()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGateway(probe.Ref);

        var task = client.PostAsJsonAsync("/api/devices/bulb_5_1/rename", new Dictionary<string, string> { ["to"] = "taken" });
        probe.ExpectMsg<RenameRequest>();
        probe.Reply(new RenameResult(false, "name_taken"));

        Assert.Equal(HttpStatusCode.Conflict, (await task).StatusCode);
    }

    [Fact]
    public async Task Rename_invalid_name_returns_400()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGateway(probe.Ref);

        var task = client.PostAsJsonAsync("/api/devices/bulb_5_1/rename", new Dictionary<string, string> { ["to"] = "!!!" });
        probe.ExpectMsg<RenameRequest>();
        probe.Reply(new RenameResult(false, "invalid_name"));

        Assert.Equal(HttpStatusCode.BadRequest, (await task).StatusCode);
    }

    private HttpClient ClientWithGateway(IActorRef gateway)
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
