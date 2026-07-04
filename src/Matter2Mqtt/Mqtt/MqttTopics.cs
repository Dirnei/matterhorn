namespace Matter2Mqtt.Mqtt;

/// <summary>Pure builders/parsers for the Z2M-shaped topic tree (spec §4).</summary>
public sealed class MqttTopics(string baseTopic)
{
    public string Base { get; } = baseTopic;
    public string BridgeState() => $"{Base}/bridge/state";
    public string BridgeInfo() => $"{Base}/bridge/info";
    public string BridgeDevices() => $"{Base}/bridge/devices";
    public string BridgeEvent() => $"{Base}/bridge/event";
    public string Device(string name) => $"{Base}/{name}";
    public string Availability(string name) => $"{Base}/{name}/availability";
    public string SetSubscription() => $"{Base}/+/set";
    public string SetAttrSubscription() => $"{Base}/+/set/+";
    public string GetSubscription() => $"{Base}/+/get";
    public string RequestSubscription() => $"{Base}/bridge/request/+";

    public bool TryParseSet(string topic, out string friendlyName, out string? attr)
    {
        friendlyName = ""; attr = null;
        if (!topic.StartsWith(Base + "/")) return false;
        var parts = topic[(Base.Length + 1)..].Split('/');
        if (parts.Length == 2 && parts[1] == "set") { friendlyName = parts[0]; return true; }
        if (parts.Length == 3 && parts[1] == "set") { friendlyName = parts[0]; attr = parts[2]; return true; }
        return false;
    }

    public bool TryParseRequest(string topic, out string action)
    {
        action = "";
        var prefix = $"{Base}/bridge/request/";
        if (!topic.StartsWith(prefix)) return false;
        action = topic[prefix.Length..];
        return action.Length > 0 && !action.Contains('/');
    }
}
