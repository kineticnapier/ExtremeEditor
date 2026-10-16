namespace ExtremeEditor.Core;

/// <summary>Explicit ADOFAI playback parameters; music grid BPM is independent.</summary>
public sealed record TimeFirstAdoFaiSettings(
    double InitialBpm, double OffsetMilliseconds = 0,
    double PitchPercent = 100, int CountdownTicks = 0,
    bool SeparateCountdownTime = false);

public sealed record TimeFirstAdoFaiCompositionResult(
    LevelDocument? Document, IReadOnlyList<string> Issues)
{
    public bool Accepted => Document is not null && Issues.Count == 0;
}

public sealed record TimeFirstAdoFaiExportResult(
    string DestinationPath, string SourceCommit, int FloorCount, int ActionCount);

public static class TimeFirstSegmentAdoFaiExporter
{
    public static TimeFirstAdoFaiCompositionResult ComposeAndValidate(
        TimeFirstSegmentSnapshot snapshot, TimeFirstAdoFaiSettings settings) =>
        throw new NotImplementedException("RED: validate time-first chart timing and Segment seams.");

    public static TimeFirstAdoFaiExportResult ExportValidated(
        TimeFirstSegmentGitWorkspace workspace, TimeFirstAdoFaiSettings settings,
        string destinationPath, bool overwriteExisting = false) =>
        throw new NotImplementedException("RED: export only an immutable, fully validated chart.");
}
