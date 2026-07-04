using System.Text.Json;
using Akka.Actor;
using Matterhorn.Bridge;

namespace Matterhorn.Mqtt;

/// <summary>
/// Translates an inbound MQTT command message into the matching gateway message and forwards
/// it — the write-path mirror of the REST facade, so control is identical over MQTT and REST
/// (spec §2, §4, §6). Pure and side-effect-free apart from the <c>Tell</c>, so it is unit-tested
/// against a probe gateway without a broker.
/// </summary>
public static class MqttCommandRouter
{
    public static void Route(MqttTopics topics, string topic, string payload, ICanTell gateway)
    {
        if (topics.TryParseSet(topic, out var name, out var attr))
        {
            var body = attr is null ? ParseObject(payload) : SingleAttr(attr, payload);
            if (body.Count > 0) gateway.Tell(new SetDevice(name, body), ActorRefs.NoSender);
            return;
        }

        if (topics.TryParseRequest(topic, out var action))
            RouteRequest(action, payload, gateway);
    }

    private static IReadOnlyDictionary<string, JsonElement> ParseObject(string payload)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payload) ?? new(); }
        catch (JsonException) { return new Dictionary<string, JsonElement>(); }
    }

    // "matterhorn/lamp/set/state" with body "ON" -> { "state": "ON" }; numbers stay numbers.
    private static IReadOnlyDictionary<string, JsonElement> SingleAttr(string attr, string payload)
    {
        var trimmed = payload.Trim();
        JsonElement value;
        try { value = JsonDocument.Parse(trimmed).RootElement.Clone(); }
        catch (JsonException) { value = JsonSerializer.SerializeToElement(trimmed); }
        return new Dictionary<string, JsonElement> { [attr] = value };
    }

    private static void RouteRequest(string action, string payload, ICanTell gateway)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(string.IsNullOrWhiteSpace(payload) ? "{}" : payload).RootElement.Clone(); }
        catch (JsonException) { return; }

        string Tx() => root.TryGetProperty("transaction", out var t) ? t.GetString() ?? "" : "";
        switch (action)
        {
            case "commission":
                if (root.TryGetProperty("code", out var code))
                    gateway.Tell(new CommissionRequest(code.GetString() ?? "", Tx()), ActorRefs.NoSender);
                break;
            case "remove":
                var id = root.TryGetProperty("id", out var i) ? i.GetString()
                       : root.TryGetProperty("friendly_name", out var f) ? f.GetString() : null;
                if (!string.IsNullOrEmpty(id))
                    gateway.Tell(new RemoveRequest(id, Tx()), ActorRefs.NoSender);
                break;
            // "rename" has no gateway handler in Phase 1 (documented follow-up, spec §6).
        }
    }
}
