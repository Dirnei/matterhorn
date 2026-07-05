# Direct value entry on the dashboard

## Goal

Let users type an exact value into the web dashboard's numeric controls, not only
drag the slider. Applies to numeric setpoints and to the raw color values.

## Scope

Frontend only — a single file, `src/Matterhorn/wwwroot/index.html`. No backend,
OpenAPI contract, MQTT, or actor changes. The REST `PATCH /api/devices/<name>`
path already accepts every property involved; this change only adds a second way
to produce the same `patch()` calls the sliders already make.

## Part 1 — editable numeric readout

Affects every settable numeric control built by `control()`: `brightness`,
`color_temp`, and any future numeric setpoint. Read-only sensor rows
(`readoutRow`) are unchanged.

The read-only `<span class="readout">` beside each slider becomes an
`<input type="number">` carrying the same `min`/`max` as the slider.

Behavior:

- **Mirrored.** Dragging the slider updates the field's value; typing in the
  field moves the slider thumb (live, via `input`).
- **Commit on Enter or blur**, mirroring the slider's commit-on-`change`. The
  value is clamped to `[min, max]`, the slider is set to match, then `patch()`
  sends it. Typing alone (before commit) never patches, so we don't flood the
  controller keystroke-by-keystroke.
- **No clobber while editing.** `applyState()` writes incoming live values into
  the field's `.value` only when the field is **not focused** — the same intent
  as the slider's existing `dragging` guard, so an SSE `state` update mid-type
  doesn't overwrite what the user is typing.
- **Styled as today's readout** (monospace, tabular-nums, right-aligned, native
  spinners hidden) so the card layout is visually unchanged.

## Part 2 — raw HS color entry

Affects the color card built by `colorControl()`, shown when a device exposes
both `hue` and `saturation`.

Add two editable `<input type="number">` fields, `hue` and `saturation`, range
`0–254` each, beneath the existing color wheel.

Behavior:

- **Raw values only.** The fields hold the raw Matter hue/saturation (0–254).
  There is no RGB/hex and no conversion between color schemas — the user edits
  exactly the values the device uses.
- **Two-way with the wheel.** `applyState()` fills the fields from live
  `hue`/`saturation` (skipping a focused field, as in Part 1); committing a
  field also moves the wheel.
- **Commit on Enter or blur**, clamped to `0–254`. Editing `hue` sends
  `patch({hue})`; editing `saturation` sends `patch({saturation})`. The backend
  already turns a lone key into `MoveToHue` / `MoveToSaturation`.

## Explicitly out of scope

- **XY color.** No `x`/`y` property is exposed by the backend today
  (`PropertyMapping`, `CommandMapping`, `ExposesBuilder` map only hue/saturation
  for color). There is nothing to bind XY fields to. True XY entry would be a
  separate backend change and is not part of this work.
- Any RGB/hex color entry or color-space conversion.

## Testing

This is a static-asset UI change with no test harness for `index.html`. Verify by
running the app against the fake controller (`docker compose up --build`) and, on
a dimmable/color demo device:

1. Type a `brightness`/`color_temp` value + Enter → device state changes; slider
   and field agree.
2. Drag the slider → the field tracks it.
3. While a field is focused, trigger a live update from elsewhere → the field is
   not overwritten mid-edit.
4. Type raw `hue`/`saturation` values → the wheel and the device follow.
