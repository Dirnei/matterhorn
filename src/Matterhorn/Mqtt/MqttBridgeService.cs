using Akka.Actor;
using Akka.Hosting;
using HiveMQtt.Client;
using HiveMQtt.MQTT5.Types;
using Matterhorn.Bridge;
using Matterhorn.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Matterhorn.Mqtt;

/// <summary>
/// Owns the broker connection: connects (retrying), subscribes to the inbound command topics,
/// routes incoming messages to the gateway, and — on every (re)connect — asks the gateway to
/// re-announce its retained state so a broker restart never leaves stale/absent retained topics.
/// </summary>
public sealed class MqttBridgeService(
    HiveMQClient client, MqttTopics topics, MatterhornConfig cfg, ActorRegistry registry, ILogger<MqttBridgeService> logger)
    : BackgroundService
{
    private readonly string? _haStatusTopic = cfg.HaEnabled ? $"{cfg.HaDiscoveryTopic}/status" : null;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var gateway = await registry.GetAsync<MatterGatewayActor>(ct);

        client.OnMessageReceived += (_, e) =>
        {
            try { MqttCommandRouter.Route(topics, e.PublishMessage.Topic ?? "", e.PublishMessage.PayloadAsString ?? "", gateway, _haStatusTopic); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to route inbound {Topic}", e.PublishMessage.Topic); }
        };
        client.AfterConnect += async (_, _) =>
        {
            await SubscribeInbound();
            gateway.Tell(new MqttConnected());
        };

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!client.IsConnected()) await client.ConnectAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MQTT connect to {Host} failed; retrying", topics.Base);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SubscribeInbound()
    {
        await client.SubscribeAsync(topics.SetSubscription(), QualityOfService.AtLeastOnceDelivery);
        await client.SubscribeAsync(topics.SetAttrSubscription(), QualityOfService.AtLeastOnceDelivery);
        await client.SubscribeAsync(topics.RequestSubscription(), QualityOfService.AtLeastOnceDelivery);
        if (_haStatusTopic is not null)
            await client.SubscribeAsync(_haStatusTopic, QualityOfService.AtLeastOnceDelivery);
        logger.LogInformation("Subscribed to inbound MQTT command topics under {Base}", topics.Base);
    }
}
