using System.Text.Json;
using Matterhorn.Bridge;
using Matterhorn.Devices;
using Matterhorn.Matter;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Matterhorn.Dev;

/// <summary>
/// Dev-only (<c>DevSeed=true</c>): emits a couple of demo devices through the fake controller so
/// the MQTT/REST surface has real content to interact with, then nudges the sensor periodically
/// so retained state visibly changes. No-op unless the configured controller is the fake one.
/// </summary>
public sealed class DemoDeviceSeeder(IMatterController controller, ILogger<DemoDeviceSeeder> logger) : BackgroundService
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (controller is not FakeMatterController fake)
        {
            logger.LogWarning("DevSeed enabled but controller is not the fake controller; skipping seed");
            return;
        }

        // Reflect commands back as attribute changes so /set (MQTT or REST) visibly updates state.
        fake.EchoCommandsAsAttributes = true;

        var bulb = new EndpointInfo(1, 1, "Nanoleaf", "Essentials Bulb", 4442, 3, "Extended Color Light", true,
            new[] { MatterClusters.OnOff, MatterClusters.LevelControl, MatterClusters.ColorControl },
            "wifi", 0x1D);   // HS + XY + CT
        var sensor = new EndpointInfo(2, 1, "Aqara", "Motion Sensor", 4447, 42, "OccupancySensor", true,
            new[]
            {
                MatterClusters.OccupancySensing, MatterClusters.TemperatureMeasurement,
                MatterClusters.RelativeHumidityMeasurement, MatterClusters.PowerSource,
            }, "thread", 0);
        var cover = new EndpointInfo(3, 1, "IKEA", "FYRTUR Roller Blind", 4476, 8194, "WindowCovering", true,
            new[] { MatterClusters.WindowCovering }, "zigbee", 0);
        var doorLock = new EndpointInfo(4, 1, "Aqara", "Smart Lock U100", 4447, 16961, "DoorLock", true,
            new[] { MatterClusters.DoorLock }, "thread", 0);
        var thermostat = new EndpointInfo(5, 1, "Ecobee", "Smart Thermostat", 4633, 512, "Thermostat", true,
            new[] { MatterClusters.Thermostat }, "wifi", 0);
        var fan = new EndpointInfo(6, 1, "Dreo", "Tower Fan", 4991, 64, "FanControl", true,
            new[] { MatterClusters.FanControl }, "wifi", 0);
        var airSensor = new EndpointInfo(7, 1, "Bosch", "Air Quality Monitor", 4886, 128, "AirQualitySensor", true,
            new[]
            {
                MatterClusters.AirQuality, MatterClusters.SmokeCoAlarm, MatterClusters.PressureMeasurement,
                MatterClusters.ElectricalPowerMeasurement, MatterClusters.ElectricalEnergyMeasurement,
            }, "wifi", 0);

        fake.Emit(new NodeAdded(bulb));
        fake.Emit(new NodeAdded(sensor));
        fake.Emit(new NodeAdded(cover));
        fake.Emit(new NodeAdded(doorLock));
        fake.Emit(new NodeAdded(thermostat));
        fake.Emit(new NodeAdded(fan));
        fake.Emit(new NodeAdded(airSensor));

        fake.Emit(new AttributeChanged(new AttributeReading(1, 1, MatterClusters.OnOff, 0, J("true"))));
        fake.Emit(new AttributeChanged(new AttributeReading(1, 1, MatterClusters.LevelControl, 0, J("200"))));
        fake.Emit(new AttributeChanged(new AttributeReading(1, 1, MatterClusters.ColorControl, 7, J("370"))));
        fake.Emit(new AttributeChanged(new AttributeReading(1, 1, MatterClusters.ColorControl, 0, J("40"))));    // hue
        fake.Emit(new AttributeChanged(new AttributeReading(1, 1, MatterClusters.ColorControl, 1, J("180"))));   // saturation
        fake.Emit(new AttributeChanged(new AttributeReading(1, 1, MatterClusters.ColorControl, 8, J("0"))));     // color_mode = hs
        fake.Emit(new AttributeChanged(new AttributeReading(2, 1, MatterClusters.OccupancySensing, 0, J("0"))));
        fake.Emit(new AttributeChanged(new AttributeReading(2, 1, MatterClusters.PowerSource, 12, J("184"))));        // 92%
        fake.Emit(new AttributeChanged(new AttributeReading(2, 1, MatterClusters.RelativeHumidityMeasurement, 0, J("4750")))); // 47.5%

        fake.Emit(new AttributeChanged(new AttributeReading(3, 1, MatterClusters.WindowCovering, 8, J("40"))));   // position -> 60%

        fake.Emit(new AttributeChanged(new AttributeReading(4, 1, MatterClusters.DoorLock, 0, J("1"))));          // LockState -> LOCK

        fake.Emit(new AttributeChanged(new AttributeReading(5, 1, MatterClusters.Thermostat, 0x00, J("2150"))));  // local_temperature -> 21.5°C
        fake.Emit(new AttributeChanged(new AttributeReading(5, 1, MatterClusters.Thermostat, 0x12, J("2200"))));  // occupied_heating_setpoint -> 22.0°C
        fake.Emit(new AttributeChanged(new AttributeReading(5, 1, MatterClusters.Thermostat, 0x1C, J("4"))));     // system_mode -> heat

        fake.Emit(new AttributeChanged(new AttributeReading(6, 1, MatterClusters.FanControl, 0, J("2"))));        // fan_mode -> medium
        fake.Emit(new AttributeChanged(new AttributeReading(6, 1, MatterClusters.FanControl, 6, J("55"))));       // percent -> 55%

        fake.Emit(new AttributeChanged(new AttributeReading(7, 1, MatterClusters.AirQuality, 0, J("1"))));                    // air_quality -> good
        fake.Emit(new AttributeChanged(new AttributeReading(7, 1, MatterClusters.SmokeCoAlarm, 1, J("0"))));                  // smoke -> false
        fake.Emit(new AttributeChanged(new AttributeReading(7, 1, MatterClusters.SmokeCoAlarm, 2, J("0"))));                  // carbon_monoxide -> false
        fake.Emit(new AttributeChanged(new AttributeReading(7, 1, MatterClusters.PressureMeasurement, 0, J("10130"))));       // pressure -> 1013.0 hPa
        fake.Emit(new AttributeChanged(new AttributeReading(7, 1, MatterClusters.ElectricalPowerMeasurement, 8, J("45000")))); // power -> 45.0 W
        fake.Emit(new AttributeChanged(new AttributeReading(7, 1, MatterClusters.ElectricalEnergyMeasurement, 1, J("12500000")))); // energy -> 12.5 kWh

        logger.LogInformation(
            "Seeded demo devices: essentials_bulb_1_1, motion_sensor_2_1, fyrtur_roller_blind_3_1, " +
            "smart_lock_u100_4_1, smart_thermostat_5_1, tower_fan_6_1, air_quality_monitor_7_1");

        var rnd = new Random();
        var occupied = false;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
            catch (OperationCanceledException) { break; }

            var centiCelsius = 2000 + rnd.Next(0, 500); // 20.00–24.99 °C
            occupied = !occupied;
            fake.Emit(new AttributeChanged(new AttributeReading(2, 1, MatterClusters.TemperatureMeasurement, 0, J(centiCelsius.ToString()))));
            fake.Emit(new AttributeChanged(new AttributeReading(2, 1, MatterClusters.OccupancySensing, 0, J(occupied ? "1" : "0"))));
        }
    }
}
