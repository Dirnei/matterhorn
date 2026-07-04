using Microsoft.Extensions.Configuration;

namespace Matterhorn.Configuration;

/// <summary>Strongly-typed service configuration.</summary>
public record MatterhornConfig(
    string ControllerWsUrl, string ControllerKind,
    string MqttHost, int MqttPort, string? MqttUser, string? MqttPassword,
    string BaseTopic, bool RestEnabled, int RestPort, string? ApiKey, string? ThreadDataset)
{
    public static MatterhornConfig FromConfiguration(IConfiguration c) => new(
        ControllerWsUrl: c["Controller:WsUrl"] ?? "ws://localhost:5580/ws",
        ControllerKind: c["Controller:Kind"] ?? "matterjs-server",
        MqttHost: c["Mqtt:Host"] ?? "localhost",
        MqttPort: int.TryParse(c["Mqtt:Port"], out var p) ? p : 1883,
        MqttUser: c["Mqtt:User"], MqttPassword: c["Mqtt:Password"],
        BaseTopic: c["Mqtt:BaseTopic"] ?? "matterhorn",
        RestEnabled: !bool.TryParse(c["Rest:Enabled"], out var re) || re,
        RestPort: int.TryParse(c["Rest:Port"], out var rp) ? rp : 8090,
        ApiKey: c["Rest:ApiKey"], ThreadDataset: c["Thread:Dataset"]);
}
