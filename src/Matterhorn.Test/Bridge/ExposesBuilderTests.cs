using Matterhorn.Bridge;
using Matterhorn.Matter;

namespace Matterhorn.Test.Bridge;

public class ExposesBuilderTests
{
    [Fact]
    public void Builds_state_and_brightness_for_dimmable_light()
    {
        var exposes = ExposesBuilder.Build(new[] { MatterClusters.OnOff, MatterClusters.LevelControl }, 0);
        Assert.Contains(exposes, e => e.Property == "state" && e.Type == "binary");
        Assert.Contains(exposes, e => e.Property == "brightness" && e.Type == "numeric");
    }

    [Fact]
    public void Sensor_exposes_are_readonly_access_1()
    {
        var exposes = ExposesBuilder.Build(new[] { MatterClusters.TemperatureMeasurement }, 0);
        var temp = Assert.Single(exposes);
        Assert.Equal("temperature", temp.Property);
        Assert.Equal(1, temp.Access); // published only
    }

    [Fact]
    public void Ignores_unsupported_cluster()
    {
        Assert.Empty(ExposesBuilder.Build(new[] { 0x9999u }, 0));
    }

    [Fact]
    public void Hs_feature_adds_hue_and_saturation()
    {
        var exposes = ExposesBuilder.Build(new[] { MatterClusters.ColorControl }, 0x01);
        Assert.Contains(exposes, e => e.Property == "hue");
        Assert.Contains(exposes, e => e.Property == "saturation");
        Assert.DoesNotContain(exposes, e => e.Property == "color_temp");
    }

    [Fact]
    public void Ct_feature_without_hs_adds_only_color_temp()
    {
        var exposes = ExposesBuilder.Build(new[] { MatterClusters.ColorControl }, 0x10);
        Assert.Contains(exposes, e => e.Property == "color_temp");
        Assert.DoesNotContain(exposes, e => e.Property == "hue");
    }

    [Fact]
    public void Pressure_cluster_exposes_read_only_pressure()
    {
        var exposes = ExposesBuilder.Build(new[] { MatterClusters.PressureMeasurement }, 0);
        var e = Assert.Single(exposes, x => x.Property == "pressure");
        Assert.Equal("numeric", e.Type);
        Assert.Equal(1, e.Access);          // Published only
        Assert.Equal("hPa", e.Unit);
    }

    [Fact]
    public void Power_measurement_exposes_power_voltage_current()
    {
        var exposes = ExposesBuilder.Build(new[] { MatterClusters.ElectricalPowerMeasurement }, 0);
        Assert.Contains(exposes, x => x.Property == "power" && x.Unit == "W");
        Assert.Contains(exposes, x => x.Property == "voltage" && x.Unit == "V");
        Assert.Contains(exposes, x => x.Property == "current" && x.Unit == "A");
    }
}
