using System.Text.Json;

namespace ExtremeEditor.Core;

/// <summary>Fail-closed validation of an untrusted proposal against an explicitly
/// trusted base commit. Does not checkout, merge, or change any Git ref.</summary>
public static class TimeFirstSubmissionValidator
{
    public static void Validate(TimeFirstSegmentGitWorkspace workspace,
        string trustedBaseCommit, string proposedCommit, string memberId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (string.IsNullOrWhiteSpace(memberId))
            throw new ArgumentException("Member ID required.", nameof(memberId));

        var git = workspace.Transport;
        string baseline = git.ExecuteGit("rev-parse", "--verify",
            trustedBaseCommit + "^{commit}").Trim();
        string proposal = git.ExecuteGit("rev-parse", "--verify",
            proposedCommit + "^{commit}").Trim();
        if (baseline == proposal)
            throw new InvalidOperationException("Submission contains no new commit.");
        // Do not accept rewritten history or a different starting project.
        git.ExecuteGit("merge-base", "--is-ancestor", baseline, proposal);

        TimeFirstSegmentSnapshot before = workspace.ReadAt(baseline);
        TimeFirstSegmentSnapshot after = workspace.ReadAt(proposal);
        TimeFirstSegmentGitWorkspace.ValidateSnapshot(after);

        static string Canonical(object? value) => JsonSerializer.Serialize(value);
        if (Canonical(before.Audio) != Canonical(after.Audio) ||
            Canonical(before.TimingPoints) != Canonical(after.TimingPoints) ||
            Canonical(before.Timeline) != Canonical(after.Timeline))
            throw new InvalidOperationException("Submission changes protected project metadata.");

        var permitted = before.Timeline.Partitions
            .Where(p => p.OwnerId == memberId && p.Status == SegmentPartitionStatus.Active)
            .Select(p => p.SegmentId).ToHashSet(StringComparer.Ordinal);
        if (permitted.Count == 0)
            throw new InvalidOperationException("Member has no assigned segment in trusted base.");

        var ids = before.Content.Keys.Union(after.Content.Keys, StringComparer.Ordinal);
        foreach (string id in ids)
        {
            if (permitted.Contains(id)) continue;
            if (!before.Content.TryGetValue(id, out var bc) ||
                !after.Content.TryGetValue(id, out var ac) ||
                !before.Drafts.TryGetValue(id, out var bd) ||
                !after.Drafts.TryGetValue(id, out var ad) ||
                Canonical(bc) != Canonical(ac) || Canonical(bd) != Canonical(ad) ||
                Canonical(before.Authored?.GetValueOrDefault(id)) !=
                Canonical(after.Authored?.GetValueOrDefault(id)))
                throw new InvalidOperationException("Submission modifies another member's segment: " + id);
        }

        // Protect against unrecognized files in Git trees, including audio,
        // hidden payloads, and additional executable content.
        string diff = git.ExecuteGit("diff", "--name-only", "--no-renames",
            baseline, proposal);
        foreach (string path in diff.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string normalized = path.TrimEnd('\r');
            if (!normalized.StartsWith("time-first-segments/", StringComparison.Ordinal) ||
                !normalized.EndsWith(".json", StringComparison.Ordinal))
                throw new InvalidOperationException("Submission touches a protected Git path.");
            string id = normalized["time-first-segments/".Length..^".json".Length];
            if (!permitted.Contains(id) || id.Contains('/'))
                throw new InvalidOperationException("Submission touches an unassigned Git path.");
        }
    }
}
