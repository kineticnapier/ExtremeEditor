using System.Text.Json.Nodes;

namespace ExtremeEditor.Core;

public static class MoveDecorationsTargeting
{
    public static bool Targets(LevelDecoration decoration, VfxOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(decoration);
        ArgumentNullException.ThrowIfNull(occurrence);

        string? decorationTags = ReadString(decoration.Properties["tag"]);
        string? targetTags = GetTargetTag(occurrence.SourceEvent);
        if (string.IsNullOrEmpty(decorationTags) || string.IsNullOrEmpty(targetTags))
            return false;

        foreach (string target in SplitTags(targetTags))
        {
            foreach (string candidate in SplitTags(decorationTags))
            {
                if (string.Equals(target, candidate, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    public static string? GetTargetTag(LevelAction action) =>
        ReadOverride(action.PropertyOverrides, "tag", action.TargetTag);

    public static IReadOnlyList<string> GetTargetTags(LevelAction action)
    {
        string? tags = GetTargetTag(action);
        return string.IsNullOrEmpty(tags) ? [] : SplitTags(tags);
    }

    public static string? GetEventTag(LevelAction action) =>
        ReadOverride(action.PropertyOverrides, "eventTag", action.EventTag);

    private static string? ReadOverride(JsonObject? properties, string name, string? fallback)
    {
        if (properties?.TryGetPropertyValue(name, out JsonNode? node) == true)
            return ReadString(node);
        return fallback;
    }

    private static string[] SplitTags(string tags) =>
        tags.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string? ReadString(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue(out string? text))
            return text;
        return null;
    }
}
