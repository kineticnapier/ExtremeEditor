using System.Text.Json;
using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

/// <summary>
/// Regression for looking up time-positioned draft markers only within monotone
/// music-time Segments; never assumes a floor-time ordered global ADOFAI chart.
/// </summary>
internal static class SegmentDraftTimeSearchRegression
{
    private const int Rate = 48_000;
    private static long S(long seconds) => checked(seconds * Rate);

    public static void Run()
    {
        // A [0,40), unassigned gap [40,50), B [50,80), C [80,120).
        // Each active time range has its own sorted tile-draft snapshot.
        SegmentTimeTimeline timeline = new(Rate, []);
        timeline = SegmentTimePlanner.CreateEmpty(timeline, "A", S(0), S(40), "author-A");
        timeline = SegmentTimePlanner.CreateEmpty(timeline, "B", S(50), S(80), "author-B");
        timeline = SegmentTimePlanner.CreateEmpty(timeline, "C", S(80), S(120), "author-C");

        var content = timeline.Partitions.ToDictionary(
            p => p.SegmentId, SegmentContentPlanner.CreateEmpty,
            StringComparer.Ordinal);
        var a = SegmentTimeDraftEditor.Open(timeline, content, "A", "author-A");
        var b = SegmentTimeDraftEditor.Open(timeline, content, "B", "author-B");
        var c = SegmentTimeDraftEditor.Open(timeline, content, "C", "author-C");

        // Placement is not ordered; the editor's within-Segment order is.
        b = SegmentTimeDraftEditor.PlaceAnchor(b, "middle", S(60));
        b = SegmentTimeDraftEditor.PlaceAnchor(b, "late", S(79));
        b = SegmentTimeDraftEditor.PlaceAnchor(b, "middle-2", S(60));
        b = SegmentTimeDraftEditor.PlaceAnchor(b, "first", S(50));
        c = SegmentTimeDraftEditor.PlaceAnchor(c, "next-segment", S(80));

        var drafts = new Dictionary<string, SegmentTimeDraft>(StringComparer.Ordinal)
        {
            ["A"] = a,
            ["B"] = b,
            ["C"] = c
        };
        string originalTimeline = JsonSerializer.Serialize(timeline);
        string originalDrafts = JsonSerializer.Serialize(drafts);

        SegmentDraftTimeSearchIndex index =
            SegmentDraftTimeSearchIndex.Build(timeline, drafts);

        var reorderedTimeline = timeline with
        {
            Partitions = timeline.Partitions.Reverse().ToArray()
        };
        SegmentDraftTimeSearchIndex reorderedIndex =
            SegmentDraftTimeSearchIndex.Build(reorderedTimeline, drafts);
        Match(reorderedIndex.FindAt(S(50)), "B", ["first"],
            "active allocations are indexed in sample order, not storage order");

        Match(index.FindAt(S(0)), "A", Array.Empty<string>(),
            "an assigned but empty section differs from an unassigned gap");
        Match(index.FindAt(S(40)), null, Array.Empty<string>(),
            "unallocated gap returns no containing Segment");
        Match(index.FindAt(S(49)), null, Array.Empty<string>(),
            "the sample immediately before B is unallocated");
        Match(index.FindAt(S(50)), "B", ["first"],
            "B starts inclusively at its first sample");
        Match(index.FindAt(S(60)), "B", ["middle", "middle-2"],
            "equal-time anchors are all found, preserving insertion order");
        Match(index.FindAt(S(61)), "B", Array.Empty<string>(),
            "assigned sample without a tile is not a missing Segment");
        Match(index.FindAt(S(79)), "B", ["late"],
            "last tile before B's right boundary belongs to B");
        Match(index.FindAt(S(80)), "C", ["next-segment"],
            "the exact B/C boundary belongs to C only");
        Match(index.FindAt(S(120)), null, Array.Empty<string>(),
            "the final allocation end is exclusive");
        Match(index.FindAt(-1), null, Array.Empty<string>(),
            "a negative sample is unallocated");

        Range(index.FindRange("B", S(50), S(80)),
            ["first", "middle", "middle-2", "late"],
            "range within B includes every marker, but not the first C tile");
        Range(index.FindRange("B", S(60), S(61)),
            ["middle", "middle-2"], "exact equal-time run is preserved");
        Range(index.FindRange("B", S(50), S(60)),
            ["first"], "right endpoint 60 seconds is excluded");
        Range(index.FindRange("B", S(61), S(70)),
            [], "empty query interval within B is valid");
        Range(index.FindRange("C", S(80), S(90)),
            ["next-segment"], "right-hand owner starts at the cut");

        // The index must never silently search two Segments as one global
        // monotone run. Caller must split queries at time ownership cuts.
        ExpectRejected(() => index.FindRange("B", S(79), S(81)),
            "range crossing B/C boundary rejected");
        ExpectRejected(() => index.FindRange("B", S(49), S(51)),
            "range crossing gap/B boundary rejected");
        ExpectRejected(() => index.FindRange("B", S(50), S(50)),
            "empty search range is rejected");
        ExpectRejected(() => index.FindRange("B", S(80), S(90)),
            "range starting at B's exclusive end is rejected");
        ExpectRejected(() => index.FindRange("D", S(50), S(60)),
            "unknown Segment cannot be searched");

        // A snapshot is frozen: changes to the *caller's* draft dictionary
        // after Build must not alter the index or leak a writable array.
        drafts["B"] = SegmentTimeDraftEditor.RemoveAnchor(b, "middle");
        Match(index.FindAt(S(60)), "B", ["middle", "middle-2"],
            "post-build author edits do not mutate an old search index");
        SegmentDraftTimeMatch frozen = index.FindAt(S(60));
        if (frozen.Anchors is IList<SegmentDraftTileAnchor> list)
            Check(list.IsReadOnly, "index must not expose a writable marker collection");

        // Fail closed if Build sees stale ownership or broken monotonicity.
        var missing = new Dictionary<string, SegmentTimeDraft>(drafts,
            StringComparer.Ordinal);
        missing.Remove("B");
        ExpectRejected(() => SegmentDraftTimeSearchIndex.Build(timeline, missing),
            "missing active draft must not be treated as a legitimate empty section");

        var stale = new Dictionary<string, SegmentTimeDraft>(drafts,
            StringComparer.Ordinal)
        {
            ["B"] = b with { StartSample = S(55) }
        };
        ExpectRejected(() => SegmentDraftTimeSearchIndex.Build(timeline, stale),
            "draft whose ownership bounds have shifted is rejected");

        var wrongOwner = new Dictionary<string, SegmentTimeDraft>(drafts,
            StringComparer.Ordinal)
        {
            ["B"] = b with { OwnerId = "author-A" }
        };
        ExpectRejected(() => SegmentDraftTimeSearchIndex.Build(timeline, wrongOwner),
            "draft whose owner differs from allocation is rejected");

        var unordered = new Dictionary<string, SegmentTimeDraft>(drafts,
            StringComparer.Ordinal)
        {
            ["B"] = b with
            {
                TileAnchors =
                [
                    new SegmentDraftTileAnchor("later", S(79)),
                    new SegmentDraftTileAnchor("earlier", S(60))
                ]
            }
        };
        ExpectRejected(() => SegmentDraftTimeSearchIndex.Build(timeline, unordered),
            "binary search requires monotone samples within each Segment");

        var duplicate = new Dictionary<string, SegmentTimeDraft>(drafts,
            StringComparer.Ordinal)
        {
            ["B"] = b with
            {
                TileAnchors =
                [
                    new SegmentDraftTileAnchor("same", S(60)),
                    new SegmentDraftTileAnchor("same", S(60))
                ]
            }
        };
        ExpectRejected(() => SegmentDraftTimeSearchIndex.Build(timeline, duplicate),
            "draft tile IDs must be unique even when times are equal");

        var wrongRate = new Dictionary<string, SegmentTimeDraft>(drafts,
            StringComparer.Ordinal)
        {
            ["B"] = b with { SampleRate = 44_100 }
        };
        ExpectRejected(() => SegmentDraftTimeSearchIndex.Build(timeline, wrongRate),
            "draft sample rate must match the common audio timeline");

        var outside = new Dictionary<string, SegmentTimeDraft>(drafts,
            StringComparer.Ordinal)
        {
            ["B"] = b with
            {
                TileAnchors =
                [
                    new SegmentDraftTileAnchor("outside", S(80))
                ]
            }
        };
        ExpectRejected(() => SegmentDraftTimeSearchIndex.Build(timeline, outside),
            "draft anchors must remain inside their half-open allocation");

        var revised = timeline with
        {
            Partitions = timeline.Partitions.Select(p => p.SegmentId == "B"
                ? p with { ContentRevisionId = "existing-authored-revision" }
                : p).ToArray()
        };
        ExpectRejected(() => SegmentDraftTimeSearchIndex.Build(revised,
            new Dictionary<string, SegmentTimeDraft>(StringComparer.Ordinal)
            {
                ["A"] = a, ["B"] = b, ["C"] = c
            }),
            "draft-only search must not mistake an authored floor revision for empty");

        var unowned = timeline with
        {
            Partitions = timeline.Partitions.Select(p => p.SegmentId == "B"
                ? p with { OwnerId = null }
                : p).ToArray()
        };
        ExpectRejected(() => SegmentDraftTimeSearchIndex.Build(unowned,
                new Dictionary<string, SegmentTimeDraft>(StringComparer.Ordinal)
                {
                    ["A"] = a, ["B"] = b, ["C"] = c
                }),
            "active allocations without editing authority are rejected");

        Check(originalTimeline == JsonSerializer.Serialize(timeline),
            "index must never mutate source time allocations");
        Check(originalDrafts == JsonSerializer.Serialize(
            new Dictionary<string, SegmentTimeDraft>(StringComparer.Ordinal)
            {
                ["A"] = a, ["B"] = b, ["C"] = c
            }), "index construction must never mutate supplied draft snapshots");
    }

    private static void Match(SegmentDraftTimeMatch result,
        string? expectedOwner, string[] expectedIds, string label)
    {
        Check(result.SegmentId == expectedOwner,
            label + ": unexpected Segment " + result.SegmentId);
        Range(result.Anchors, expectedIds, label);
    }

    private static void Range(IEnumerable<SegmentDraftTileAnchor> anchors,
        string[] expectedIds, string label)
    {
        Check(anchors.Select(a => a.TileId).SequenceEqual(expectedIds),
            label + ": returned unexpected tile markers");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void ExpectRejected(Action action, string label)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException(
            "Unsafe time-index operation accepted: " + label);
    }
}
