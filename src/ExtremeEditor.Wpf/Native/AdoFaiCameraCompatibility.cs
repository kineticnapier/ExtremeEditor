using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal static class AdoFaiCameraCompatibility
{
    /// <summary>
    /// ADOFAI serializes camera positions/offsets in tile units. ExtremeEditor's
    /// native scene uses Unity-style world units, where one stock long tile is
    /// PathBuilder.DefaultLongTileSize (1.5) units between floor centres.
    /// Convert after parsing so the JSON-facing metadata remains ADOFAI-shaped.
    /// </summary>
    internal static CameraSourceData ToWorldUnits(CameraSourceData source)
    {
        ArgumentNullException.ThrowIfNull(source);

        CameraSourceEvent[] events = new CameraSourceEvent[source.Events.Length];
        for (int i = 0; i < source.Events.Length; i++)
        {
            CameraSourceEvent item = source.Events[i];
            events[i] = item with
            {
                Position = ScalePosition(item.Position)
            };
        }

        return source with
        {
            Events = events,
            InitialPosition = ScalePosition(source.InitialPosition)
        };
    }

    private static CameraSourcePosition ScalePosition(CameraSourcePosition position) => new(
        Scale(position.X),
        Scale(position.Y));

    private static double? Scale(double? value) =>
        value is double component
            ? component * PathBuilder.DefaultLongTileSize
            : null;
}
