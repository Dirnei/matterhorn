using Microsoft.Extensions.Configuration;

namespace Matter2Mqtt.Configuration;

/// <summary>Strongly-typed service configuration (spec §10).</summary>
public record Matter2MqttConfig(
    string ControllerWsUrl, string ControllerKind,
    string MqttHost, int MqttPort, string? MqttUser, string? MqttPassword,
    string BaseTopic, bool RestEnabled, int RestPort, string? ApiKey, string? ThreadDataset)
{
    public static Matter2MqttConfig FromConfiguration(IConfiguration c) => new(
        ControllerWsUrl: c["Controller:WsUrl"] ?? "ws://localhost:5580/ws",
        ControllerKind: c["Controller:Kind"] ?? "python-matter-server",
        MqttHost: c["Mqtt:Host"] ?? "localhost",
        MqttPort: int.TryParse(c["Mqtt:Port"], out var p) ? p : 1883,
        MqttUser: c["Mqtt:User"], MqttPassword: c["Mqtt:Password"],
        BaseTopic: c["Mqtt:BaseTopic"] ?? "matter2mqtt",
        RestEnabled: !bool.TryParse(c["Rest:Enabled"], out var re) || re,
        RestPort: int.TryParse(c["Rest:Port"], out var rp) ? rp : 8090,
        ApiKey: c["Rest:ApiKey"], ThreadDataset: c["Thread:Dataset"]);
}
