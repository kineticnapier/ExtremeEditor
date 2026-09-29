using System.IO;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class MoveDecorationsTargetingRegression
{
    public static void Run()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-MoveDecorationTargeting-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "targeting.adofai");
            File.WriteAllText(
                path,
                """
                {
                  "angleData": [0, 0],
                  "settings": { "bpm": 60, "pitch": 100 },
                  "actions": [
                    {
                      "floor": 0,
                      "eventType": "MoveDecorations",
                      "active": true,
                      "duration": 0,
                      "tag": "foo",
                      "eventTag": "",
                      "opacity": 50
                    },
                    {
                      "floor": 0,
                      "eventType": "MoveDecorations",
                      "active": true,
                      "duration": 0,
                      "tag": "bar",
                      "eventTag": "foo",
                      "opacity": 10
                    }
                  ],
                  "decorations": [
                    {
                      "floor": 0,
                      "eventType": "AddDecoration",
                      "tag": "foo",
                      "relativeTo": "Global",
                      "opacity": 100
                    }
                  ]
                }
                """);

            LevelDocument level = AdoFaiLoader.LoadFlatAsync(path).GetAwaiter().GetResult().Document;
            VfxTimeline timeline = VfxTimelineBuilder.Build(level);
            LevelDecoration decoration = level.Decorations.Single();

            VfxOccurrence[] positive = timeline.Occurrences
                .Where(occurrence => MoveDecorationsTargeting.Targets(decoration, occurrence))
                .ToArray();
            VfxOccurrence[] negative = timeline.Occurrences
                .Where(occurrence => TargetTag(occurrence) == "bar")
                .ToArray();
            if (positive.Length == 0)
            {
                throw new InvalidOperationException(
                    "MoveDecorations target tag was not preserved as a related occurrence.");
            }
            if (negative.Length == 0)
                throw new InvalidOperationException("MoveDecorations inverse targeting fixture was not loaded.");

            if (negative.Any(occurrence => MoveDecorationsTargeting.Targets(decoration, occurrence)))
            {
                throw new InvalidOperationException(
                    "MoveDecorations targeting used eventTag instead of the target tag property.");
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static string? TargetTag(VfxOccurrence occurrence)
    {
        return MoveDecorationsTargeting.GetTargetTag(occurrence.SourceEvent);
    }
}
