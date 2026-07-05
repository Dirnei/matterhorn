using System.Text.Json;
using Akka.Actor;
using Matterhorn.Bridge;

namespace Matterhorn.Mqtt;

/// <summary>
/// Translates an inbound MQTT command message into the matching gateway message and forwards
/// it — the write-path mirror of the REST facade, so control is identical over MQTT and REST.
/// Pure and side-effect-free apart from the <c>Tell</c>, so it is unit-tested
/// against a probe gateway without a broker.
/// </summary>
public static class MqttCommandRouter
{
    public static void Route(MqttTopics topics, string topic, string payload, CommandTargets targets)
    {
        if (topics.TryParseSet(topic, out var name, out var attr))
        {
            var body = attr is null ? ParseObject(payload) : SingleAttr(attr, payload);
            if (body.Count > 0) targets.Gateway.Tell(new SetDevice(name, body), ActorRefs.NoSender);
            return;
        }

        if (topics.TryParseRequest(topic, out var action))
            RouteRequest(action, payload, targets);
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

    private static void RouteRequest(string action, string payload, CommandTargets targets)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(string.IsNullOrWhiteSpace(payload) ? "{}" : payload).RootElement.Clone(); }
        catch (JsonException) { return; }

        string Tx() => root.TryGetProperty("transaction", out var t) ? t.GetString() ?? "" : "";

        if (action.StartsWith("group/"))
        {
            RouteGroup(action["group/".Length..], root, targets.Groups);
            return;
        }

        switch (action)
        {
            case "commission":
                if (root.TryGetProperty("code", out var code))
                    targets.Gateway.Tell(new CommissionRequest(code.GetString() ?? "", Tx()), ActorRefs.NoSender);
                break;
            case "remove":
                var id = root.TryGetProperty("id", out var i) ? i.GetString()
                       : root.TryGetProperty("friendly_name", out var f) ? f.GetString() : null;
                if (!string.IsNullOrEmpty(id))
                    targets.Gateway.Tell(new RemoveRequest(id, Tx()), ActorRefs.NoSender);
                break;
            case "rename":
                var from = root.TryGetProperty("from", out var fr) ? fr.GetString() : null;
                var to = root.TryGetProperty("to", out var tr) ? tr.GetString() : null;
                if (!string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to))
                    targets.Gateway.Tell(new RenameRequest(from, to, Tx()), ActorRefs.NoSender);
                break;
        }
    }

    private static void RouteGroup(string sub, JsonElement root, ICanTell groups)
    {
        string Tx() => root.TryGetProperty("transaction", out var t) ? t.GetString() ?? "" : "";
        string? Str(string p) => root.TryGetProperty(p, out var v) ? v.GetString() : null;
        switch (sub)
        {
            case "add" when Str("friendly_name") is { } n:
                groups.Tell(new Groups.CreateGroup(n, Array.Empty<string>(), Tx()), ActorRefs.NoSender); break;
            case "remove" when (Str("id") ?? Str("friendly_name")) is { } n:
                groups.Tell(new Groups.DeleteGroup(n, Tx()), ActorRefs.NoSender); break;
            case "rename" when Str("from") is { } f && Str("to") is { } t:
                groups.Tell(new Groups.RenameGroup(f, t, Tx()), ActorRefs.NoSender); break;
            case "members/add" when Str("group") is { } g && Str("device") is { } d:
                groups.Tell(new Groups.AddGroupMember(g, d, Tx()), ActorRefs.NoSender); break;
            case "members/remove" when Str("group") is { } g && Str("device") is { } d:
                groups.Tell(new Groups.RemoveGroupMember(g, d, Tx()), ActorRefs.NoSender); break;
        }
    }
}

/// <summary>Recipients an inbound MQTT command can be routed to.</summary>
public record CommandTargets(ICanTell Gateway, ICanTell Groups, ICanTell Scenes);
