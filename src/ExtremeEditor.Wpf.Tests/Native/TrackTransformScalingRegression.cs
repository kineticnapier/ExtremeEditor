using System.Numerics;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class TrackTransformScalingRegression
{
    private const float Tolerance = 0.0001f;

    public static void Run()
    {
        VerifyReferenceSemantics();
        VerifyPersistentScalingContract();
    }

    private static void VerifyReferenceSemantics()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(8);
        FixtureEvent[] events =
        [
            new(0, 1, true, false, 1.0, 2.0, 0, "ThisTile", 10.0, 120.0, 80.0, false),
            new(1, 1, true, false, 2.0, 0.0, 0, "ThisTile", 20.0, 130.0, 70.0, true),
            new(2, 2, false, false, 99.0, 99.0, 0, "ThisTile", 99.0, 999.0, 1.0, false),
            new(3, 3, true, true, 0.5, -0.5, 0, "ThisTile", 90.0, 50.0, 40.0, false),
            new(4, 3, true, false, 0.25, 0.75, 0, "ThisTile", 30.0, 140.0, 60.0, true),
            new(5, 3, true, true, -0.25, 0.25, 0, "ThisTile", -30.0, 90.0, 65.0, false),
            new(6, 4, true, false, 1.0, 0.0, 1, "ThisTile", 35.0, 145.0, 58.0, false),
            new(7, 5, true, false, -1.0, 1.0, 1, "Start", -45.0, 75.0, 55.0, true)
        ];
        level.ReplaceActions(events.Select(CreateAction));

        StaticTrackTransform[] expected = ResolveReference(level, events);
        StaticTrackTransform[] actual = TrackTransformResolver.ResolveStatic(level);
        if (actual.Length != expected.Length)
            throw new InvalidOperationException($"Static transform count changed: expected {expected.Length}, actual {actual.Length}.");

        for (int floor = 0; floor < expected.Length; floor++)
        {
            StaticTrackTransform wanted = expected[floor];
            StaticTrackTransform found = actual[floor];
            if (!Close(wanted.X, found.X) ||
                !Close(wanted.Y, found.Y) ||
                !Close(wanted.Rotation, found.Rotation) ||
                !Close(wanted.ScaleX, found.ScaleX) ||
                !Close(wanted.ScaleY, found.ScaleY) ||
                !Close(wanted.Opacity, found.Opacity) ||
                wanted.StickToFloors != found.StickToFloors)
            {
                throw new InvalidOperationException(
                    $"Static PositionTrack semantics changed at floor {floor}: expected {wanted}, actual {found}.");
            }
        }
    }

    private static void VerifyPersistentScalingContract()
    {
        const int floorCount = 8_192;
        const int eventCount = 96;
        LevelDocument level = LevelDocument.CreateSynthetic(floorCount);
        var actions = new LevelAction[eventCount];
        for (int index = 0; index < eventCount; index++)
        {
            int floor = 1 + index * 37 % 2_048;
            actions[index] = CreateAction(new FixtureEvent(
                index,
                floor,
                Active: true,
                JustThisTile: false,
                PositionX: 0.01,
                PositionY: -0.01,
                RelativeOffset: 0,
                RelativeMode: "ThisTile",
                Rotation: index,
                Scale: 100.0 + index,
                Opacity: 50.0 + index % 50,
                StickToFloors: (index & 1) == 0));
        }
        level.ReplaceActions(actions);

        var diagnostics = new TrackTransformResolveDiagnostics();
        _ = TrackTransformResolver.ResolveStatic(level, diagnostics);

        long linearBudget = floorCount + eventCount * 8L;
        if (diagnostics.WorkItemCount > linearBudget)
        {
            throw new InvalidOperationException(
                "persistent track transforms scale with event-floor products. " +
                $"work items={diagnostics.WorkItemCount:N0}, linear budget={linearBudget:N0}.");
        }
    }

    private static LevelAction CreateAction(FixtureEvent item) => new(
        item.Floor,
        "PositionTrack",
        item.Active,
        null,
        null,
        null,
        null)
    {
        SourceIndex = item.SourceIndex,
        PropertyOverrides = new JsonObject
        {
            ["positionOffset"] = new JsonArray(item.PositionX, item.PositionY),
            ["relativeTo"] = new JsonArray(item.RelativeOffset, item.RelativeMode),
            ["rotation"] = item.Rotation,
            ["scale"] = item.Scale,
            ["opacity"] = item.Opacity,
            ["justThisTile"] = item.JustThisTile,
            ["stickToFloors"] = item.StickToFloors
        }
    };

    private static StaticTrackTransform[] ResolveReference(LevelDocument level, IEnumerable<FixtureEvent> source)
    {
        int count = level.FloorCount;
        Vector2[] position = level.Positions.ToArray();
        var rotation = new float[count];
        var scale = Enumerable.Repeat(1f, count).ToArray();
        var opacity = Enumerable.Repeat(1f, count).ToArray();
        var stick = Enumerable.Repeat(true, count).ToArray();
        Vector2 persistentOffset = Vector2.Zero;

        foreach (FixtureEvent item in source.OrderBy(static item => item.Floor).ThenBy(static item => item.SourceIndex))
        {
            if (!item.Active || (uint)item.Floor >= (uint)count)
                continue;

            int floor = item.Floor;
            int target = ResolveReference(item, count);
            float dx = (float)item.PositionX * PathBuilder.DefaultLongTileSize;
            float dy = (float)item.PositionY * PathBuilder.DefaultLongTileSize;
            if (target != floor)
            {
                Vector2 baseCurrent = level.Positions[floor] + persistentOffset;
                Vector2 relativeDelta = position[target] - baseCurrent;
                dx += relativeDelta.X;
                dy += relativeDelta.Y;
            }

            var delta = new Vector2(dx, dy);
            if (item.JustThisTile)
            {
                position[floor] += delta;
            }
            else
            {
                for (int index = floor; index < count; index++)
                    position[index] += delta;
                persistentOffset = position[floor] - level.Positions[floor];
            }

            ApplyReference(scale, floor, (float)(item.Scale / 100.0), item.JustThisTile);
            ApplyReference(rotation, floor, (float)item.Rotation * MathF.PI / 180f, item.JustThisTile);
            ApplyReference(opacity, floor, Math.Clamp((float)(item.Opacity / 100.0), 0f, 100f), item.JustThisTile);
            ApplyReference(stick, floor, item.StickToFloors, item.JustThisTile);
        }

        var result = new StaticTrackTransform[count];
        for (int floor = 0; floor < count; floor++)
        {
            result[floor] = new StaticTrackTransform(
                position[floor].X,
                position[floor].Y,
                rotation[floor],
                scale[floor],
                scale[floor],
                opacity[floor],
                stick[floor]);
        }
        return result;
    }

    private static int ResolveReference(FixtureEvent item, int count)
    {
        int target = item.RelativeMode switch
        {
            "Start" or "1" => item.RelativeOffset,
            "End" or "2" => count - 1 + item.RelativeOffset,
            _ => item.Floor + item.RelativeOffset
        };
        return Math.Clamp(target, 0, count - 1);
    }

    private static void ApplyReference<T>(T[] values, int floor, T value, bool justThisTile)
    {
        int end = justThisTile ? floor + 1 : values.Length;
        for (int index = floor; index < end; index++)
            values[index] = value;
    }

    private static bool Close(float expected, float actual) => Math.Abs(expected - actual) <= Tolerance;

    private sealed record FixtureEvent(
        int SourceIndex,
        int Floor,
        bool Active,
        bool JustThisTile,
        double PositionX,
        double PositionY,
        int RelativeOffset,
        string RelativeMode,
        double Rotation,
        double Scale,
        double Opacity,
        bool StickToFloors);
}
