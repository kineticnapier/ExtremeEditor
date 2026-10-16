namespace ExtremeEditor.Core;

public sealed record TogetherSection(
    string InternalId,
    long StartSample,
    long EndSampleExclusive,
    string? OwnerName);

public sealed record TogetherSectionPlan(
    int SampleRate,
    long TotalSamples,
    IReadOnlyList<TogetherSection> Sections);

public static class TogetherSectionPlanner
{
    public static TogetherSectionPlan CreateWholeSong(
        int sampleRate, long totalSamples) =>
        throw new NotImplementedException(
            "RED: initialize one whole-song Together section.");

    public static TogetherSectionPlan Split(
        TogetherSectionPlan plan, string sectionId, long sample) =>
        throw new NotImplementedException();

    public static TogetherSectionPlan JoinWithNext(
        TogetherSectionPlan plan, string leftSectionId) =>
        throw new NotImplementedException();

    public static TogetherSectionPlan MoveBoundary(
        TogetherSectionPlan plan, string leftSectionId, long newBoundarySample) =>
        throw new NotImplementedException();

    public static TogetherSectionPlan AssignOwner(
        TogetherSectionPlan plan, string sectionId, string ownerName) =>
        throw new NotImplementedException();

    public static SegmentTimeTimeline FinalizeTimeline(TogetherSectionPlan plan) =>
        throw new NotImplementedException();

    public static void Validate(TogetherSectionPlan plan) =>
        throw new NotImplementedException();
}
