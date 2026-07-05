# Home Assistant MQTT Discovery — Design

**Date:** 2026-07-05
**Status:** Approved

## Goal

Matterhorn announces its devices to Home Assistant via MQTT Discovery, the same way
Zigbee2Mqtt does: retained config payloads under a discovery prefix that make devices
appear in HA automatically, with proper entity types, availability, and device registry
metadata. The feature is opt-in.

## Configuration

Two new keys in `MatterhornConfig`:

| Key | Default | Meaning |
|---|---|---|
| `HomeAssistant:Enabled` | `false` | Master switch. When off, nothing about current behavior changes. |
| `HomeAssistant:DiscoveryTopic` | `homeassistant` | Discovery prefix HA listens on. |

## Components

### `HaDiscoveryBuilder` (new, `src/Matterhorn/HomeAssistant/`)

Pure function, analogous to `ExposesBuilder` / `MqttTopics`:

```
Build(DeviceDescriptor, MqttTopics, discoveryPrefix) -> IReadOnlyList<(string Topic, string Payload)>
```

**Exposes → HA component mapping:**

| Exposes | HA component |
|---|---|
| `state` + any of `brightness`, `color_temp`, `hue`+`saturation` | one `light` entity, `schema: json`, `supported_color_modes` derived from features |
| `state` alone | `switch` |
| `contact`, `occupancy` | `binary_sensor` with matching `device_class`, values via `value_template` from the state JSON |
| `temperature`, `humidity`, `illuminance`, `battery` | `sensor` with `device_class`, `unit_of_measurement`, `state_class: measurement` |

**Topic & identity scheme:**

- Config topic: `{prefix}/{component}/matterhorn_{nodeId}_{endpoint}/{property}/config`
- `unique_id`: `matterhorn_{nodeId}_{endpoint}_{property}`
- Deliberately keyed on NodeId/Endpoint, **not** friendly name: a rename overwrites the
  same config topics (payloads carry the new name and state topics), so no cleanup pass
  is needed and HA entity history survives renames.

**Payload contents:**

- `device` block: `identifiers: ["matterhorn_{nodeId}_{endpoint}"]`, `name` =
  friendly_name, `manufacturer`/`model` from Vendor/Product, `via_device` = the bridge.
- `origin` block: Matterhorn name + version.
- Availability (mode `all`): `{base}/bridge/state` (with `value_template` extracting
  `state`) and `{base}/{friendlyName}/availability`.
- State via `state_topic` = `{base}/{friendlyName}` with `value_template` per property
  (the `light` json schema reads the whole payload); `command_topic` =
  `{base}/{friendlyName}/set` for writable entities.

### Gateway integration (`MatterGatewayActor`)

Discovery publishing happens at the same points where `PublishDevices()` runs today:

- **Join / Rename:** publish all configs for the device, retained.
- **Remove:** publish empty payloads to the device's config topics (entities disappear
  from HA).
- **MqttConnected / AnnounceAll:** re-publish configs for all devices.

When `HomeAssistant:Enabled` is false the gateway skips all of this.

### HA restart handling (`MqttBridgeService`)

Additionally subscribe to `{discoveryPrefix}/status`. On payload `online` (HA birth
message), send a message to the gateway which re-publishes discovery configs **and**
device states, mirroring Z2M behavior.

### `CommandMapping` extension

HA's `light` json schema sends color nested — `{"color":{"h":0-360,"s":0-100}}` — while
the existing flat `hue`/`saturation` keys use Matter's 0–254 range. `CommandMapping.Map`
additionally accepts the nested `color` object and normalizes h/s to Matter ranges
(`MoveToHueAndSaturation`). `state`, `brightness` (0–254), and `color_temp` (mireds)
already match HA's json schema as-is.

## Error handling

- Discovery publishing reuses `IMqttPublisher`; failures surface the same way existing
  publishes do (logged, non-fatal).
- Unknown/unmapped expose properties are skipped silently by the builder.

## Testing

- `HaDiscoveryBuilderTests`: component mapping per device shape (light with/without
  color, switch-only, sensor combos), topic stability across renames, remove payloads.
- Gateway tests with the existing `InMemoryMqttPublisher`: configs published on join,
  cleared on remove, re-published on `MqttConnected` and on HA status `online`; nothing
  published when disabled.
- `CommandMappingTests`: nested `color` object incl. range normalization.
