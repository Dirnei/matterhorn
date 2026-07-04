using System.Text;
using HiveMQtt.Client;
using HiveMQtt.MQTT5.Types;
using Microsoft.Extensions.Logging;

namespace Matterhorn.Mqtt;

/// <summary>
/// HiveMQtt-backed <see cref="IMqttPublisher"/>. Publish failures (e.g. broker temporarily
/// unreachable) are logged and dropped rather than thrown, so a down broker cannot crash the
/// device actors that call this.
/// </summary>
public sealed class HiveMqttPublisher(HiveMQClient client, ILogger<HiveMqttPublisher> logger) : IMqttPublisher
{
    public Task PublishRetained(string topic, string payload) => Send(topic, payload, retain: true);
    public Task Publish(string topic, string payload) => Send(topic, payload, retain: false);

    private async Task Send(string topic, string payload, bool retain)
    {
        try
        {
            var msg = new MQTT5PublishMessage(topic, QualityOfService.AtMostOnceDelivery)
            {
                Payload = Encoding.UTF8.GetBytes(payload),
                Retain = retain,
            };
            await client.PublishAsync(msg);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Dropping MQTT publish to {Topic} (broker unavailable?)", topic);
        }
    }
}
