using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;
using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

internal static class TimeFirstSegmentGitRegression
{
    private const int Rate = 48_000;

    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(),
            $"extremeeditor-time-git-{Guid.NewGuid():N}");
        string aPath = Path.Combine(root, "A");
        string bPath = Path.Combine(root, "B");
        string remote = Path.Combine(root, "shared.git");
        Directory.CreateDirectory(root);
        try
        {
            RunGit(root, "init", "--bare", "--initial-branch=main", remote);
            TimeFirstSegmentGitWorkspace a =
                TimeFirstSegmentGitWorkspace.Initialize(aPath);
            a.Save(Fixture());
            a.Commit("base");
            TimeFirstSegmentGitRemoteSync.ConnectLocalBare(a, remote);
            var monitoredRemote = new CountingRemote();
            TimeFirstSegmentGitRemoteSync.Push(a, remote: monitoredRemote);
            Check(monitoredRemote.PushCount == 1, "injected remote handles Push");

            TimeFirstSegmentGitWorkspace b =
                TimeFirstSegmentGitRemoteSync.CloneLocalBare(remote, bPath);
            string common = a.HeadCommit;

            a.Save(Edit(a.Load(), "A", "alice", "A-1", "a-tile", Rate * 2L, 0));
            string aCommit = a.Commit("Alice edits A");
            TimeFirstSegmentGitRemoteSync.Push(a);

            b.Save(Edit(b.Load(), "B", "bob", "B-1", "b-tile", Rate * 12L, 90));
            string bCommit = b.Commit("Bob edits B");
            Check(bCommit != common && aCommit != common, "independent local histories");
            TimeFirstSegmentPullResult merged = TimeFirstSegmentGitRemoteSync.Pull(b, remote: monitoredRemote);
            Check(monitoredRemote.FetchCount == 1,
                "semantic Pull fetches through the injected remote");
            Check(merged.Success, "different Segment edits merge semantically");
            Check(b.Load().Authored?.Keys.Order().SequenceEqual(["A", "B"]) == true,
                "both authors' real tile edits survive");
            string[] parents = RunGit(bPath, "show", "-s", "--format=%P", "HEAD")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Check(parents.Length == 2, "divergent Pull creates two-parent history");
            TimeFirstSegmentGitRemoteSync.Push(b);
            Check(TimeFirstSegmentGitRemoteSync.Pull(a).Success,
                "first client receives merged history");

            a.Save(Edit(a.Load(), "A", "alice", "A-2", "a-second", Rate * 3L, 180));
            a.Commit("Alice edits A again");
            TimeFirstSegmentGitRemoteSync.Push(a);

            string bBeforeConflict = b.HeadCommit;
            b.Save(Edit(b.Load(), "A", "alice", "A-alt", "a-alt", Rate * 4L, 270));
            b.Commit("Concurrent edit to A");
            string bLocal = b.HeadCommit;
            TimeFirstSegmentPullResult conflict = TimeFirstSegmentGitRemoteSync.Pull(b);
            Check(!conflict.Success && conflict.ContentConflictSegmentIds.SequenceEqual(["A"]),
                "same Segment edit is explicit conflict");
            Check(b.HeadCommit == bLocal && b.HeadCommit != bBeforeConflict,
                "conflict preserves local HEAD and history");
            Check(b.Load().Authored?["A"].RevisionId == "A-alt",
                "conflict preserves local authored payload");

            TimeFirstSegmentSnapshot dirty = b.Load();
            b.Save(dirty with { Audio = dirty.Audio with { FileName = "dirty.ogg" } });
            ExpectRejected(() => TimeFirstSegmentGitRemoteSync.Pull(b),
                "uncommitted owned data blocks Pull");
            Check(b.Load().Audio.FileName == "dirty.ogg",
                "dirty data is not discarded");
        }
        finally
        {
            CleanupRoot(root);
        }
        RunLoopbackTransportPolicy();
        RunHttpBundleRemote();
        RunSubmissionInbox();
        RunSubmissionValidation();
    }

    private static void RunLoopbackTransportPolicy()
    {
        using var client = new HttpClient();
        foreach (string url in new[]
        {
            "http://127.0.0.1:8787/rooms/example12345/",
            "http://localhost:8787/rooms/example12345/",
            "http://[::1]:8787/rooms/example12345/",
            "https://relay.example.net/rooms/example12345/"
        })
            _ = new HttpTimeFirstSubmissionInbox(client, new Uri(url), "secret");

        foreach (string url in new[]
        {
            "http://relay.example.net/rooms/example12345/",
            "http://192.168.1.1:8787/rooms/example12345/",
            "http://127.0.0.2:8787/rooms/example12345/"
        })
            try
            {
                _ = new HttpTimeFirstSubmissionInbox(client, new Uri(url), "secret");
                throw new InvalidOperationException("Unsafe HTTP endpoint allowed: " + url);
            }
            catch (ArgumentException) { }
    }

    private sealed class MemoryBundleServer : HttpMessageHandler
    {
        private byte[]? _bundle;
        private int _version;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(Send(request, cancellationToken));

        protected override HttpResponseMessage Send(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization?.Parameter != "test-token")
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            if (request.Method == HttpMethod.Get)
            {
                if (_bundle is null) return new HttpResponseMessage(HttpStatusCode.NotFound);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_bundle),
                    Headers = { ETag = new EntityTagHeaderValue($"\"{_version}\"") }
                };
            }
            if (request.Method != HttpMethod.Put)
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            bool valid = _bundle is null
                ? request.Headers.IfNoneMatch.Any(x => x.Tag == "*")
                : request.Headers.IfMatch.Any(x => x.Tag == $"\"{_version}\"");
            if (!valid) return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
            _bundle = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            _version++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static void RunHttpBundleRemote()
    {
        string root = Path.Combine(Path.GetTempPath(), $"extreme-http-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var client = new HttpClient(new MemoryBundleServer());
            var remote = new HttpBundleTimeFirstSegmentRemote(
                client, new Uri("https://example.invalid/rooms/test/bundle"), "test-token");
            var a = TimeFirstSegmentGitWorkspace.Initialize(Path.Combine(root, "A"));
            a.Save(Fixture());
            a.Commit("base");
            Check(remote.Push(a, "main") == a.HeadCommit, "HTTP first publish");
            var b = TimeFirstSegmentGitWorkspace.Initialize(Path.Combine(root, "B"));
            string fetched = remote.Fetch(b, "main");
            RunGit(Path.Combine(root, "B"), "reset", "--hard", fetched);
            Check(b.Load().Timeline.Partitions.Count == 2, "HTTP first download");
            a.Save(Edit(a.Load(), "A", "alice", "http-a", "a-http", Rate * 2L, 0));
            a.Commit("A offline edit");
            b.Save(Edit(b.Load(), "B", "bob", "http-b", "b-http", Rate * 12L, 90));
            b.Commit("B offline edit");
            remote.Push(a, "main");
            ExpectRejected(() => remote.Push(b, "main"), "HTTP stale push rejected");
            Check(TimeFirstSegmentGitRemoteSync.Pull(b, remote: remote).Success,
                "HTTP transport semantic merge");
            remote.Push(b, "main");
            Check(TimeFirstSegmentGitRemoteSync.Pull(a, remote: remote).Success,
                "HTTP transport merged version distributes");
            Check(a.Load().Authored?.Count == 2, "HTTP both owners edits present");
        }
        finally { CleanupRoot(root); }
    }

    private sealed class SubmissionHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));

        protected override HttpResponseMessage Send(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get ||
                request.RequestUri?.AbsolutePath != "/rooms/testroom1234/submissions/alice" ||
                request.Headers.Authorization?.Parameter != "guest-token")
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
        }
    }

    private sealed class SubmissionUploadHandler : HttpMessageHandler
    {
        public int PutCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));

        protected override HttpResponseMessage Send(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath !=
                "/rooms/testroom1234/submissions/alice" ||
                request.Headers.Authorization?.Parameter != "guest-token")
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method != HttpMethod.Put ||
                !request.Headers.IfNoneMatch.Any(item => item.Tag == "*"))
                return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
            byte[] submitted = request.Content!.ReadAsByteArrayAsync()
                .GetAwaiter().GetResult();
            if (!System.Text.Encoding.ASCII.GetString(submitted, 0,
                    Math.Min(16, submitted.Length)).StartsWith("# v2 git bundle",
                    StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            PutCount++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static void RunSubmissionInbox()
    {
        string root = Path.Combine(Path.GetTempPath(), $"extreme-inbox-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string origin = Path.Combine(root, "origin");
            var alice = TimeFirstSegmentGitWorkspace.Initialize(origin);
            alice.Save(Fixture());
            alice.Commit("base");
            alice.Save(Edit(alice.Load(), "A", "alice", "proposed", "tile-proposed",
                Rate * 3L, 0));
            string proposed = alice.Commit("Alice proposal");
            string bundle = Path.Combine(root, "alice.bundle");
            RunGit(origin, "bundle", "create", bundle, "refs/heads/main");

            string clone = Path.Combine(root, "clone");
            var receiver = TimeFirstSegmentGitWorkspace.Initialize(clone);
            receiver.Save(Fixture());
            receiver.Commit("local independent commit");
            string headBefore = receiver.HeadCommit;
            string projectBefore = System.Text.Json.JsonSerializer.Serialize(receiver.Load());
            string? remoteBefore = null;
            try { remoteBefore = RunGit(clone, "rev-parse", "refs/remotes/origin/main"); }
            catch (InvalidOperationException) { }

            using var client = new HttpClient(new SubmissionHandler(File.ReadAllBytes(bundle)));
            var inbox = new HttpTimeFirstSubmissionInbox(client,
                new Uri("https://example.invalid/rooms/testroom1234/"), "guest-token");
            Check(inbox.FetchProposal(receiver, "alice") == proposed,
                "quarantined proposal commit imported");
            Check(receiver.HeadCommit == headBefore, "proposal fetch does not alter HEAD");
            Check(RunGit(clone, "rev-parse",
                "refs/extremeeditor/submissions/alice/main") == proposed,
                "proposal stored under isolated ref");
            string? remoteAfter = null;
            try { remoteAfter = RunGit(clone, "rev-parse", "refs/remotes/origin/main"); }
            catch (InvalidOperationException) { }
            Check(remoteBefore == remoteAfter, "canonical remote ref unchanged");
            Check(System.Text.Json.JsonSerializer.Serialize(receiver.Load()) == projectBefore,
                "proposal fetch does not change local project files");
            var uploadHandler = new SubmissionUploadHandler();
            using var uploaderClient = new HttpClient(uploadHandler);
            var uploader = new HttpTimeFirstSubmissionInbox(uploaderClient,
                new Uri("https://example.invalid/rooms/testroom1234/"), "guest-token");
            Check(uploader.SubmitProposal(receiver, "alice") == headBefore,
                "offline committed proposal uploaded");
            Check(uploadHandler.PutCount == 1, "proposal uploads once with create precondition");
            Check(receiver.HeadCommit == headBefore, "proposal upload preserves HEAD");
        }
        finally { CleanupRoot(root); }
    }

    private static void RunSubmissionValidation()
    {
        string root = Path.Combine(Path.GetTempPath(),
            $"extreme-proposal-validation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var ws = TimeFirstSegmentGitWorkspace.Initialize(Path.Combine(root, "ws"));
            ws.Save(Fixture());
            string baseline = ws.Commit("trusted baseline");

            ws.Save(Edit(ws.Load(), "A", "alice", "valid-A", "alice-tile", Rate * 2L, 0));
            string valid = ws.Commit("Alice proposes A");
            TimeFirstSubmissionValidator.Validate(ws, baseline, valid, "alice");
            ExpectRejected(() => TimeFirstSubmissionValidator.Validate(
                ws, baseline, valid, "bob"), "Bob cannot submit Alice's edit");

            ws.Save(Edit(ws.Load(), "B", "bob", "bad-B", "bob-tile", Rate * 12L, 0));
            string outside = ws.Commit("Also changes B");
            ExpectRejected(() => TimeFirstSubmissionValidator.Validate(
                ws, baseline, outside, "alice"), "Alice cannot submit Bob's edit");

            RunGit(ws.Directory, "reset", "--hard", baseline);
            ws.Save(Edit(ws.Load(), "B", "bob", "local-B", "local-bob", Rate * 12L, 0));
            ws.Commit("Bob's independent edit");
            RunGit(ws.Directory, "update-ref",
                "refs/extremeeditor/submissions/alice/main", valid);
            string canonicalBefore = ws.HeadCommit;
            TimeFirstSegmentPullResult accepted =
                TimeFirstSegmentGitRemoteSync.MergeSubmission(ws, baseline, "alice");
            Check(accepted.Success && ws.Load().Authored?.Count == 2,
                "valid Alice proposal merges with Bob's independent edit");
            Check(ws.HeadCommit != canonicalBefore,
                "accepted proposal creates the merged local commit");
            ExpectRejected(() => TimeFirstSegmentGitRemoteSync.MergeSubmission(
                ws, baseline, "bob"), "cannot claim another participant's proposal");

            RunGit(ws.Directory, "reset", "--hard", baseline);
            var altered = ws.Load();
            ws.Save(altered with { Audio = altered.Audio with { FileName = "other.ogg" } });
            string alteredMetadata = ws.Commit("Change audio identity");
            ExpectRejected(() => TimeFirstSubmissionValidator.Validate(
                ws, baseline, alteredMetadata, "alice"), "Audio identity protected");
        }
        finally { CleanupRoot(root); }
    }

    private sealed class CountingRemote : ITimeFirstSegmentRemote
    {
        public int FetchCount { get; private set; }
        public int PushCount { get; private set; }

        public string Fetch(TimeFirstSegmentGitWorkspace workspace, string branchName)
        {
            FetchCount++;
            return LocalBareTimeFirstSegmentRemote.Instance.Fetch(workspace, branchName);
        }

        public string Push(TimeFirstSegmentGitWorkspace workspace, string branchName)
        {
            PushCount++;
            return LocalBareTimeFirstSegmentRemote.Instance.Push(workspace, branchName);
        }
    }

    private static TimeFirstSegmentSnapshot Edit(
        TimeFirstSegmentSnapshot snapshot, string segmentId, string owner,
        string revision, string tileId, long sample, double angle)
    {
        var editor = TimeFirstSegmentEditorSession.Open(snapshot, segmentId, owner);
        editor.PlaceTile(tileId, sample, angle);
        return editor.CreateRevision(snapshot, revision);
    }

    private static TimeFirstSegmentSnapshot Fixture()
    {
        SegmentTimeTimeline timeline = new(Rate, []);
        timeline = SegmentTimePlanner.CreateEmpty(timeline, "A", 0, Rate * 10L, "alice");
        timeline = SegmentTimePlanner.CreateEmpty(timeline, "B", Rate * 10L, Rate * 20L, "bob");
        var content = timeline.Partitions.ToDictionary(
            p => p.SegmentId, SegmentContentPlanner.CreateEmpty, StringComparer.Ordinal);
        var drafts = timeline.Partitions.ToDictionary(
            p => p.SegmentId,
            p => SegmentTimeDraftEditor.Open(timeline, content, p.SegmentId, p.OwnerId!),
            StringComparer.Ordinal);
        return new TimeFirstSegmentSnapshot(
            new TimeFirstAudioIdentity("song.ogg", new string('c', 64), Rate, Rate * 20L),
            [new MusicTimingPoint(0, 120m)], timeline, content, drafts);
    }

    private static string RunGit(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Git fixture failed: " + error);
        return output.Trim();
    }

    private static void CleanupRoot(string root)
    {
        if (!Directory.Exists(root)) return;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count != 0)
        {
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Refusing linked fixture entry.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
            }
        }
        Directory.Delete(root, recursive: true);
    }

    private static void ExpectRejected(Action action, string label)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Unsafe Git operation accepted: " + label);
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
