using System.Text;

namespace Matterhorn.Bridge;

/// <summary>Default friendly-name generation and slugification (spec §5).</summary>
public static class FriendlyName
{
    public static string Default(string? productName, ulong nodeId, ushort endpoint)
    {
        var prefix = string.IsNullOrWhiteSpace(productName) ? "node" : Slug(productName);
        return $"{prefix}_{nodeId}_{endpoint}";
    }

    public static string Slug(string input)
    {
        var sb = new StringBuilder();
        foreach (var ch in input.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        return sb.ToString().Trim('_');
    }
}
