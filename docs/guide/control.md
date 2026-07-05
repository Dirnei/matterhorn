# Controlling a device

Both surfaces are equivalent; the retained `matterhorn/<device>` topic updates either way.

```bash
# MQTT
mqttx pub -h localhost -p 16883 -t 'matterhorn/<device>/set' -m '{"state":"ON","brightness":180}'
mqttx pub -h localhost -p 16883 -t 'matterhorn/<device>/set' -m '{"hue":0,"saturation":254}'   # red

# REST — PATCH a partial state change, GET the current state
curl -X PATCH http://localhost:16090/api/devices/<device> \
  -H 'content-type: application/json' -d '{"state":"ON"}'
curl http://localhost:16090/api/devices/<device>
```

Colour is expressed the way Matter models it: `hue` and `saturation` (0–254) drive the colour
wheel, `color_temp` (mireds) is separate, and `brightness` is always its own axis.

## Home Assistant

With `HomeAssistant__Enabled=true`, Matterhorn announces every device to Home Assistant via
[MQTT Discovery](https://www.home-assistant.io/integrations/mqtt/#mqtt-discovery) — the same
mechanism Zigbee2MQTT uses. Lights (on/off, brightness, color temperature, hue/saturation)
appear as a single `light` entity, plain on/off devices as a `switch`, and contact, occupancy,
temperature, humidity, illuminance, and battery readings as `binary_sensor`/`sensor` entities.

Entities keep their identity across renames (discovery keys on the Matter node, not the friendly
name), availability follows both the bridge state and the per-device availability topic, and when
Home Assistant restarts, Matterhorn re-announces everything automatically. Removing a device
clears its retained discovery configs, so the entities disappear from HA as well.

The discovery prefix defaults to `homeassistant`; set `HomeAssistant__DiscoveryTopic` if your HA
instance uses a custom one.
