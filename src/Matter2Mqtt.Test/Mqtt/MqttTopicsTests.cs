using Matter2Mqtt.Mqtt;

namespace Matter2Mqtt.Test.Mqtt;

public class MqttTopicsTests
{
    private readonly MqttTopics _t = new("matter2mqtt");

    [Fact]
    public void Builds_device_and_bridge_topics()
    {
        Assert.Equal("matter2mqtt/lamp", _t.Device("lamp"));
        Assert.Equal("matter2mqtt/lamp/availability", _t.Availability("lamp"));
        Assert.Equal("matter2mqtt/bridge/devices", _t.BridgeDevices());
    }

    [Fact]
    public void Parses_set_topic_without_attr()
    {
        Assert.True(_t.TryParseSet("matter2mqtt/lamp/set", out var name, out var attr));
        Assert.Equal("lamp", name);
        Assert.Null(attr);
    }

    [Fact]
    public void Parses_set_topic_with_attr()
    {
        Assert.True(_t.TryParseSet("matter2mqtt/lamp/set/brightness", out var name, out var attr));
        Assert.Equal("lamp", name);
        Assert.Equal("brightness", attr);
    }

    [Fact]
    public void Rejects_non_set_topic()
    {
        Assert.False(_t.TryParseSet("matter2mqtt/lamp", out _, out _));
    }
}
