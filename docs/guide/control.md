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
