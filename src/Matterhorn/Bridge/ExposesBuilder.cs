using Matterhorn.Matter;

namespace Matterhorn.Bridge;

/// <summary>Builds Z2M-style exposes from the clusters a device actually reports.</summary>
public static class ExposesBuilder
{
    private const int Published = 1, Set = 2, Get = 4, All = 7;

    public static IReadOnlyList<ExposeEntry> Build(IReadOnlyList<uint> clusterIds, uint colorFeatures)
    {
        var list = new List<ExposeEntry>();
        var has = new HashSet<uint>(clusterIds);

        if (has.Contains(MatterClusters.OnOff))
            list.Add(new("binary", "state", All, ValueOn: "ON", ValueOff: "OFF"));
        if (has.Contains(MatterClusters.LevelControl))
            list.Add(new("numeric", "brightness", All, ValueMin: 0, ValueMax: 254));
        if (has.Contains(MatterClusters.ColorControl))
        {
            if ((colorFeatures & 0x01) != 0) // Hue/Saturation
            {
                list.Add(new("numeric", "hue", All, ValueMin: 0, ValueMax: 254));
                list.Add(new("numeric", "saturation", All, ValueMin: 0, ValueMax: 254));
            }
            if ((colorFeatures & 0x10) != 0) // Color Temperature
                list.Add(new("numeric", "color_temp", All, ValueMin: 147, ValueMax: 500, Unit: "mired"));
        }
        if (has.Contains(MatterClusters.BooleanState))
            list.Add(new("binary", "contact", Published));
        if (has.Contains(MatterClusters.OccupancySensing))
            list.Add(new("binary", "occupancy", Published));
        if (has.Contains(MatterClusters.TemperatureMeasurement))
            list.Add(new("numeric", "temperature", Published, Unit: "°C"));
        if (has.Contains(MatterClusters.RelativeHumidityMeasurement))
            list.Add(new("numeric", "humidity", Published, Unit: "%"));
        if (has.Contains(MatterClusters.IlluminanceMeasurement))
            list.Add(new("numeric", "illuminance", Published, Unit: "lx"));
        if (has.Contains(MatterClusters.PowerSource))
        {
            list.Add(new("numeric", "battery", Published, ValueMin: 0, ValueMax: 100, Unit: "%"));
            list.Add(new("binary", "battery_low", Published));
        }
        if (has.Contains(MatterClusters.PressureMeasurement))
            list.Add(new("numeric", "pressure", Published, Unit: "hPa"));
        if (has.Contains(MatterClusters.FlowMeasurement))
            list.Add(new("numeric", "flow", Published, Unit: "m³/h"));
        if (has.Contains(MatterClusters.SmokeCoAlarm))
        {
            list.Add(new("binary", "smoke", Published));
            list.Add(new("binary", "carbon_monoxide", Published));
        }
        if (has.Contains(MatterClusters.AirQuality))
            list.Add(new("enum", "air_quality", Published,
                Values: new[] { "unknown", "good", "fair", "moderate", "poor", "very_poor", "extremely_poor" }));
        if (has.Contains(MatterClusters.CarbonDioxideConcentration))
            list.Add(new("numeric", "co2", Published, Unit: "ppm"));
        if (has.Contains(MatterClusters.Pm25Concentration))
            list.Add(new("numeric", "pm25", Published, Unit: "µg/m³"));
        if (has.Contains(MatterClusters.Pm10Concentration))
            list.Add(new("numeric", "pm10", Published, Unit: "µg/m³"));
        if (has.Contains(MatterClusters.ElectricalPowerMeasurement))
        {
            list.Add(new("numeric", "power", Published, Unit: "W"));
            list.Add(new("numeric", "voltage", Published, Unit: "V"));
            list.Add(new("numeric", "current", Published, Unit: "A"));
        }
        if (has.Contains(MatterClusters.ElectricalEnergyMeasurement))
            list.Add(new("numeric", "energy", Published, Unit: "kWh"));
        if (has.Contains(MatterClusters.WindowCovering))
        {
            list.Add(new("enum", "state", Set, Values: new[] { "OPEN", "CLOSE", "STOP" }));
            list.Add(new("numeric", "position", All, ValueMin: 0, ValueMax: 100, Unit: "%"));
        }
        return list;
    }
}
