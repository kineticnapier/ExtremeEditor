using System.IO;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

// Phantigma (AWC2026 Finals) uses OutCirc, InCirc, InOutCirc, OutElastic,
// OutBack and OutExpo on 181 timed MoveDecorations events. Every one of them
// must take part in decoration evaluation, and an isolated event must move
// its targets along the DOTween curve from the value it started at.
internal static class PhantigmaMoveDecorationsEaseRegression
{
    // The end state is sampled just after the tween ends.
    private const double EndSampleOffset = 1e-6;

    private static readonly HashSet<string> FormerlySkippedEases = new(StringComparer.Ordinal)
    {
        "OutCirc", "InCirc", "InOutCirc", "OutElastic", "OutBack", "OutExpo"
    };

    public static void Run()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "4hf3bmk", "main.adofai");
        if (!File.Exists(path))
        {
            Console.WriteLine("SKIP: Phantigma main.adofai is not present on this machine.");
            return;
        }

        LevelDocument level = WpfLevelLoader.Load(path).Document;
        VfxTimeline timeline = VfxTimelineBuilder.Build(level);
        VfxOccurrence[] moves = timeline.Occurrences
            .Where(o => o.Active && o.EventType == "MoveDecorations" && o.RepeatPlacementResolved != false)
            .ToArray();
        VfxOccurrence[] formerlySkipped = moves
            .Where(o => !o.IsRepeated && o.Duration is > 0 && o.Ease is string ease && FormerlySkippedEases.Contains(ease))
            .ToArray();
        Require(formerlySkipped.Length == 181,
            $"Expected 181 timed Phantigma MoveDecorations with Circ/Elastic/Back/Expo, found {formerlySkipped.Length}.");
        Require(formerlySkipped.All(o => DotweenEaseEvaluator.IsSupported(o.Ease)),
            "A Phantigma MoveDecorations ease is still reported as unsupported.");

        int checkedValues = 0;
        int checkedEvents = 0;
        var checkedByScene = new Dictionary<string, int> { ["1-29"] = 0, ["63-72"] = 0, ["373-382"] = 0 };
        var failures = new List<string>();
        foreach (VfxOccurrence move in formerlySkipped)
        {
            double start = move.StartTime;
            double end = start + move.DurationSeconds!.Value;
            LevelDecoration[] targets = level.Decorations
                .Where(d => d.EventType == "AddDecoration" && MoveDecorationsTargeting.Targets(d, move))
                .ToArray();
            if (targets.Length == 0)
                continue;
            // Other events that start while this one runs, up to the end
            // sample, on the same targets.
            VfxOccurrence[] concurrent = moves.Where(other =>
                    !ReferenceEquals(other, move) &&
                    other.StartTime >= start - 1e-9 && other.StartTime <= end + EndSampleOffset &&
                    targets.Any(d => MoveDecorationsTargeting.Targets(d, other)))
                .ToArray();

            JsonObject properties = MoveDecorationsTargeting.GetProperties(move.SourceEvent)!;
            double eased = MoveDecorationsFullEaseRegression.Oracle(move.Ease!, 0.5);
            double mid = start + (end - start) * 0.5;
            bool any = false;
            foreach (LevelDecoration decoration in targets)
            {
                DecorationState atStart = DecorationState.Evaluate(decoration, timeline, start);
                DecorationState atMid = DecorationState.Evaluate(decoration, timeline, mid);
                DecorationState atEnd = DecorationState.Evaluate(decoration, timeline, end + EndSampleOffset);
                foreach ((string name, double? target, Func<DecorationState, double> read) in Targets(properties))
                {
                    if (target is not double goal)
                        continue;
                    // Only a property no concurrent event touches has a
                    // closed-form expectation.
                    if (concurrent.Any(other =>
                            MoveDecorationsTargeting.Targets(decoration, other) &&
                            Targets(MoveDecorationsTargeting.GetProperties(other.SourceEvent)!)
                                .Any(t => t.Name == name && t.Target is not null)))
                    {
                        continue;
                    }
                    double from = read(atStart);
                    double expectedMid = from + (goal - from) * eased;
                    checkedValues++;
                    any = true;
                    if (Math.Abs(read(atMid) - expectedMid) > 0.01 || Math.Abs(read(atEnd) - goal) > 0.01)
                    {
                        failures.Add(
                            $"floor {move.Floor} tag '{properties["tag"]}' {move.Ease} {name}: start {from}, " +
                            $"mid {read(atMid)} (expected {expectedMid}), end {read(atEnd)} (expected {goal})");
                    }
                }
            }
            if (!any)
                continue;
            checkedEvents++;
            string? scene = move.Floor switch
            {
                >= 1 and <= 29 => "1-29",
                >= 63 and <= 72 => "63-72",
                >= 373 and <= 382 => "373-382",
                _ => null
            };
            if (scene is not null)
                checkedByScene[scene]++;
        }

        Require(failures.Count == 0,
            $"{failures.Count} Phantigma eased values are wrong:\n" + string.Join("\n", failures.Take(12)));
        Require(checkedEvents >= 40 && checkedByScene.Values.All(count => count > 0),
            $"Too few isolated Phantigma events were checked: {checkedEvents} " +
            $"({string.Join(", ", checkedByScene.Select(p => $"{p.Key}: {p.Value}"))}).");
        Console.WriteLine(
            $"Phantigma: {formerlySkipped.Length} formerly skipped MoveDecorations evaluated; " +
            $"{checkedEvents} isolated events / {checkedValues} values matched DOTween " +
            $"({string.Join(", ", checkedByScene.Select(p => $"floors {p.Key}: {p.Value}"))}).");
    }

    private static IEnumerable<(string Name, double? Target, Func<DecorationState, double> Read)> Targets(JsonObject properties)
    {
        if (properties["scale"] is JsonArray { Count: >= 2 } scale)
        {
            yield return ("scale.x", Number(scale[0]), s => s.ScaleX);
            yield return ("scale.y", Number(scale[1]), s => s.ScaleY);
        }
        yield return ("opacity", Number(properties["opacity"]), s => s.Opacity);
        yield return ("rotation", Number(properties["rotationOffset"]), s => s.Rotation);
    }

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out double number) ? number : null;

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
