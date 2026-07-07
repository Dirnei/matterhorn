namespace Matterhorn.Matter;

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
    public const uint PressureMeasurement = 0x0403;
    public const uint FlowMeasurement = 0x0404;
    public const uint SmokeCoAlarm = 0x005C;
    public const uint AirQuality = 0x005B;
    public const uint CarbonDioxideConcentration = 0x040D;
    public const uint Pm25Concentration = 0x042A;
    public const uint Pm10Concentration = 0x042D;
    public const uint ElectricalPowerMeasurement = 0x0090;
    public const uint ElectricalEnergyMeasurement = 0x0091;
    public const uint WindowCovering = 0x0102;

    // Utility clusters read during node interview parsing.
    public const uint BasicInformation = 0x0028;
    public const uint Descriptor = 0x001D;
    public const uint NetworkCommissioning = 0x0031;
}
