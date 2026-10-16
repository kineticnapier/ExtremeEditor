using System.Text.Json;
using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

internal static class TogetherSectionPlannerRegression
{
    private const int Rate = 48_000;
    private const long Total = Rate * 180L;

    public static void Run()
    {
        TogetherSectionPlan whole =
            TogetherSectionPlanner.CreateWholeSong(Rate, Total);
        Check(whole.Sections.Count == 1, "song starts as one section");
        Section(whole, 0, 0, Total, null);

        string frozen = JsonSerializer.Serialize(whole);
        TogetherSectionPlan two = TogetherSectionPlanner.Split(
            whole, whole.Sections[0].InternalId, Rate * 60L);
        TogetherSectionPlan three = TogetherSectionPlanner.Split(
            two, two.Sections[1].InternalId, Rate * 120L);
        Check(JsonSerializer.Serialize(whole) == frozen,
            "split does not mutate the previous plan");
        Check(three.Sections.Select(section => section.InternalId).Distinct().Count() == 3,
            "internal IDs are generated uniquely");
        Coverage(three);

        three = TogetherSectionPlanner.AssignOwner(
            three, three.Sections[0].InternalId, "Alice");
        three = TogetherSectionPlanner.AssignOwner(
            three, three.Sections[1].InternalId, "Bob");
        three = TogetherSectionPlanner.AssignOwner(
            three, three.Sections[2].InternalId, "Carol");
        SegmentTimeTimeline timeline =
            TogetherSectionPlanner.FinalizeTimeline(three);
        Check(timeline.Partitions.Select(p => p.OwnerId)
                .SequenceEqual(["Alice", "Bob", "Carol"]),
            "owners finalize in music-time order");
        Check(timeline.Partitions.All(p => p.ContentRevisionId is null),
            "new sections remain explicitly empty");

        TogetherSectionPlan moved = TogetherSectionPlanner.MoveBoundary(
            three, three.Sections[0].InternalId, Rate * 75L);
        Section(moved, 0, 0, Rate * 75L, "Alice");
        Section(moved, 1, Rate * 75L, Rate * 120L, "Bob");
        Coverage(moved);

        TogetherSectionPlan joined = TogetherSectionPlanner.JoinWithNext(
            moved, moved.Sections[0].InternalId);
        Check(joined.Sections.Count == 2, "adjacent sections join");
        Check(joined.Sections[0].OwnerName is null,
            "joining different owners requires an explicit reassignment");
        Coverage(joined);

        ExpectRejected(() => TogetherSectionPlanner.FinalizeTimeline(
            TogetherSectionPlanner.CreateWholeSong(Rate, Total)),
            "unassigned section cannot create a collaboration");
        ExpectRejected(() => TogetherSectionPlanner.Split(
            whole, whole.Sections[0].InternalId, 0), "split at song start");
        ExpectRejected(() => TogetherSectionPlanner.Split(
            whole, whole.Sections[0].InternalId, Total), "split at song end");
        ExpectRejected(() => TogetherSectionPlanner.MoveBoundary(
            three, three.Sections[0].InternalId, 0), "zero-length left section");
        ExpectRejected(() => TogetherSectionPlanner.MoveBoundary(
            three, three.Sections[0].InternalId, Rate * 120L),
            "zero-length right section");
        ExpectRejected(() => TogetherSectionPlanner.AssignOwner(
            three, three.Sections[0].InternalId, "  "), "blank owner");
    }

    private static void Coverage(TogetherSectionPlan plan)
    {
        TogetherSectionPlanner.Validate(plan);
        Check(plan.Sections[0].StartSample == 0, "coverage begins at song start");
        Check(plan.Sections[^1].EndSampleExclusive == plan.TotalSamples,
            "coverage reaches song end");
        for (int i = 1; i < plan.Sections.Count; i++)
            Check(plan.Sections[i - 1].EndSampleExclusive == plan.Sections[i].StartSample,
                "sections have neither gap nor overlap");
    }

    private static void Section(TogetherSectionPlan plan, int index,
        long start, long end, string? owner)
    {
        TogetherSection section = plan.Sections[index];
        Check(section.StartSample == start && section.EndSampleExclusive == end &&
              section.OwnerName == owner, $"section {index + 1} mismatch");
    }

    private static void ExpectRejected(Action action, string label)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Unsafe section operation accepted: " + label);
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
