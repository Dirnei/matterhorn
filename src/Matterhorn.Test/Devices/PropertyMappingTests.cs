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

    [Fact]
    public void Maps_hue_saturation_and_color_mode()
    {
        var props = PropertyMapping.Map(new[]
        {
            R(MatterClusters.ColorControl, 0, "19"),
            R(MatterClusters.ColorControl, 1, "58"),
            R(MatterClusters.ColorControl, 8, "0"),
        });
        Assert.Equal(19, Assert.IsType<int>(props["hue"]));
        Assert.Equal(58, Assert.IsType<int>(props["saturation"]));
        Assert.Equal("hs", props["color_mode"]);
    }

    [Fact]
    public void Maps_pressure_tenths_kpa_to_hpa()
    {
        var props = PropertyMapping.Map(new[] { R(MatterClusters.PressureMeasurement, 0, "10132") });
        Assert.Equal(1013.2, Assert.IsType<double>(props["pressure"]), 1);
    }

    [Fact]
    public void Maps_smoke_state_nonzero_to_true()
    {
        var props = PropertyMapping.Map(new[] { R(MatterClusters.SmokeCoAlarm, 1, "1") });
        Assert.True(Assert.IsType<bool>(props["smoke"]));
    }

    [Fact]
    public void Maps_active_power_milliwatts_to_watts()
    {
        var props = PropertyMapping.Map(new[] { R(MatterClusters.ElectricalPowerMeasurement, 8, "15500") });
        Assert.Equal(15.5, Assert.IsType<double>(props["power"]), 1);
    }

    [Fact]
    public void Maps_battery_charge_level_warning_to_battery_low_true()
    {
        var props = PropertyMapping.Map(new[] { R(MatterClusters.PowerSource, 14, "1") });
        Assert.True(Assert.IsType<bool>(props["battery_low"]));
    }

    [Fact]
    public void Maps_cover_lift_percent_inverted_to_position()
    {
        // Matter 30% closed-from-open -> Z2M position 70 (open-ness).
        var props = PropertyMapping.Map(new[] { R(MatterClusters.WindowCovering, 8, "30") });
        Assert.Equal(70, Assert.IsType<int>(props["position"]));
    }

    [Fact]
    public void Maps_lockstate_locked_to_state_LOCK()
    {
        var props = PropertyMapping.Map(new[] { R(MatterClusters.DoorLock, 0, "1") });
        Assert.Equal("LOCK", props["state"]);
    }

    [Fact]
    public void Maps_lockstate_unlocked_to_state_UNLOCK()
    {
        var props = PropertyMapping.Map(new[] { R(MatterClusters.DoorLock, 0, "2") });
        Assert.Equal("UNLOCK", props["state"]);
    }

    [Fact]
    public void Maps_thermostat_local_temperature_hundredths_to_celsius()
    {
        var props = PropertyMapping.Map(new[] { R(MatterClusters.Thermostat, 0, "2150") });
        Assert.Equal(21.5, Assert.IsType<double>(props["local_temperature"]), 2);
    }

    [Fact]
    public void Maps_thermostat_system_mode_heat()
    {
        var props = PropertyMapping.Map(new[] { R(MatterClusters.Thermostat, 0x1C, "4") });
        Assert.Equal("heat", props["system_mode"]);
    }
}
