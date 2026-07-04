# Step 4 — RGB color control + transport badge

Status: design approved 2026-07-04. Follows the live commissioning / node-lifecycle work
(commit 892a9d2). Verified inputs come from a real Govee H600C on the Pi's python-matter-server.

## 1. Goal

Two device-facing additions, both driven by data the node already reports:

1. **RGB color control** — read and set a bulb's color via Matter's Hue/Saturation, exposed as
   flat `hue`/`saturation` properties and driven from a proper color-wheel picker in the dashboard.
2. **Transport badge** — show whether each device is Wi-Fi / Thread / Ethernet, read from its
   `NetworkCommissioning` cluster.

## 2. Non-goals

- No XY (CIE) color path. The bulb supports XY, but Hue/Saturation covers RGB and maps 1:1 to a
  picker; XY would add a second representation with no user benefit.
- No RGB/hex on the wire. Matter has no RGB; the contract stays Matter-shaped (see §4). RGB↔HS is a
  browser-only concern inside the picker.
- Color does **not** carry brightness. Brightness stays its own property/slider — this mirrors
  Matter, where brightness is a *separate cluster* (`LevelControl`), not part of `ColorControl`.
- No automations/scenes/effects (color loop, etc.).

## 3. Matter background (the "spec" we comply with)

`ColorControl` cluster `0x0300` carries color as parallel, feature-gated representations; the
`ColorMode` attribute says which is authoritative. All values are `uint8` 0–254 (same scale as our
existing `brightness`).

| Concern | Attr / Cmd | Id |
|---|---|---|
| Hue (read) | `CurrentHue` | `0x0000` (0) |
| Saturation (read) | `CurrentSaturation` | `0x0001` (1) |
| Active representation | `ColorMode` | `0x0008` (8): 0=HS, 1=XY, 2=CT |
| Color temp (read) | `ColorTemperatureMireds` | `0x0007` (7) — already mapped |
| Set hue+sat | `MoveToHueAndSaturation` | cmd |
| Set hue only | `MoveToHue` | cmd |
| Set sat only | `MoveToSaturation` | cmd |
| Feature support | `FeatureMap` | `0xFFFC` (65532): bit0=HS, bit3=XY, bit4=CT |

`NetworkCommissioning` cluster `0x0031` (49), `FeatureMap` `0xFFFC`: bit0=Wi-Fi, bit1=Thread,
bit2=Ethernet — on endpoint 0.

(Ground truth from the H600C: ColorControl FeatureMap=25 → HS+XY+CT, ColorMode=0 → HS active;
NetworkCommissioning FeatureMap=1 → Wi-Fi.)

## 4. Contract changes

**Exposes** (per device, feature-gated) — new numeric entries when the bulb advertises the HS
feature; `color_temp` now gated on the CT feature instead of mere `ColorControl` presence:

```
{ "type":"numeric", "property":"hue",        "access":7, "value_min":0, "value_max":254 }
{ "type":"numeric", "property":"saturation", "access":7, "value_min":0, "value_max":254 }
```

**State** stays flat, e.g.:
```
{ "state":"ON", "brightness":140, "hue":19, "saturation":58, "color_temp":301, "color_mode":"hs" }
```
`color_mode` (`"hs" | "xy" | "ct"`) is **state-only** (no expose) — it is not a control, it tells
the UI which representation is live so the stale non-active value (e.g. `color_temp` while in HS
mode) can be de-emphasized.

**Set** payloads:
- `{ "hue":H, "saturation":S }` → `MoveToHueAndSaturation`
- `{ "hue":H }` → `MoveToHue`
- `{ "saturation":S }` → `MoveToSaturation`

**DeviceDescriptor** gains `transport: "wifi" | "thread" | "ethernet" | "unknown"`.

## 5. Conversion (browser-only, picker ↔ Matter)

iro.js is HSV-native, so this is a linear scale — no RGB/color-space math:
```
// picker -> Matter
hue254 = round(iro.hsv.h * 254 / 360)     // h in 0..360
sat254 = round(iro.hsv.s * 254 / 100)     // s in 0..100
// Matter -> picker (value pinned to 100 so the wheel shows pure hue+sat; brightness is separate)
iro.hsv = { h: hue254 * 360 / 254, s: sat254 * 100 / 254, v: 100 }
```
The picker sends both `hue` and `saturation` together on change (debounced like the existing
sliders). The server does **zero** color math — pure passthrough, exactly like `brightness`.

## 6. Component changes

- **`MatterClusters`**: add `NetworkCommissioning = 0x0031`. (`ColorControl` already present.)

- **`MatterServerProtocol.ParseNode`**: while scanning endpoint 0, capture two FeatureMaps —
  `NetworkCommissioning`/`0xFFFC` → decode to a transport string; `ColorControl`/`0xFFFC` on the
  application endpoint → carry as the endpoint's color features. Transport decode priority when
  multiple bits set (rare): Thread > Wi-Fi > Ethernet > `unknown`.

