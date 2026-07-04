namespace Matterhorn.Mqtt;

public interface IMqttPublisher
{
    Task PublishRetained(string topic, string payload);
    Task Publish(string topic, string payload);
}
