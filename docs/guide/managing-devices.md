# Renaming & unpairing

Every device card has a **⋮ menu** (top-right) with two actions — **Rename** and **Unpair**.
Both are also available over REST and MQTT; the three surfaces are equivalent.

## Rename

On the dashboard, **⋮ → ✎ Rename** edits the name in place: press Enter to save, Esc to cancel.

```bash
# REST — 200 ok, 409 if the name is taken, 400 if it slugs to nothing, 404 if unknown
curl -X POST http://localhost:16090/api/devices/<device>/rename \
  -H 'content-type: application/json' -d '{"to":"living room lamp"}'

# MQTT — response on matterhorn/bridge/response/rename
mqttx pub -h localhost -p 16883 -t 'matterhorn/bridge/request/rename' \
  -m '{"from":"<device>","to":"living room lamp"}'
```

The name is slugified into a valid topic segment (`living room lamp` → `living_room_lamp`).

Renames are **persisted** and survive restarts and device re-joins — the name is keyed by the
device's Matter node id + endpoint in a small JSON file (see
[`Storage__NamesFile`](/guide/configuration)). One caveat: *re-commissioning* a device (after a
factory reset) gives it a new node id, so a custom name does not carry over — just rename it again.

## Unpair (decommission)

**⋮ → ⌫ Unpair…** asks for confirmation, then **removes the device from Matterhorn's Matter
fabric**. It is decommissioned and must be re-paired with its physical setup code to return. If the
device is also in Apple Home or Google (multi-admin), that pairing is untouched.

```bash
# REST — 202 Accepted (removal completes asynchronously), 404 if unknown
curl -X DELETE http://localhost:16090/api/devices/<device>

# MQTT — response on matterhorn/bridge/response/remove
mqttx pub -h localhost -p 16883 -t 'matterhorn/bridge/request/remove' \
  -m '{"id":"<device>"}'
```

The card disappears once the controller reports the device gone, and the device's stored name
override is pruned automatically.
