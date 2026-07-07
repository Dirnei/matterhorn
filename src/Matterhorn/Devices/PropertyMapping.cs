using System.Text.Json;
using Matterhorn.Matter;

namespace Matterhorn.Devices;

/// <summary>
/// Read path: turns Matter cluster/attribute readings into Z2M-shaped semantic
/// properties. Table-driven; unmappable readings are skipped.
/// </summary>
public static class PropertyMapping
{
    private sealed record Rule(uint Cluster, uint Attr, string Property, Func<JsonElement, object?> Convert);

    private static readonly Rule[] Rules =
    [
        new(MatterClusters.OnOff, 0, "state", v => v.GetBoolean() ? "ON" : "OFF"),
        new(MatterClusters.LevelControl, 0, "brightness", v => v.GetInt32()),
        new(MatterClusters.ColorControl, 7, "color_temp", v => v.GetInt32()),
        new(MatterClusters.ColorControl, 0, "hue", v => v.GetInt32()),
        new(MatterClusters.ColorControl, 1, "saturation", v => v.GetInt32()),
        new(MatterClusters.ColorControl, 8, "color_mode",
            v => v.GetInt32() switch { 0 => "hs", 1 => "xy", 2 => "ct", _ => "unknown" }),
        new(MatterClusters.BooleanState, 0, "contact", v => v.GetBoolean()),
        new(MatterClusters.OccupancySensing, 0, "occupancy", v => v.GetInt32() != 0),
        new(MatterClusters.TemperatureMeasurement, 0, "temperature", v => v.GetInt32() / 100.0),
        new(MatterClusters.RelativeHumidityMeasurement, 0, "humidity", v => v.GetInt32() / 100.0),
        new(MatterClusters.IlluminanceMeasurement, 0, "illuminance", v => Math.Pow(10, (v.GetInt32() - 1) / 10000.0)),
        new(MatterClusters.PowerSource, 12, "battery", v => v.GetInt32() / 2),
        new(MatterClusters.PressureMeasurement, 0, "pressure", v => v.GetInt32() / 10.0),
        new(MatterClusters.FlowMeasurement, 0, "flow", v => v.GetInt32() / 10.0),
        new(MatterClusters.SmokeCoAlarm, 1, "smoke", v => v.GetInt32() != 0),
        new(MatterClusters.SmokeCoAlarm, 2, "carbon_monoxide", v => v.GetInt32() != 0),
        new(MatterClusters.AirQuality, 0, "air_quality",
            v => v.GetInt32() switch { 0 => "unknown", 1 => "good", 2 => "fair", 3 => "moderate", 4 => "poor", 5 => "very_poor", 6 => "extremely_poor", _ => "unknown" }),
        new(MatterClusters.CarbonDioxideConcentration, 0, "co2", v => v.GetDouble()),
        new(MatterClusters.Pm25Concentration, 0, "pm25", v => v.GetDouble()),
        new(MatterClusters.Pm10Concentration, 0, "pm10", v => v.GetDouble()),
        new(MatterClusters.ElectricalPowerMeasurement, 8, "power", v => v.GetInt64() / 1000.0),
        new(MatterClusters.ElectricalPowerMeasurement, 4, "voltage", v => v.GetInt64() / 1000.0),
        new(MatterClusters.ElectricalPowerMeasurement, 5, "current", v => v.GetInt64() / 1000.0),
        new(MatterClusters.ElectricalEnergyMeasurement, 1, "energy", v => v.GetInt64() / 1_000_000.0),
        new(MatterClusters.PowerSource, 14, "battery_low", v => v.GetInt32() != 0),
    ];

    public static IReadOnlyDictionary<string, object?> Map(IEnumerable<AttributeReading> readings)
    {
        var result = new Dictionary<string, object?>();
        foreach (var r in readings)
        {
            var rule = Array.Find(Rules, x => x.Cluster == r.ClusterId && x.Attr == r.AttributeId);
            if (rule is null) continue;
            result[rule.Property] = rule.Convert(r.Value);
        }
        return result;
    }

    public static bool IsMappable(uint clusterId, uint attributeId) =>
        Array.Exists(Rules, x => x.Cluster == clusterId && x.Attr == attributeId);

    public static IEnumerable<string> KnownProperties => Rules.Select(r => r.Property);
}
