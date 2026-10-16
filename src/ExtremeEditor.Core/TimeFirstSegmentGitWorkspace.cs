namespace ExtremeEditor.Core;

/// <summary>
/// Stable identity of the decoded music used by a time-first Together project.
/// The file name is descriptive; SHA-256, measured sample rate, and decoded
/// sample count are the identity-bearing values.
/// </summary>
public sealed record TimeFirstAudioIdentity(
    string FileName,
    string Sha256,
    int SampleRate,
    long TotalSamples);

/// <summary>
/// Complete time-first collaboration state. Explicit empty content and an
/// empty draft are both persisted; an absent payload is corruption, not empty.
/// </summary>
public sealed record TimeFirstSegmentSnapshot(
    TimeFirstAudioIdentity Audio,
    IReadOnlyList<MusicTimingPoint> TimingPoints,
    SegmentTimeTimeline Timeline,
    IReadOnlyDictionary<string, SegmentContentEntry> Content,
    IReadOnlyDictionary<string, SegmentTimeDraft> Drafts);

public sealed class TimeFirstSegmentContentMissingException : IOException
{
    public TimeFirstSegmentContentMissingException(string message) : base(message) { }
}

/// <summary>
/// Versioned Git persistence for sample-time-first Together projects.
/// </summary>
public sealed class TimeFirstSegmentGitWorkspace
{
    private TimeFirstSegmentGitWorkspace(string directory) => Directory = directory;

    public string Directory { get; }

    public static TimeFirstSegmentGitWorkspace Initialize(string directory) =>
        throw new NotImplementedException(
            "RED: initialize the versioned time-first Together workspace.");

    public static TimeFirstSegmentGitWorkspace Open(string directory) =>
        throw new NotImplementedException();

    public void Save(TimeFirstSegmentSnapshot snapshot) =>
        throw new NotImplementedException();

    public TimeFirstSegmentSnapshot Load() => throw new NotImplementedException();

    public string Commit(string message) => throw new NotImplementedException();

    public static void ValidateSnapshot(TimeFirstSegmentSnapshot snapshot) =>
        throw new NotImplementedException();
}