- **`EndpointInfo`** (`Bridge/DeviceDescriptor.cs`): add `string Transport` and `uint ColorFeatures`
  (the ColorControl FeatureMap; 0 when absent). `DeviceDescriptor`: add `string Transport`. Give the
  new fields defaults (`Transport = "unknown"`, `ColorFeatures = 0`) so existing `EndpointInfo`
  constructions (tests, seeder) keep compiling; `ExposesBuilder`'s signature change likewise ripples
  to `ExposesBuilderTests`.

- **`PropertyMapping`**: add rules
  `ColorControl/0 → "hue" (int)`, `ColorControl/1 → "saturation" (int)`,
  `ColorControl/8 → "color_mode" (0→"hs",1→"xy",2→"ct")`.

- **`CommandMapping`**: extend `Order` and emit color commands. Combine logic: if the payload has
  both `hue` and `saturation` → one `MoveToHueAndSaturation { hue, saturation }`; if only `hue` →
  `MoveToHue { hue, direction: 0 }`; only `saturation` → `MoveToSaturation { saturation }`.
  (transitionTime omitted → server defaults 0, matching existing `MoveToLevelWithOnOff` /
  `MoveToColorTemperature`.)

- **`ExposesBuilder.Build`**: take the color FeatureMap. Under `ColorControl`: add `hue`+`saturation`
  when HS bit (0x01) set; add `color_temp` when CT bit (0x10) set. Gateway passes
  `info.ColorFeatures`.

- **`MatterGatewayActor.OnNodeAdded`**: put `info.Transport` into the built `DeviceDescriptor` and
  pass `info.ColorFeatures` to `ExposesBuilder`.

- **OpenAPI** (`contracts/matterhorn.openapi.yaml`): add `transport` to the `DeviceDescriptor`
  schema; rebuild (NSwag) and map it in `Api/MatterhornController.cs`. `color_mode` needs no
  contract change (it rides in the freeform state object).

- **`DemoDeviceSeeder`**: update the two `EndpointInfo` constructions for the new fields (bulb:
  `Transport="wifi"`, `ColorFeatures=0x1D`; sensor: `Transport="thread"`, `ColorFeatures=0`), and
  seed initial `1/768/0` (hue) and `1/768/1` (saturation) readings so the fake bulb shows color.

- **Dashboard** (`wwwroot/index.html` + vendored `wwwroot/iro.min.js`):
  - Vendor iro.js locally (served same-origin; no CDN/runtime dependency). `<script src="/iro.min.js">`.
  - When a device exposes both `hue` and `saturation`, render **one iro color picker** bound to
    them (and exclude them from the generic slider rendering). Brightness and `color_temp` remain
    sliders.
  - On picker change → `PATCH { hue, saturation }` (reuse existing `patch()` + drag-guard so live
    updates don't fight interaction).
  - On live state → set the picker from `hue`/`saturation` (value pinned 100).
  - Use `state.color_mode` to de-emphasize the non-active control (e.g. dim the `color_temp` slider
    when `color_mode==="hs"`).
  - Add a small transport pill in the card header from `device.transport`.

## 7. Data flow

**Read:** `attribute_updated (1/768/0|1|8)` → `ParseIncoming` → `AttributeChanged` → endpoint actor
`PropertyMapping` → `hue`/`saturation`/`color_mode` in state → MQTT retained + SSE → dashboard sets
the picker.

**Write:** picker change → `PATCH /api/devices/{name} {hue,saturation}` → `SetDevice` → endpoint
`ApplySet` → `CommandMapping` → `MoveToHueAndSaturation` → `InvokeCommand` → python-matter-server →
bulb → reports back via `attribute_updated` (the read path above closes the loop).

## 8. Testing (TDD)

Unit (xUnit), each red-first:
- `ParseNode`: decodes transport from NetworkCommissioning FeatureMap (wifi/thread/ethernet/unknown,
  priority); captures ColorControl FeatureMap into `ColorFeatures`.
- `PropertyMapping`: `1/768/0`→hue, `1/768/1`→saturation, `1/768/8`→color_mode string.
- `CommandMapping`: `{hue,saturation}`→ single `MoveToHueAndSaturation`; single-key → `MoveToHue` /
  `MoveToSaturation`.
- `ExposesBuilder`: HS bit → hue+saturation present; no-HS + CT bit → color_temp only, no hue/sat.
- Gateway: `DeviceDescriptor.Transport` reflects `EndpointInfo.Transport`.

Manual/live (docker against the Pi, per [[run-app-via-docker]]): commission/existing H600C shows a
Wi-Fi pill, a working color wheel that moves the real bulb, and `color_mode` reflecting HS.

## 9. Known limitation / future work

An **XY-only** bulb (advertises the XY feature but not HS) gets **no color control** under this
design — `ExposesBuilder` gates color on the HS bit, so it degrades gracefully to
brightness/color_temp only (no broken widget, just no wheel). If such a device shows up, the follow
up is an XY color path: expose `color_x`/`color_y` (or convert xy→HS for the picker) and map
`MoveToColor`. Deferred until a real XY-only device exists — HS covers every color bulb we have.

## 10. Rollout

Run via `docker compose up --build` with `.env` pointing at the Pi (`192.168.0.118:5580`). No schema
migrations, no persistence. Backward compatible: devices without HS simply omit the color exposes;
`transport` defaults to `unknown` if `NetworkCommissioning` is absent.
