using System.Collections.Concurrent;
using Matter2Mqtt.Mqtt;

namespace Matter2Mqtt.Test.Mqtt;

/// <summary>Test double capturing published messages in order.</summary>
public sealed class InMemoryMqttPublisher : IMqttPublisher
{
    public ConcurrentQueue<(string Topic, string Payload, bool Retained)> Messages { get; } = new();

    public Task PublishRetained(string topic, string payload) { Messages.Enqueue((topic, payload, true)); return Task.CompletedTask; }
    public Task Publish(string topic, string payload) { Messages.Enqueue((topic, payload, false)); return Task.CompletedTask; }
}
