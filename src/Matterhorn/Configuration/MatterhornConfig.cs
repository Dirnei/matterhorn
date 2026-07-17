using Microsoft.Extensions.Configuration;

namespace Matterhorn.Configuration;

/// <summary>Strongly-typed service configuration.</summary>
public record MatterhornConfig(
    string ControllerWsUrl, string ControllerKind,
    string MqttHost, int MqttPort, string? MqttUser, string? MqttPassword,
    string BaseTopic, bool RestEnabled, int RestPort, string? ApiKey,
    string? ThreadDataset, string? ThreadOtbrUrl,
    string NamesFile, string GroupsFile, string ScenesFile)
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
        ApiKey: c["Rest:ApiKey"],
        // Thread onboarding credentials. Normally leave Dataset unset and point OtbrUrl at your
        // border router (e.g. http://192.168.0.233:8080) — the dataset is then read from it.
        // Dataset is the escape hatch for a border router with no reachable REST API.
        ThreadDataset: c["Thread:Dataset"], ThreadOtbrUrl: c["Thread:OtbrUrl"],
        NamesFile: c["Storage:NamesFile"] ?? "data/names.json",
        GroupsFile: c["Storage:GroupsFile"] ?? "data/groups.json",
        ScenesFile: c["Storage:ScenesFile"] ?? "data/scenes.json");
}
