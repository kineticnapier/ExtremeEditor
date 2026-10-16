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
        int sampleRate, long totalSamples)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate),
                "The audio sample rate must be positive.");
        if (totalSamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalSamples),
                "The song must contain at least one sample.");

        return Snapshot(sampleRate, totalSamples,
        [
            new TogetherSection("section-1", 0, totalSamples, OwnerName: null)
        ]);
    }

    public static TogetherSectionPlan Split(
        TogetherSectionPlan plan, string sectionId, long sample)
    {
        Validate(plan);
        int index = Find(plan, sectionId);
        TogetherSection section = plan.Sections[index];
        if (sample <= section.StartSample || sample >= section.EndSampleExclusive)
            throw new ArgumentException(
                "The split point must be strictly inside the selected section.", nameof(sample));

        var sections = plan.Sections.ToList();
        sections[index] = section with { EndSampleExclusive = sample };
        sections.Insert(index + 1, section with
        {
            InternalId = NextId(plan),
            StartSample = sample
        });
        return Snapshot(plan.SampleRate, plan.TotalSamples, sections);
    }

    public static TogetherSectionPlan JoinWithNext(
        TogetherSectionPlan plan, string leftSectionId)
    {
        Validate(plan);
        int index = Find(plan, leftSectionId);
        if (index >= plan.Sections.Count - 1)
            throw new InvalidOperationException("The final section has no following section to join.");

        TogetherSection left = plan.Sections[index];
        TogetherSection right = plan.Sections[index + 1];
        string? owner = string.Equals(left.OwnerName, right.OwnerName,
            StringComparison.Ordinal) ? left.OwnerName : null;

        var sections = plan.Sections.ToList();
        sections[index] = left with
        {
            EndSampleExclusive = right.EndSampleExclusive,
            OwnerName = owner
        };
        sections.RemoveAt(index + 1);
        return Snapshot(plan.SampleRate, plan.TotalSamples, sections);
    }

    public static TogetherSectionPlan MoveBoundary(
        TogetherSectionPlan plan, string leftSectionId, long newBoundarySample)
    {
        Validate(plan);
        int index = Find(plan, leftSectionId);
        if (index >= plan.Sections.Count - 1)
            throw new InvalidOperationException("The final section has no movable right boundary.");

        TogetherSection left = plan.Sections[index];
        TogetherSection right = plan.Sections[index + 1];
        if (newBoundarySample <= left.StartSample ||
            newBoundarySample >= right.EndSampleExclusive)
        {
            throw new ArgumentException(
                "The boundary must leave both adjacent sections nonempty.",
                nameof(newBoundarySample));
        }

        var sections = plan.Sections.ToList();
        sections[index] = left with { EndSampleExclusive = newBoundarySample };
        sections[index + 1] = right with { StartSample = newBoundarySample };
        return Snapshot(plan.SampleRate, plan.TotalSamples, sections);
    }

    public static TogetherSectionPlan AssignOwner(
        TogetherSectionPlan plan, string sectionId, string ownerName)
    {
        Validate(plan);
        if (string.IsNullOrWhiteSpace(ownerName))
            throw new ArgumentException("A participant name is required.", nameof(ownerName));

        int index = Find(plan, sectionId);
        var sections = plan.Sections.ToList();
        sections[index] = sections[index] with { OwnerName = ownerName.Trim() };
        return Snapshot(plan.SampleRate, plan.TotalSamples, sections);
    }

    public static SegmentTimeTimeline FinalizeTimeline(TogetherSectionPlan plan)
    {
        Validate(plan);
        TogetherSection? unassigned = plan.Sections.FirstOrDefault(
            static section => string.IsNullOrWhiteSpace(section.OwnerName));
        if (unassigned is not null)
            throw new InvalidOperationException(
                $"Section '{unassigned.InternalId}' needs a participant before collaboration can start.");

        SegmentTimePartition[] partitions = plan.Sections.Select(section =>
            new SegmentTimePartition(
                section.InternalId,
                section.StartSample,
                section.EndSampleExclusive,
                ContentRevisionId: null,
                ContentOriginSample: section.StartSample,
                OwnerId: section.OwnerName,
                Status: SegmentPartitionStatus.Active)).ToArray();
        var timeline = new SegmentTimeTimeline(
            plan.SampleRate, Array.AsReadOnly(partitions));
        SegmentTimePlanner.Validate(timeline);
        return timeline;
    }

    public static void Validate(TogetherSectionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.SampleRate <= 0)
            throw new ArgumentException("The audio sample rate must be positive.", nameof(plan));
        if (plan.TotalSamples <= 0)
            throw new ArgumentException("The song must contain at least one sample.", nameof(plan));
        if (plan.Sections is null || plan.Sections.Count == 0)
            throw new ArgumentException("At least one section is required.", nameof(plan));

        var ids = new HashSet<string>(StringComparer.Ordinal);
        long expectedStart = 0;
        foreach (TogetherSection? section in plan.Sections)
        {
            if (section is null)
                throw new ArgumentException("A section cannot be null.", nameof(plan));
            if (string.IsNullOrWhiteSpace(section.InternalId) ||
                !ids.Add(section.InternalId))
            {
                throw new ArgumentException(
                    "Internal section IDs must be nonempty and unique.", nameof(plan));
            }
            if (section.StartSample != expectedStart ||
                section.EndSampleExclusive <= section.StartSample ||
                section.EndSampleExclusive > plan.TotalSamples)
            {
                throw new ArgumentException(
                    "Sections must be ordered, nonempty, and cover the song without gaps or overlaps.",
                    nameof(plan));
            }
            if (section.OwnerName is not null && string.IsNullOrWhiteSpace(section.OwnerName))
                throw new ArgumentException("A participant name cannot be blank.", nameof(plan));
            expectedStart = section.EndSampleExclusive;
        }

        if (expectedStart != plan.TotalSamples)
            throw new ArgumentException(
                "Sections must cover the song through its final sample.", nameof(plan));
    }

    private static TogetherSectionPlan Snapshot(
        int sampleRate, long totalSamples, IEnumerable<TogetherSection> sections)
    {
        TogetherSection[] copy = sections.ToArray();
        var plan = new TogetherSectionPlan(
            sampleRate, totalSamples, Array.AsReadOnly(copy));
        Validate(plan);
        return plan;
    }

    private static int Find(TogetherSectionPlan plan, string sectionId)
    {
        if (string.IsNullOrWhiteSpace(sectionId))
            throw new ArgumentException("A section must be selected.", nameof(sectionId));
        for (int i = 0; i < plan.Sections.Count; i++)
        {
            if (string.Equals(plan.Sections[i].InternalId, sectionId,
                StringComparison.Ordinal))
                return i;
        }
        throw new ArgumentException("The selected section no longer exists.", nameof(sectionId));
    }

    private static string NextId(TogetherSectionPlan plan)
    {
        var existing = new HashSet<string>(
            plan.Sections.Select(static section => section.InternalId),
            StringComparer.Ordinal);
        for (int number = 1; number < int.MaxValue; number++)
        {
            string candidate = $"section-{number}";
            if (!existing.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException("No internal section ID is available.");
    }
}
