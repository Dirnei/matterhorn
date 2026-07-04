using System.Text.Json;
using Akka.Actor;
using Matter2Mqtt.Bridge;
using Matter2Mqtt.Devices;

namespace Matter2Mqtt.Api;

/// <summary>
/// The thin REST facade — a request/response view of the same actor model as MQTT (spec §8).
/// The HTTP verb carries the intent: <c>GET</c> reads a device, <c>PATCH</c> applies a partial
/// state change (the MQTT <c>/set</c> equivalent).
/// </summary>
public static class ApiEndpoints
{
    public static void Map(WebApplication app)
    {
        IActorRef Gw() => app.Services.GetRequiredService<GatewayRef>().Ref;
        var timeout = TimeSpan.FromSeconds(10);

        app.MapGet("/api/devices", async () =>
            Results.Json(await Gw().Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices(), timeout)));

        app.MapGet("/api/devices/{name}", async (string name) =>
        {
            var snap = await Gw().Ask<DeviceStateSnapshot>(new GetDeviceState(name), timeout);
            return snap.Found ? Results.Json(snap.State) : Results.NotFound();
        });

        app.MapMethods("/api/devices/{name}", ["PATCH"], (string name, Dictionary<string, JsonElement> body) =>
        {
            Gw().Tell(new SetDevice(name, body));
            return Results.Accepted();
        });

        app.MapPost("/api/commission", (CommissionBody body) =>
        {
            var tx = Guid.NewGuid().ToString("N");
            Gw().Tell(new CommissionRequest(body.Code, tx));
            return Results.Accepted($"/api/commission/{tx}");
        });

        app.MapGet("/api/bridge/info", () => Results.Json(new { service = "matter2mqtt" }));
    }
}

public record CommissionBody(string Code);
