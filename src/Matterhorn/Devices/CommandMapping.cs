using System.Text.Json;
using Matterhorn.Matter;

namespace Matterhorn.Devices;

/// <summary>
/// Write path: maps a <c>/set</c> payload of semantic properties into ordered Matter write actions
/// (<see cref="CommandSpec"/> cluster commands and <see cref="AttributeWriteSpec"/> attribute writes).
/// Handlers run in registration order, so "on then set level" stays predictable.
/// </summary>
public static class CommandMapping
{
    private delegate void Handler(IReadOnlyDictionary<string, JsonElement> payload, List<IDeviceWrite> writes);

    private static readonly Handler[] Handlers =
    [
        State, Brightness, ColorTemp, ColorHueSat,
    ];

    public static IReadOnlyList<IDeviceWrite> Map(IReadOnlyDictionary<string, JsonElement> payload)
    {
        var writes = new List<IDeviceWrite>();
        foreach (var h in Handlers) h(payload, writes);
        return writes;
    }

    private static IReadOnlyDictionary<string, object?> Args(params (string, object?)[] kv) =>
        kv.ToDictionary(x => x.Item1, x => x.Item2);
    private static readonly IReadOnlyDictionary<string, object?> NoArgs = new Dictionary<string, object?>();

    private static void State(IReadOnlyDictionary<string, JsonElement> p, List<IDeviceWrite> w)
    {
        if (!p.TryGetValue("state", out var v)) return;
        switch (v.GetString()?.ToUpperInvariant())
        {
            case "ON": w.Add(new CommandSpec(MatterClusters.OnOff, "On", NoArgs)); break;
            case "OFF": w.Add(new CommandSpec(MatterClusters.OnOff, "Off", NoArgs)); break;
            case "TOGGLE": w.Add(new CommandSpec(MatterClusters.OnOff, "Toggle", NoArgs)); break;
        }
    }

    private static void Brightness(IReadOnlyDictionary<string, JsonElement> p, List<IDeviceWrite> w)
    {
        if (!p.TryGetValue("brightness", out var v)) return;
        w.Add(new CommandSpec(MatterClusters.LevelControl, "MoveToLevelWithOnOff",
            Args(("level", v.GetInt32()))));
    }

    private static void ColorTemp(IReadOnlyDictionary<string, JsonElement> p, List<IDeviceWrite> w)
    {
        if (!p.TryGetValue("color_temp", out var v)) return;
        w.Add(new CommandSpec(MatterClusters.ColorControl, "MoveToColorTemperature",
            Args(("colorTemperatureMireds", v.GetInt32()))));
    }

    private static void ColorHueSat(IReadOnlyDictionary<string, JsonElement> p, List<IDeviceWrite> w)
    {
        var hasHue = p.TryGetValue("hue", out var hue);
        var hasSat = p.TryGetValue("saturation", out var sat);
        if (hasHue && hasSat)
            w.Add(new CommandSpec(MatterClusters.ColorControl, "MoveToHueAndSaturation",
                Args(("hue", hue.GetInt32()), ("saturation", sat.GetInt32()))));
        else if (hasHue)
            w.Add(new CommandSpec(MatterClusters.ColorControl, "MoveToHue",
                Args(("hue", hue.GetInt32()), ("direction", 0))));
        else if (hasSat)
            w.Add(new CommandSpec(MatterClusters.ColorControl, "MoveToSaturation",
                Args(("saturation", sat.GetInt32()))));
    }
}
