using System.Text.Json;
using Matter2Mqtt.Bridge;
using Matter2Mqtt.Devices;
using Matter2Mqtt.Matter;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Matter2Mqtt.Dev;

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

        var bulb = new EndpointInfo(1, 1, "Nanoleaf", "Essentials Bulb", 4442, 3, "OnOffDimmableLight", true,
            new[] { MatterClusters.OnOff, MatterClusters.LevelControl, MatterClusters.ColorControl });
        var sensor = new EndpointInfo(2, 1, "Aqara", "Motion Sensor", 4447, 42, "OccupancySensor", true,
            new[]
            {
                MatterClusters.OccupancySensing, MatterClusters.TemperatureMeasurement,
                MatterClusters.RelativeHumidityMeasurement, MatterClusters.PowerSource,
            });

        fake.Emit(new NodeAdded(bulb));
        fake.Emit(new NodeAdded(sensor));

        fake.Emit(new AttributeChanged(new AttributeReading(1, 1, MatterClusters.OnOff, 0, J("true"))));
        fake.Emit(new AttributeChanged(new AttributeReading(1, 1, MatterClusters.LevelControl, 0, J("200"))));
        fake.Emit(new AttributeChanged(new AttributeReading(1, 1, MatterClusters.ColorControl, 7, J("370"))));
        fake.Emit(new AttributeChanged(new AttributeReading(2, 1, MatterClusters.OccupancySensing, 0, J("0"))));
        fake.Emit(new AttributeChanged(new AttributeReading(2, 1, MatterClusters.PowerSource, 12, J("184"))));        // 92%
        fake.Emit(new AttributeChanged(new AttributeReading(2, 1, MatterClusters.RelativeHumidityMeasurement, 0, J("4750")))); // 47.5%
        logger.LogInformation("Seeded demo devices: essentials_bulb_1_1, motion_sensor_2_1");

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
