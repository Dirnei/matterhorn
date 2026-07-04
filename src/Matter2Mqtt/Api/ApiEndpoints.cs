using System.Text.Json;
using Akka.Actor;
using Matter2Mqtt.Bridge;

namespace Matter2Mqtt.Api;

/// <summary>
/// The thin REST facade — a request/response view of the same actor model as MQTT (spec §8).
/// </summary>
public static class ApiEndpoints
{
    public static void Map(WebApplication app)
    {
        IActorRef Gw() => app.Services.GetRequiredService<GatewayRef>().Ref;
        var timeout = TimeSpan.FromSeconds(10);

        app.MapGet("/api/devices", async () =>
            Results.Json(await Gw().Ask<IReadOnlyList<DeviceDescriptor>>(new GetDevices(), timeout)));

        app.MapPost("/api/devices/{name}/set", (string name, Dictionary<string, JsonElement> body) =>
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
