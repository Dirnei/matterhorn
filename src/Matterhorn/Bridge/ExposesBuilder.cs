using Matterhorn.Matter;

namespace Matterhorn.Bridge;

/// <summary>Builds Z2M-style exposes from the clusters a device actually reports (spec §7).</summary>
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
            list.Add(new("numeric", "battery", Published, ValueMin: 0, ValueMax: 100, Unit: "%"));
        return list;
    }
}
