using System.Text.Json;
using Matterhorn.Devices;

namespace Matterhorn.Matter;

/// <summary>
/// Pure WS-JSON ⇄ domain codec for the python-matter-server protocol (spec §3, §13).
/// Isolated here so the wire format stays behind <see cref="IMatterController"/>.
/// </summary>
public static class MatterServerProtocol
{
    public static string StartListening(int messageId) =>
        JsonSerializer.Serialize(new { message_id = messageId.ToString(), command = "start_listening" });

    public static string DeviceCommand(int messageId, ulong nodeId, ushort endpoint, CommandSpec cmd) =>
        JsonSerializer.Serialize(new
        {
            message_id = messageId.ToString(),
            command = "device_command",
            args = new
            {
                node_id = nodeId,
                endpoint_id = endpoint,
                cluster_id = cmd.ClusterId,
                command_name = cmd.CommandName,
                payload = cmd.Payload,
            }
        });

    public static IEnumerable<MatterEvent> ParseIncoming(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("event", out var evt)) yield break;

        if (evt.GetString() == "attribute_updated" && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() == 3)
        {
            var nodeId = (ulong)data[0].GetInt64();
            var path = data[1].GetString()!.Split('/');
            var value = data[2].Clone();
            yield return new AttributeChanged(new AttributeReading(
                nodeId, ushort.Parse(path[0]), uint.Parse(path[1]), uint.Parse(path[2]), value));
        }
        // node_added / node_removed parsing added when wiring the live server (spec §13).
    }
}
