using System.Text.Json;
using Matterhorn.Devices;
using Matterhorn.Matter;

namespace Matterhorn.Test.Devices;

public class PropertyMappingTests
{
    private static AttributeReading R(uint cluster, uint attr, string json) =>
        new(1, 1, cluster, attr, JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Maps_onoff_true_to_state_ON()
    {
        var props = PropertyMapping.Map(new[] { R(MatterClusters.OnOff, 0, "true") });
        Assert.Equal("ON", props["state"]);
    }

    [Fact]
    public void Maps_level_to_brightness_unchanged_range()
    {
        var props = PropertyMapping.Map(new[] { R(MatterClusters.LevelControl, 0, "128") });
        Assert.Equal(128, Assert.IsType<int>(props["brightness"]));
    }

    [Fact]
    public void Maps_temperature_hundredths_to_celsius()
    {
        var props = PropertyMapping.Map(new[] { R(MatterClusters.TemperatureMeasurement, 0, "2172") });
        Assert.Equal(21.72, Assert.IsType<double>(props["temperature"]), 3);
    }

    [Fact]
    public void Maps_illuminance_measuredvalue_to_lux()
    {
        // v = 10000*log10(lux)+1 ; for lux=100 -> v = 20001
        var props = PropertyMapping.Map(new[] { R(MatterClusters.IlluminanceMeasurement, 0, "20001") });
        Assert.Equal(100.0, Assert.IsType<double>(props["illuminance"]), 1);
    }

    [Fact]
    public void Maps_battery_halfpercent_to_percent()
    {
        var props = PropertyMapping.Map(new[] { R(MatterClusters.PowerSource, 12, "150") });
        Assert.Equal(75, Assert.IsType<int>(props["battery"]));
    }

    [Fact]
    public void Skips_unmapped_cluster()
    {
        var props = PropertyMapping.Map(new[] { R(0x9999, 0, "1") });
        Assert.Empty(props);
    }
}
