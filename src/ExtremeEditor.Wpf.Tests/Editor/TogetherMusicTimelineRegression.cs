using System.IO;
using System.Xml.Linq;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class TogetherMusicTimelineRegression
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "extremeeditor-music-timeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string wav = Path.Combine(root, "measured-8000hz.wav");
            WriteTestWave(wav, sampleRate: 8_000, sampleCount: 800);
            AudioWaveformData waveform = AudioWaveformLoader.Load(
                wav, maximumPeakCount: 8);

            Equal(8_000, waveform.SampleRate,
                "sample rate is measured from the audio file");
            Equal(800L, waveform.TotalSamples, "decoded sample-frame count");
            Equal(8, waveform.Peaks.Count, "bounded waveform bucket count");
            Check(waveform.Peaks.All(peak =>
                    peak.EndSampleExclusive > peak.StartSample &&
                    peak.Minimum <= -0.99f && peak.Maximum >= 0.99f),
                "waveform retains signed extrema");
            if (waveform.Peaks is IList<AudioWaveformPeak> peaks)
                Check(peaks.IsReadOnly, "waveform collection is immutable");

            MusicTimingMap timing = MusicTimingMap.Build(8_000,
            [
                new MusicTimingPoint(0, 120m),
                new MusicTimingPoint(400, 60m)
            ]);
            var viewport = new TogetherMusicTimelineViewport(waveform, timing);
            viewport.Configure(width: 400, visibleStartSample: 0,
                samplesPerPixel: 2);

            SegmentTimePartition[] segments =
            [
                new("intro", 0, 400, null, 0, "A",
                    SegmentPartitionStatus.Active),
                new("drop", 400, 800, null, 400, "B",
                    SegmentPartitionStatus.Active)
            ];
            TogetherMusicTimelineFrame frame = viewport.Project(
                MusicBeatSubdivision.One, segments);
            Equal(0L, frame.VisibleStartSample, "visible start");
            Equal(800L, frame.VisibleEndSampleExclusive, "visible end");
            Sequence([0L, 400L],
                frame.GridMarks.Select(mark => mark.Line.AudioSample),
                "only visible timing-section grid lines are projected");
            Equal(8, frame.WaveformColumns.Count,
                "only intersecting bounded waveform columns are projected");
            Equal(2, frame.SegmentBands.Count, "sample-time Segment bands");

            MusicTimingSnap snapped = viewport.SnapAtPixel(
                199, MusicBeatSubdivision.One);
            Equal(400L, snapped.AudioSample,
                "pointer selection snaps across a BPM boundary");

            viewport.ZoomAt(x: 200, factor: 0.5);
            Equal(1d, viewport.SamplesPerPixel, "zoom changes scale");
            Equal(200L, viewport.VisibleStartSample,
                "zoom preserves the pointer's sample anchor");
            viewport.ScrollByPixels(100);
            Equal(300L, viewport.VisibleStartSample,
                "horizontal scrolling changes visible sample origin");

            RequireWpfSurface();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void RequireWpfSurface()
    {
        string repository = FindRepositoryRoot();
        string xamlPath = Path.Combine(repository, "src", "ExtremeEditor.Wpf",
            "Dialogs", "TogetherWorkspaceWindow.xaml");
        string codePath = Path.ChangeExtension(xamlPath, ".xaml.cs");
        XDocument document = XDocument.Load(xamlPath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (string name in new[]
        {
            "MusicTimeline", "BpmBox", "BeatOffsetBox",
            "SnapSubdivisionBox", "TimelineZoomSlider", "TimelineScrollBar",
            "SelectionStartSampleText", "SelectionEndSampleText",
            "SegmentOwnerBox", "AddTimeSegmentButton", "MeasuredAudioText"
        })
        {
            Check(document.Descendants().Any(element =>
                    (string?)element.Attribute(x + "Name") == name),
                "WPF music timeline is missing " + name);
        }

        string code = File.ReadAllText(codePath);
        foreach (string handler in new[]
        {
            "LoadMusicTimelineAsync", "MusicTimelineSelectionChanged",
            "AddTimeSegmentClick", "TimelineZoomChanged",
            "TimelineScrollChanged", "CreateTimeFirstWorkspace",
            "OpenTimeFirstWorkspace", "ApplyTimeFirstSnapshot",
            "TimeFirstSegmentGitWorkspace", "SegmentContentPlanner.CreateEmpty",
            "SegmentTimeDraftEditor.Open"
        })
            Check(code.Contains(handler, StringComparison.Ordinal),
                "WPF music timeline is not wired to " + handler);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src",
                    "ExtremeEditor.Wpf", "Dialogs",
                    "TogetherWorkspaceWindow.xaml")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private static void WriteTestWave(
        string path, int sampleRate, int sampleCount)
    {
        const short low = -32_768;
        const short high = 32_767;
        int dataLength = checked(sampleCount * sizeof(short));
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(dataLength);
        for (int i = 0; i < sampleCount; i++)
            writer.Write((i & 1) == 0 ? low : high);
    }

    private static void Sequence<T>(
        IEnumerable<T> expected, IEnumerable<T> actual, string label)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException("Unexpected sequence: " + label);
    }

    private static void Equal<T>(T expected, T actual, string label)
        where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
            throw new InvalidOperationException(
                $"{label}: expected {expected}, actual {actual}");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition)
            throw new InvalidOperationException(label);
    }
}
