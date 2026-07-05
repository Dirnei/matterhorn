namespace Matterhorn.Persistence;

/// <summary>Stable device-key ⇄ string codec ("{node}_{endpoint}") shared by the JSON stores.</summary>
public static class DeviceKeys
{
    public static string Format((ulong NodeId, ushort Endpoint) key) => $"{key.NodeId}_{key.Endpoint}";

    public static bool TryParse(string s, out (ulong NodeId, ushort Endpoint) key)
    {
        key = default;
        var parts = s.Split('_');
        if (parts.Length != 2) return false;
        if (!ulong.TryParse(parts[0], out var node) || !ushort.TryParse(parts[1], out var ep)) return false;
        key = (node, ep);
        return true;
    }
}
