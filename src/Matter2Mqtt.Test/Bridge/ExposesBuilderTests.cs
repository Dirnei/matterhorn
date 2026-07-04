using Matter2Mqtt.Bridge;
using Matter2Mqtt.Matter;

namespace Matter2Mqtt.Test.Bridge;

public class ExposesBuilderTests
{
    [Fact]
    public void Builds_state_and_brightness_for_dimmable_light()
    {
        var exposes = ExposesBuilder.Build(new[] { MatterClusters.OnOff, MatterClusters.LevelControl });
        Assert.Contains(exposes, e => e.Property == "state" && e.Type == "binary");
        Assert.Contains(exposes, e => e.Property == "brightness" && e.Type == "numeric");
    }

    [Fact]
    public void Sensor_exposes_are_readonly_access_1()
    {
        var exposes = ExposesBuilder.Build(new[] { MatterClusters.TemperatureMeasurement });
        var temp = Assert.Single(exposes);
        Assert.Equal("temperature", temp.Property);
        Assert.Equal(1, temp.Access); // published only
    }

    [Fact]
    public void Ignores_unsupported_cluster()
    {
        Assert.Empty(ExposesBuilder.Build(new[] { 0x9999u }));
    }
}
