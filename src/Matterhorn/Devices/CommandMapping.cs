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

        return cmds;
    }

    private static IReadOnlyDictionary<string, object?> Empty() => new Dictionary<string, object?>();
}
