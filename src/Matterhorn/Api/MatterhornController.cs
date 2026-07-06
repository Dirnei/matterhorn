using System.Text.Json;
using Akka.Actor;
using Matterhorn.Bridge;
using Matterhorn.Devices;
using Microsoft.AspNetCore.Mvc;
using Gen = Matterhorn.Api.Generated;

namespace Matterhorn.Api;

/// <summary>
/// Implements the OpenAPI contract (generated abstract base) against the actor model, mapping
/// the internal domain records to the generated wire DTOs. The contract YAML is the source of
/// truth; this class is the only place domain ⇄ contract mapping lives.
/// </summary>
[ApiController]
public sealed class MatterhornController(GatewayRef gateway, Matterhorn.Groups.GroupsRef groups, Matterhorn.Scenes.ScenesRef scenes) : Gen.MatterhornControllerBase
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private IActorRef Gw => gateway.Ref;
    private IActorRef Grp => groups.Ref;
    private IActorRef Scn => scenes.Ref;

    public override async Task<ActionResult<ICollection<Gen.Device>>> ListDevices()
    {
        var devices = await Gw.Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices(), Timeout);
        return devices.Select(ToDto).ToList();
    }

    public override async Task<ActionResult<Gen.DeviceState>> GetDeviceState(string name)
    {
        var snap = await Gw.Ask<DeviceStateSnapshot>(new Bridge.GetDeviceState(name), Timeout);
        if (!snap.Found || snap.State is null) return NotFound();
        return new Gen.DeviceState { AdditionalProperties = snap.State.ToDictionary(kv => kv.Key, kv => kv.Value!) };
    }

    public override Task<IActionResult> SetDeviceState(string name, Gen.SetRequest body)
    {
        var payload = SetRequestToPayload(body);
        if (payload.Count > 0) Gw.Tell(new SetDevice(name, payload));
        return Task.FromResult<IActionResult>(Accepted());
    }

    public override async Task<IActionResult> RenameDevice(string name, Gen.RenameRequest body)
    {
        var tx = Guid.NewGuid().ToString("N");
        var result = await Gw.Ask<Bridge.RenameResult>(new Bridge.RenameRequest(name, body.To, tx), Timeout);
        return result switch
        {
            { Ok: true } => Ok(),
            { Error: "not_found" } => NotFound(),
            { Error: "name_taken" } => Conflict(),
            _ => BadRequest(),
        };
    }

    public override async Task<IActionResult> RemoveDevice(string name)
    {
        var tx = Guid.NewGuid().ToString("N");
        var result = await Gw.Ask<Bridge.RemoveAccepted>(new Bridge.RemoveRequest(name, tx), Timeout);
        return result.Found ? Accepted() : NotFound();
    }

    public override Task<ActionResult<Gen.CommissionAccepted>> Commission(Gen.CommissionRequest body)
    {
        var tx = Guid.NewGuid().ToString("N");
        Gw.Tell(new Bridge.CommissionRequest(body.Code, tx));
        return Task.FromResult<ActionResult<Gen.CommissionAccepted>>(
            Accepted((string?)null, new Gen.CommissionAccepted { Transaction = tx }));
    }

    public override Task<ActionResult<Gen.BridgeInfo>> GetBridgeInfo() =>
        Task.FromResult<ActionResult<Gen.BridgeInfo>>(new Gen.BridgeInfo { Service = "matterhorn" });

    public override async Task<ActionResult<ICollection<Gen.Group>>> ListGroups()
    {
        var views = await Grp.Ask<IReadOnlyList<Matterhorn.Groups.GroupView>>(new Matterhorn.Groups.GetGroups(), Timeout);
        return views.Select(v => new Gen.Group { Friendly_name = v.FriendlyName, Members = v.Members.ToList() }).ToList();
    }

    public override async Task<IActionResult> PutGroup(string name, Gen.GroupMembers body)
    {
        var tx = Guid.NewGuid().ToString("N");
        var members = body?.Members?.ToList() ?? new List<string>();
        var result = await Grp.Ask<Matterhorn.Groups.GroupOpResult>(new Matterhorn.Groups.CreateGroup(name, members, tx), Timeout);
        return result switch
        {
            { Ok: true } => Created($"/api/groups/{result.Name}", null),
            { Error: "collides_with_device" } => Conflict(),
            _ => BadRequest(),
        };
    }

    public override Task<IActionResult> SetGroupState(string name, Gen.SetRequest body)
    {
        var payload = SetRequestToPayload(body);
        if (payload.Count > 0) Grp.Tell(new Matterhorn.Groups.GroupSet(name, payload));
        return Task.FromResult<IActionResult>(Accepted());
    }

    public override async Task<IActionResult> DeleteGroup(string name)
    {
        var tx = Guid.NewGuid().ToString("N");
        var result = await Grp.Ask<Matterhorn.Groups.GroupOpResult>(new Matterhorn.Groups.DeleteGroup(name, tx), Timeout);
        return result.Ok ? Accepted() : NotFound();
    }

    public override async Task<IActionResult> AddGroupMember(string name, string device)
    {
        var tx = Guid.NewGuid().ToString("N");
        var result = await Grp.Ask<Matterhorn.Groups.GroupOpResult>(new Matterhorn.Groups.AddGroupMember(name, device, tx), Timeout);
        return result.Ok ? Ok() : NotFound();
    }

    public override async Task<IActionResult> RemoveGroupMember(string name, string device)
    {
        var tx = Guid.NewGuid().ToString("N");
        var result = await Grp.Ask<Matterhorn.Groups.GroupOpResult>(new Matterhorn.Groups.RemoveGroupMember(name, device, tx), Timeout);
        return result.Ok ? Ok() : NotFound();
    }

    public override async Task<IActionResult> RenameGroup(string name, Gen.RenameRequest body)
    {
        var tx = Guid.NewGuid().ToString("N");
        var result = await Grp.Ask<Matterhorn.Groups.GroupOpResult>(new Matterhorn.Groups.RenameGroup(name, body.To, tx), Timeout);
        return result switch
        {
            { Ok: true } => Ok(),
            { Error: "not_found" } => NotFound(),
            { Error: "name_taken" } or { Error: "collides_with_device" } => Conflict(),
            _ => BadRequest(),
        };
    }

    public override async Task<ActionResult<ICollection<Gen.Scene>>> ListScenes()
    {
        var views = await Scn.Ask<IReadOnlyList<Matterhorn.Scenes.SceneView>>(new Matterhorn.Scenes.GetScenes(), Timeout);
        return views.Select(v => new Gen.Scene { Friendly_name = v.FriendlyName, Members = v.Members.ToList() }).ToList();
    }

    public override async Task<IActionResult> PutScene(string name, Gen.StoreSceneRequest body)
    {
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? explicitState = null;
        if (body?.State is { Count: > 0 })
            explicitState = body.State.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyDictionary<string, JsonElement>)JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    JsonSerializer.Serialize(kv.Value))!);
        var devices = body?.Devices?.ToList() ?? new List<string>();
        var tx = Guid.NewGuid().ToString("N");
        var r = await Scn.Ask<Matterhorn.Scenes.SceneOpResult>(
            new Matterhorn.Scenes.StoreScene(name, devices, explicitState, tx), Timeout);
        return r.Ok ? Created($"/api/scenes/{r.Name}", null) : BadRequest();
    }

    public override async Task<IActionResult> DeleteScene(string name)
    {
        var tx = Guid.NewGuid().ToString("N");
        var r = await Scn.Ask<Matterhorn.Scenes.SceneOpResult>(new Matterhorn.Scenes.DeleteScene(name, tx), Timeout);
        return r.Ok ? Accepted() : NotFound();
    }

    public override async Task<IActionResult> RecallScene(string name)
    {
        var tx = Guid.NewGuid().ToString("N");
        var r = await Scn.Ask<Matterhorn.Scenes.SceneOpResult>(new Matterhorn.Scenes.RecallSceneByName(name, tx), Timeout);
        return r.Ok ? Accepted() : NotFound();
    }

    public override async Task<IActionResult> RenameScene(string name, Gen.RenameRequest body)
    {
        var tx = Guid.NewGuid().ToString("N");
        var r = await Scn.Ask<Matterhorn.Scenes.SceneOpResult>(new Matterhorn.Scenes.RenameScene(name, body.To, tx), Timeout);
        return r switch
        {
            { Ok: true } => Ok(),
            { Error: "not_found" } => NotFound(),
            { Error: "name_taken" } => Conflict(),
            _ => BadRequest(),
        };
    }

    private static Dictionary<string, JsonElement> SetRequestToPayload(Gen.SetRequest body)
    {
        var payload = new Dictionary<string, JsonElement>();
        if (body.State.HasValue) payload["state"] = JsonSerializer.SerializeToElement(body.State.Value.ToString());
        if (body.Brightness.HasValue) payload["brightness"] = JsonSerializer.SerializeToElement(body.Brightness.Value);
        if (body.Color_temp.HasValue) payload["color_temp"] = JsonSerializer.SerializeToElement(body.Color_temp.Value);
        if (body.Hue.HasValue) payload["hue"] = JsonSerializer.SerializeToElement(body.Hue.Value);
        if (body.Saturation.HasValue) payload["saturation"] = JsonSerializer.SerializeToElement(body.Saturation.Value);
        return payload;
    }

    private static Gen.Device ToDto(DeviceDescriptor d) => new()
    {
        Friendly_name = d.FriendlyName,
        Node_id = d.NodeId,
        Endpoint = d.Endpoint,
        Vendor_name = d.VendorName,
        Product_name = d.ProductName,
        Vendor_id = d.VendorId,
        Product_id = d.ProductId,
        Device_type = d.DeviceType,
        Reachable = d.Reachable,
        Transport = d.Transport,
        Exposes = d.Exposes.Select(ToDto).ToList(),
    };

    private static Gen.Expose ToDto(ExposeEntry e) => new()
    {
        Type = e.Type,
        Property = e.Property,
        Access = e.Access,
        Value_on = e.ValueOn,
        Value_off = e.ValueOff,
        Value_min = e.ValueMin,
        Value_max = e.ValueMax,
        Unit = e.Unit,
    };
}
