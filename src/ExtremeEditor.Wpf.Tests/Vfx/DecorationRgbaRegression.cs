using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class DecorationRgbaRegression
{
    public static void Run()
    {
        VerifyStaticColors();
        VerifyColorAlphaTweens();
    }

    private static void VerifyStaticColors()
    {
        RequireColor("ffffff33", 0x33FF_FFFFu, "8-digit RGBA alpha was not preserved");
        RequireColor("ffffffff", 0xFFFF_FFFFu, "opaque RGBA was not preserved");
        RequireColor("ffffff00", 0x00FF_FFFFu, "transparent RGBA was not preserved");
        RequireColor("123456", 0xFF12_3456u, "6-digit RGB did not default to opaque");
        RequireColor("12345680", 0x8012_3456u, "RGB channels changed while decoding RGBA");

        DecorationState defaults = DecorationState.CreateInitial(CreateDecoration("default", null, 100));
        Require(defaults.Color == 0xFFFF_FFFFu, "missing color did not default to opaque white");
    }

    private static void VerifyColorAlphaTweens()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(3);
        level.InitialBpm = 60;
        level.PitchPercent = 100;
        LevelDecoration decoration = CreateDecoration("rgba", "ff804000", 50);
        level.ReplaceDecorations([decoration]);
        level.ReplaceActions(
        [
            CreateMove(0, 0, "rgba", 0, 2, "InOutCubic", "ff8040ff", 100),
            CreateMove(0, 1, "rgba", 360, 2, "Linear", "4080c000", null)
        ]);

        VfxTimeline timeline = VfxTimelineBuilder.Build(level);
        VfxOccurrence first = Find(timeline, 0);
        VfxOccurrence second = Find(timeline, 1);

        DecorationState start = DecorationState.Evaluate(decoration, timeline, first.StartTime);
        DecorationState quarter = DecorationState.Evaluate(
            decoration, timeline, first.StartTime + first.DurationSeconds!.Value * 0.25);
        DecorationState middle = DecorationState.Evaluate(
            decoration, timeline, first.StartTime + first.DurationSeconds.Value * 0.5);
        DecorationState end = DecorationState.Evaluate(
            decoration, timeline, first.StartTime + first.DurationSeconds.Value);

        Require(Alpha(start.Color) == 0, "alpha 0 tween did not begin at zero");
        Require(Alpha(quarter.Color) == 16, "InOutCubic alpha did not use DOTween easing");
        Require(Alpha(middle.Color) == 128, "alpha 0-to-255 midpoint is incorrect");
        Require(Alpha(end.Color) == 255, "alpha 0-to-255 tween did not reach its target");
        RequireNear(50, start.Opacity, "color tween changed independent opacity at start");
        RequireNear(75, middle.Opacity, "opacity and color alpha were not independently interpolated");
        RequireNear(100, end.Opacity, "opacity tween did not reach its target");

        DecorationState interrupted = DecorationState.Evaluate(decoration, timeline, second.StartTime);
        DecorationState interruptedMiddle = DecorationState.Evaluate(
            decoration, timeline, second.StartTime + second.DurationSeconds!.Value * 0.5);
        DecorationState interruptedEnd = DecorationState.Evaluate(
            decoration, timeline, second.StartTime + second.DurationSeconds.Value);
        Require(interrupted.Color == 0xFFFF_8040u,
            "replacement color tween did not complete the prior tween before interruption");
        Require(interruptedMiddle.Color == 0x80A0_8080u,
            "replacement color tween did not interpolate RGBA independently");
        Require(interruptedEnd.Color == 0x0040_80C0u,
            "alpha 255-to-0 replacement tween did not reach its target");

        DecorationState direct = DecorationState.Evaluate(
            decoration, timeline, second.StartTime + second.DurationSeconds.Value * 0.5);
        DecorationState repeated = DecorationState.Evaluate(
            decoration, timeline, second.StartTime + second.DurationSeconds.Value * 0.5);
        _ = DecorationState.Evaluate(
            decoration, timeline, second.StartTime + second.DurationSeconds.Value + 1000);
        DecorationState seekBack = DecorationState.Evaluate(
            decoration, timeline, first.StartTime + first.DurationSeconds.Value * 0.5);
        Require(direct == repeated, "same-time RGBA evaluation is not deterministic");
        Require(seekBack == middle, "backward seek retained future RGBA state");
    }

    private static void RequireColor(string color, uint expected, string message)
    {
        DecorationState state = DecorationState.CreateInitial(CreateDecoration("static", color, 100));
        Require(state.Color == expected, $"{message}: 0x{state.Color:X8}/0x{expected:X8}");
    }

    private static LevelDecoration CreateDecoration(string tag, string? color, double opacity)
    {
        var properties = new JsonObject
        {
            ["tag"] = tag,
            ["position"] = new JsonArray(0, 0),
            ["scale"] = new JsonArray(100, 100),
            ["opacity"] = opacity
        };
        if (color is not null)
            properties["color"] = color;
        return new LevelDecoration(0, "AddDecoration") { SourceIndex = 0, Properties = properties };
    }

    private static LevelAction CreateMove(
        int floor,
        int sourceIndex,
        string tag,
        double angleOffset,
        double duration,
        string ease,
        string color,
        double? opacity)
    {
        var properties = new JsonObject
        {
            ["floor"] = floor,
            ["eventType"] = "MoveDecorations",
            ["active"] = true,
            ["tag"] = tag,
            ["angleOffset"] = angleOffset,
            ["duration"] = duration,
            ["ease"] = ease,
            ["color"] = color
        };
        if (opacity is double value)
            properties["opacity"] = value;
        return new LevelAction(floor, "MoveDecorations", true, null, null, null, null)
        {
            SourceIndex = sourceIndex,
            AngleOffset = angleOffset,
            Duration = duration,
            PropertyOverrides = properties
        };
    }

    private static VfxOccurrence Find(VfxTimeline timeline, int sourceIndex) =>
        timeline.Occurrences.Single(occurrence =>
            occurrence.SourceIndex == sourceIndex && !occurrence.IsRepeated);

    private static int Alpha(uint color) => (int)(color >> 24);

    private static void RequireNear(double expected, double actual, string message)
    {
        if (Math.Abs(expected - actual) > 1e-9)
            throw new InvalidOperationException($"{message}: {actual:G17}/{expected:G17}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
