namespace Matter2Mqtt.Matter;

/// <summary>Matter cluster identifiers used across the mapping layer.</summary>
public static class MatterClusters
{
    public const uint OnOff = 0x0006;
    public const uint LevelControl = 0x0008;
    public const uint ColorControl = 0x0300;
    public const uint BooleanState = 0x0045;
    public const uint OccupancySensing = 0x0406;
    public const uint TemperatureMeasurement = 0x0402;
    public const uint RelativeHumidityMeasurement = 0x0405;
    public const uint IlluminanceMeasurement = 0x0400;
    public const uint PowerSource = 0x002F;
}
