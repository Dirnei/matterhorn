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
public sealed class MatterhornController(GatewayRef gateway) : Gen.MatterhornControllerBase
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private IActorRef Gw => gateway.Ref;

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
        var payload = new Dictionary<string, JsonElement>();
        if (body.State.HasValue) payload["state"] = JsonSerializer.SerializeToElement(body.State.Value.ToString());
        if (body.Brightness.HasValue) payload["brightness"] = JsonSerializer.SerializeToElement(body.Brightness.Value);
        if (body.Color_temp.HasValue) payload["color_temp"] = JsonSerializer.SerializeToElement(body.Color_temp.Value);
        if (body.Hue.HasValue) payload["hue"] = JsonSerializer.SerializeToElement(body.Hue.Value);
        if (body.Saturation.HasValue) payload["saturation"] = JsonSerializer.SerializeToElement(body.Saturation.Value);
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
