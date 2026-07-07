using System.Net;
using System.Net.Http.Json;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Matterhorn.Api;
using Matterhorn.Bridge;
using Matterhorn.Devices;
using Matterhorn.Groups;
using Matterhorn.Scenes;
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
    public async Task List_devices_maps_enum_expose_values_to_dto()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGateway(probe.Ref);

        var entry = new ExposeEntry("enum", "system_mode", 7, Values: new[] { "off", "heat" });
        var descriptor = new DeviceDescriptor("thermostat", "1", 1, null, null, 0, 0, "Thermostat", true, new[] { entry }, "wifi");

        var task = client.GetAsync("/api/devices");
        probe.ExpectMsg<GetDevices>();
        probe.Reply((IReadOnlyList<DeviceDescriptor>)new List<DeviceDescriptor> { descriptor });

        var resp = await task;
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"type\":\"enum\"", body);
        Assert.Contains("\"values\":[\"off\",\"heat\"]", body);
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

    [Fact]
    public async Task Remove_device_returns_202_when_found()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGateway(probe.Ref);

        var task = client.DeleteAsync("/api/devices/bulb_5_1");

        var msg = probe.ExpectMsg<RemoveRequest>();
        Assert.Equal("bulb_5_1", msg.FriendlyName);
        probe.Reply(new RemoveAccepted(true));

        Assert.Equal(HttpStatusCode.Accepted, (await task).StatusCode);
    }

    [Fact]
    public async Task Remove_missing_device_returns_404()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGateway(probe.Ref);

        var task = client.DeleteAsync("/api/devices/ghost");
        probe.ExpectMsg<RemoveRequest>();
        probe.Reply(new RemoveAccepted(false));

        Assert.Equal(HttpStatusCode.NotFound, (await task).StatusCode);
    }

    [Fact]
    public async Task List_groups_returns_supervisor_list()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGroups(probe.Ref);

        var task = client.GetAsync("/api/groups");
        probe.ExpectMsg<GetGroups>();
        probe.Reply((IReadOnlyList<GroupView>)new List<GroupView> { new("living_room", new List<string> { "lamp" }) });

        var resp = await task;
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"friendly_name\":\"living_room\"", body);
        Assert.Contains("\"members\":[\"lamp\"]", body);
    }

    [Fact]
    public async Task PutGroup_creates_and_returns_201()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGroups(probe.Ref);

        var task = client.PutAsJsonAsync("/api/groups/living_room", new Dictionary<string, List<string>> { ["members"] = new() { "lamp" } });

        var msg = probe.ExpectMsg<CreateGroup>();
        Assert.Equal("living_room", msg.Name);
        Assert.Equal(new[] { "lamp" }, msg.MemberDevices);
        probe.Reply(new GroupOpResult(true, null, "living_room"));

        Assert.Equal(HttpStatusCode.Created, (await task).StatusCode);
    }

    [Fact]
    public async Task PutGroup_collision_returns_409()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGroups(probe.Ref);

        var task = client.PutAsJsonAsync("/api/groups/lamp", new Dictionary<string, List<string>>());
        probe.ExpectMsg<CreateGroup>();
        probe.Reply(new GroupOpResult(false, "collides_with_device"));

        Assert.Equal(HttpStatusCode.Conflict, (await task).StatusCode);
    }

    [Fact]
    public async Task PutGroup_invalid_name_returns_400()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGroups(probe.Ref);

        var task = client.PutAsJsonAsync("/api/groups/!!!", new Dictionary<string, List<string>>());
        probe.ExpectMsg<CreateGroup>();
        probe.Reply(new GroupOpResult(false, "invalid_name"));

        Assert.Equal(HttpStatusCode.BadRequest, (await task).StatusCode);
    }

    [Fact]
    public async Task Patch_group_forwards_GroupSet()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGroups(probe.Ref);

        var task = client.PatchAsJsonAsync("/api/groups/living_room", new Dictionary<string, string> { ["state"] = "ON" });

        var msg = probe.ExpectMsg<GroupSet>();
        Assert.Equal("living_room", msg.Name);
        Assert.Equal("ON", msg.Payload["state"].GetString());
        Assert.Equal(HttpStatusCode.Accepted, (await task).StatusCode);
    }

    [Fact]
    public async Task Delete_group_returns_202_when_found()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGroups(probe.Ref);

        var task = client.DeleteAsync("/api/groups/living_room");
        var msg = probe.ExpectMsg<Matterhorn.Groups.DeleteGroup>();
        Assert.Equal("living_room", msg.Name);
        probe.Reply(new GroupOpResult(true, null, "living_room"));

        Assert.Equal(HttpStatusCode.Accepted, (await task).StatusCode);
    }

    [Fact]
    public async Task Delete_missing_group_returns_404()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGroups(probe.Ref);

        var task = client.DeleteAsync("/api/groups/ghost");
        probe.ExpectMsg<Matterhorn.Groups.DeleteGroup>();
        probe.Reply(new GroupOpResult(false, "not_found"));

        Assert.Equal(HttpStatusCode.NotFound, (await task).StatusCode);
    }

    [Fact]
    public async Task Add_group_member_returns_200_when_found()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGroups(probe.Ref);

        var task = client.PutAsync("/api/groups/living_room/members/lamp", null);
        var msg = probe.ExpectMsg<AddGroupMember>();
        Assert.Equal("living_room", msg.Group);
        Assert.Equal("lamp", msg.Device);
        probe.Reply(new GroupOpResult(true, null, "living_room"));

        Assert.Equal(HttpStatusCode.OK, (await task).StatusCode);
    }

    [Fact]
    public async Task Add_group_member_returns_404_when_missing()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGroups(probe.Ref);

        var task = client.PutAsync("/api/groups/ghost/members/lamp", null);
        probe.ExpectMsg<AddGroupMember>();
        probe.Reply(new GroupOpResult(false, "not_found"));

        Assert.Equal(HttpStatusCode.NotFound, (await task).StatusCode);
    }

    [Fact]
    public async Task Remove_group_member_returns_200_when_found()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGroups(probe.Ref);

        var task = client.DeleteAsync("/api/groups/living_room/members/lamp");
        var msg = probe.ExpectMsg<RemoveGroupMember>();
        Assert.Equal("living_room", msg.Group);
        Assert.Equal("lamp", msg.Device);
        probe.Reply(new GroupOpResult(true, null, "living_room"));

        Assert.Equal(HttpStatusCode.OK, (await task).StatusCode);
    }

    [Fact]
    public async Task Remove_group_member_returns_404_when_missing()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGroups(probe.Ref);

        var task = client.DeleteAsync("/api/groups/ghost/members/lamp");
        probe.ExpectMsg<RemoveGroupMember>();
        probe.Reply(new GroupOpResult(false, "not_found"));

        Assert.Equal(HttpStatusCode.NotFound, (await task).StatusCode);
    }

    [Fact]
    public async Task Rename_group_returns_200_on_success()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGroups(probe.Ref);

        var task = client.PostAsJsonAsync("/api/groups/living_room/rename", new Dictionary<string, string> { ["to"] = "den" });
        var msg = probe.ExpectMsg<Matterhorn.Groups.RenameGroup>();
        Assert.Equal("living_room", msg.From);
        Assert.Equal("den", msg.To);
        probe.Reply(new GroupOpResult(true, null, "den"));

        Assert.Equal(HttpStatusCode.OK, (await task).StatusCode);
    }

    [Fact]
    public async Task Rename_missing_group_returns_404()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGroups(probe.Ref);

        var task = client.PostAsJsonAsync("/api/groups/ghost/rename", new Dictionary<string, string> { ["to"] = "den" });
        probe.ExpectMsg<Matterhorn.Groups.RenameGroup>();
        probe.Reply(new GroupOpResult(false, "not_found"));

        Assert.Equal(HttpStatusCode.NotFound, (await task).StatusCode);
    }

    [Fact]
    public async Task Rename_group_conflict_returns_409()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGroups(probe.Ref);

        var task = client.PostAsJsonAsync("/api/groups/living_room/rename", new Dictionary<string, string> { ["to"] = "taken" });
        probe.ExpectMsg<Matterhorn.Groups.RenameGroup>();
        probe.Reply(new GroupOpResult(false, "name_taken"));

        Assert.Equal(HttpStatusCode.Conflict, (await task).StatusCode);
    }

    [Fact]
    public async Task Rename_group_invalid_name_returns_400()
    {
        var probe = CreateTestProbe();
        var client = ClientWithGroups(probe.Ref);

        var task = client.PostAsJsonAsync("/api/groups/living_room/rename", new Dictionary<string, string> { ["to"] = "!!!" });
        probe.ExpectMsg<Matterhorn.Groups.RenameGroup>();
        probe.Reply(new GroupOpResult(false, "invalid_name"));

        Assert.Equal(HttpStatusCode.BadRequest, (await task).StatusCode);
    }

    [Fact]
    public async Task List_scenes_returns_supervisor_list()
    {
        var probe = CreateTestProbe();
        var client = ClientWithScenes(probe.Ref);

        var task = client.GetAsync("/api/scenes");
        probe.ExpectMsg<GetScenes>();
        probe.Reply((IReadOnlyList<SceneView>)new List<SceneView> { new("movie", new List<string> { "lamp" }) });

        var resp = await task;
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"friendly_name\":\"movie\"", body);
        Assert.Contains("\"members\":[\"lamp\"]", body);
    }

    [Fact]
    public async Task PutScene_creates_and_returns_201()
    {
        var probe = CreateTestProbe();
        var client = ClientWithScenes(probe.Ref);

        var task = client.PutAsJsonAsync("/api/scenes/movie", new Dictionary<string, List<string>> { ["devices"] = new() { "lamp" } });

        var msg = probe.ExpectMsg<StoreScene>();
        Assert.Equal("movie", msg.Name);
        Assert.Equal(new[] { "lamp" }, msg.Devices);
        probe.Reply(new SceneOpResult(true, null, "movie"));

        Assert.Equal(HttpStatusCode.Created, (await task).StatusCode);
    }

    [Fact]
    public async Task PutScene_invalid_name_returns_400()
    {
        var probe = CreateTestProbe();
        var client = ClientWithScenes(probe.Ref);

        var task = client.PutAsJsonAsync("/api/scenes/!!!", new Dictionary<string, List<string>>());
        probe.ExpectMsg<StoreScene>();
        probe.Reply(new SceneOpResult(false, "invalid_name"));

        Assert.Equal(HttpStatusCode.BadRequest, (await task).StatusCode);
    }

    [Fact]
    public async Task Delete_scene_returns_202_when_found()
    {
        var probe = CreateTestProbe();
        var client = ClientWithScenes(probe.Ref);

        var task = client.DeleteAsync("/api/scenes/movie");
        var msg = probe.ExpectMsg<DeleteScene>();
        Assert.Equal("movie", msg.Name);
        probe.Reply(new SceneOpResult(true, null, "movie"));

        Assert.Equal(HttpStatusCode.Accepted, (await task).StatusCode);
    }

    [Fact]
    public async Task Delete_missing_scene_returns_404()
    {
        var probe = CreateTestProbe();
        var client = ClientWithScenes(probe.Ref);

        var task = client.DeleteAsync("/api/scenes/ghost");
        probe.ExpectMsg<DeleteScene>();
        probe.Reply(new SceneOpResult(false, "not_found"));

        Assert.Equal(HttpStatusCode.NotFound, (await task).StatusCode);
    }

    [Fact]
    public async Task RecallScene_returns_202_for_known_scene()
    {
        var probe = CreateTestProbe();
        var client = ClientWithScenes(probe.Ref);

        var task = client.PostAsync("/api/scenes/movie/recall", null);
        var msg = probe.ExpectMsg<RecallSceneByName>();
        Assert.Equal("movie", msg.Name);
        probe.Reply(new SceneOpResult(true, null, "movie"));

        Assert.Equal(HttpStatusCode.Accepted, (await task).StatusCode);
    }

    [Fact]
    public async Task RecallScene_returns_404_when_missing()
    {
        var probe = CreateTestProbe();
        var client = ClientWithScenes(probe.Ref);

        var task = client.PostAsync("/api/scenes/ghost/recall", null);
        probe.ExpectMsg<RecallSceneByName>();
        probe.Reply(new SceneOpResult(false, "not_found"));

        Assert.Equal(HttpStatusCode.NotFound, (await task).StatusCode);
    }

    [Fact]
    public async Task Rename_scene_returns_200_on_success()
    {
        var probe = CreateTestProbe();
        var client = ClientWithScenes(probe.Ref);

        var task = client.PostAsJsonAsync("/api/scenes/movie/rename", new Dictionary<string, string> { ["to"] = "film" });
        var msg = probe.ExpectMsg<RenameScene>();
        Assert.Equal("movie", msg.From);
        Assert.Equal("film", msg.To);
        probe.Reply(new SceneOpResult(true, null, "film"));

        Assert.Equal(HttpStatusCode.OK, (await task).StatusCode);
    }

    [Fact]
    public async Task Rename_missing_scene_returns_404()
    {
        var probe = CreateTestProbe();
        var client = ClientWithScenes(probe.Ref);

        var task = client.PostAsJsonAsync("/api/scenes/ghost/rename", new Dictionary<string, string> { ["to"] = "film" });
        probe.ExpectMsg<RenameScene>();
        probe.Reply(new SceneOpResult(false, "not_found"));

        Assert.Equal(HttpStatusCode.NotFound, (await task).StatusCode);
    }

    [Fact]
    public async Task Rename_scene_conflict_returns_409()
    {
        var probe = CreateTestProbe();
        var client = ClientWithScenes(probe.Ref);

        var task = client.PostAsJsonAsync("/api/scenes/movie/rename", new Dictionary<string, string> { ["to"] = "taken" });
        probe.ExpectMsg<RenameScene>();
        probe.Reply(new SceneOpResult(false, "name_taken"));

        Assert.Equal(HttpStatusCode.Conflict, (await task).StatusCode);
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

    private HttpClient ClientWithGroups(IActorRef groups)
    {
        var client = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.AddSingleton(new GroupsRef(groups));
                s.AddSingleton<IConfigureApiKey>(new StaticApiKey("secret"));
            })).CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");
        return client;
    }

    private HttpClient ClientWithScenes(IActorRef scenes)
    {
        var client = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.AddSingleton(new ScenesRef(scenes));
                s.AddSingleton<IConfigureApiKey>(new StaticApiKey("secret"));
            })).CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");
        return client;
    }
}
