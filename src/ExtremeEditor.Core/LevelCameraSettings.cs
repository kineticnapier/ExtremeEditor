using System.Text.Json;

namespace ExtremeEditor.Core;

public sealed record LevelCameraSettings(
    string RelativeTo,
    double PositionX,
    double PositionY,
    double Rotation,
    double Zoom)
{
    public static LevelCameraSettings Default { get; } = new("Player", 0.0, 0.0, 0.0, 100.0);

    internal static LevelCameraSettings LoadFromSource(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "<synthetic>" || !File.Exists(path))
            return Default;

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (!document.RootElement.TryGetProperty("settings", out JsonElement settings) ||
                settings.ValueKind != JsonValueKind.Object)
            {
                return Default;
            }

            string relativeTo = ReadString(settings, "relativeTo") ?? Default.RelativeTo;
            double positionX = Default.PositionX;
            double positionY = Default.PositionY;
            if (settings.TryGetProperty("position", out JsonElement position) && position.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement component in position.EnumerateArray())
                {
                    if (index == 0 && TryReadDouble(component, out double x))
                        positionX = x;
                    else if (index == 1 && TryReadDouble(component, out double y))
                        positionY = y;
                    if (++index >= 2)
                        break;
                }
            }

            double rotation = ReadDouble(settings, "rotation", Default.Rotation);
            double zoom = ReadDouble(settings, "zoom", Default.Zoom);
            if (!double.IsFinite(zoom) || zoom <= 0.0)
                zoom = Default.Zoom;

            return new LevelCameraSettings(relativeTo, positionX, positionY, rotation, zoom);
        }
        catch (JsonException)
        {
            return Default;
        }
        catch (IOException)
        {
            return Default;
        }
        catch (UnauthorizedAccessException)
        {
            return Default;
        }
    }

    private static string? ReadString(JsonElement settings, string name)
    {
        if (!settings.TryGetProperty(name, out JsonElement value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static double ReadDouble(JsonElement settings, string name, double fallback)
    {
        return settings.TryGetProperty(name, out JsonElement value) && TryReadDouble(value, out double parsed)
            ? parsed
            : fallback;
    }

    private static bool TryReadDouble(JsonElement value, out double parsed)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out parsed))
            return double.IsFinite(parsed);
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out parsed))
        {
            return double.IsFinite(parsed);
        }
        parsed = 0.0;
        return false;
    }
}
