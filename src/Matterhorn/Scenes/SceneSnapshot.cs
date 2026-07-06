using System.Text.Json;

namespace Matterhorn.Scenes;

/// <summary>
/// Projects a device's current retained state into the minimal, conflict-free set of settable
/// properties a scene should store so recall reproduces the look faithfully.
///
/// Two things the raw state must not be captured verbatim for:
/// <list type="bullet">
/// <item>An <b>off</b> device recalls as simply off. Storing its level/colour too would re-light it,
/// because the level command on the write path is <c>MoveToLevelWithOnOff</c> (it turns the light
/// back on), so the <c>Off</c> would be immediately undone.</item>
/// <item>A colour light reports <b>both</b> a <c>color_temp</c> and <c>hue</c>/<c>saturation</c> at
/// all times, with <c>color_mode</c> saying which is active. Storing both makes recall fire both a
/// <c>MoveToColorTemperature</c> and a <c>MoveToHueAndSaturation</c> — thrashing the colour mode and
/// leaving the wrong colour. Only the active representation is kept.</item>
/// </list>
/// Read-only properties (sensor readings, <c>color_mode</c> itself, availability) are never included.
/// </summary>
public static class SceneSnapshot
{
    public static Dictionary<string, JsonElement> Project(IReadOnlyDictionary<string, object?> state)
    {
        var result = new Dictionary<string, JsonElement>();
        var onOff = state.TryGetValue("state", out var s) ? s as string : null;

        // Off → store only the off-state; nothing that would re-light the device on recall.
        if (string.Equals(onOff, "OFF", StringComparison.OrdinalIgnoreCase))
        {
            result["state"] = El("OFF");
            return result;
        }

        if (onOff is not null) result["state"] = El(onOff);
        if (state.TryGetValue("brightness", out var b) && b is not null) result["brightness"] = El(b);

        var mode = state.TryGetValue("color_mode", out var m) ? m as string : null;
        state.TryGetValue("hue", out var hue);
        state.TryGetValue("saturation", out var sat);
        state.TryGetValue("color_temp", out var ct);
        var hasHueSat = hue is not null && sat is not null;
        var hasCt = ct is not null;

        if (string.Equals(mode, "ct", StringComparison.OrdinalIgnoreCase))
        {
            if (hasCt) result["color_temp"] = El(ct!);
        }
        else if (hasHueSat)                     // hs / xy / unspecified → prefer hue+saturation
        {
            result["hue"] = El(hue!);
            result["saturation"] = El(sat!);
        }
        else if (hasCt)                         // no usable hue/sat → fall back to colour temperature
        {
            result["color_temp"] = El(ct!);
        }

        return result;
    }

    private static JsonElement El(object value) => JsonSerializer.SerializeToElement(value);
}
