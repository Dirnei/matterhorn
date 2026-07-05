using System.Text.Json;
using Matterhorn.Matter;

namespace Matterhorn.Devices;

/// <summary>
/// Write path: maps a <c>/set</c> payload of semantic properties into ordered
/// Matter cluster commands.
/// </summary>
public static class CommandMapping
{
    // Fixed order so "on then set level" behaves predictably.
    private static readonly string[] Order = ["state", "brightness", "color_temp"];

    public static IReadOnlyList<CommandSpec> Map(IReadOnlyDictionary<string, JsonElement> setPayload)
    {
        var cmds = new List<CommandSpec>();
        foreach (var key in Order)
        {
            if (!setPayload.TryGetValue(key, out var v)) continue;
            switch (key)
            {
                case "state":
                    var s = v.GetString()?.ToUpperInvariant();
                    if (s == "ON") cmds.Add(new(MatterClusters.OnOff, "On", Empty()));
                    else if (s == "OFF") cmds.Add(new(MatterClusters.OnOff, "Off", Empty()));
                    else if (s == "TOGGLE") cmds.Add(new(MatterClusters.OnOff, "Toggle", Empty()));
                    break;
                case "brightness":
                    cmds.Add(new(MatterClusters.LevelControl, "MoveToLevelWithOnOff",
                        new Dictionary<string, object?> { ["level"] = v.GetInt32() }));
                    break;
                case "color_temp":
                    cmds.Add(new(MatterClusters.ColorControl, "MoveToColorTemperature",
                        new Dictionary<string, object?> { ["colorTemperatureMireds"] = v.GetInt32() }));
                    break;
            }
        }
        // Color: hue+sat travel together as "the color" -> one combined command; a lone key -> individual.
        var hasHue = setPayload.TryGetValue("hue", out var hue);
        var hasSat = setPayload.TryGetValue("saturation", out var sat);
        if (hasHue && hasSat)
            cmds.Add(new(MatterClusters.ColorControl, "MoveToHueAndSaturation",
                new Dictionary<string, object?> { ["hue"] = hue.GetInt32(), ["saturation"] = sat.GetInt32() }));
        else if (hasHue)
            cmds.Add(new(MatterClusters.ColorControl, "MoveToHue",
                new Dictionary<string, object?> { ["hue"] = hue.GetInt32(), ["direction"] = 0 }));
        else if (hasSat)
            cmds.Add(new(MatterClusters.ColorControl, "MoveToSaturation",
                new Dictionary<string, object?> { ["saturation"] = sat.GetInt32() }));

        // HA's json-schema light sends color nested: {"color":{"h":0-360,"s":0-100}} (long-form
        // hue/saturation keys allowed). Normalize to Matter's 0-254 ranges. Flat keys win.
        if (!hasHue && !hasSat && setPayload.TryGetValue("color", out var color)
            && color.ValueKind == JsonValueKind.Object)
        {
            double? h = color.TryGetProperty("h", out var hEl) ? hEl.GetDouble()
                      : color.TryGetProperty("hue", out var hLong) ? hLong.GetDouble() : null;
            double? s = color.TryGetProperty("s", out var sEl) ? sEl.GetDouble()
                      : color.TryGetProperty("saturation", out var sLong) ? sLong.GetDouble() : null;
            static int Scale(double value, double max) => Math.Clamp((int)Math.Round(value / max * 254), 0, 254);
            if (h is not null && s is not null)
                cmds.Add(new(MatterClusters.ColorControl, "MoveToHueAndSaturation",
                    new Dictionary<string, object?> { ["hue"] = Scale(h.Value, 360), ["saturation"] = Scale(s.Value, 100) }));
            else if (h is not null)
                cmds.Add(new(MatterClusters.ColorControl, "MoveToHue",
                    new Dictionary<string, object?> { ["hue"] = Scale(h.Value, 360), ["direction"] = 0 }));
            else if (s is not null)
                cmds.Add(new(MatterClusters.ColorControl, "MoveToSaturation",
                    new Dictionary<string, object?> { ["saturation"] = Scale(s.Value, 100) }));
        }

        return cmds;
    }

    private static IReadOnlyDictionary<string, object?> Empty() => new Dictionary<string, object?>();
}
