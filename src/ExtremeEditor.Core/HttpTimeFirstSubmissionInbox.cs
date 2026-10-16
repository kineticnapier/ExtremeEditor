using System.Net;
using System.Net.Http.Headers;

namespace ExtremeEditor.Core;

/// <summary>
/// Downloads an untrusted participant proposal into a quarantined Git ref.
/// This API never changes HEAD, a worktree, or refs/remotes/origin/*.
/// A fetched proposal is NOT accepted as an authorized segment edit.
/// </summary>
public sealed class HttpTimeFirstSubmissionInbox
{
    private const int MaxBytes = 32 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly Uri _roomRoot;
    private readonly string _token;

    public HttpTimeFirstSubmissionInbox(HttpClient http, Uri roomRoot, string memberToken)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (roomRoot is null || !roomRoot.IsAbsoluteUri ||
            roomRoot.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(roomRoot.UserInfo) ||
            !string.IsNullOrEmpty(roomRoot.Fragment) ||
            !roomRoot.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("An HTTPS room URL ending in '/' is required.", nameof(roomRoot));
        if (string.IsNullOrWhiteSpace(memberToken) || memberToken.Contains('\r') ||
            memberToken.Contains('\n'))
            throw new ArgumentException("A member token is required.", nameof(memberToken));
        _roomRoot = roomRoot;
        _token = memberToken;
    }

    public string FetchProposal(TimeFirstSegmentGitWorkspace workspace, string memberId,
        string branchName = "main")
    {
        ArgumentNullException.ThrowIfNull(workspace);
        SegmentGitWorkspace.ValidateBranchName(branchName);
        if (string.IsNullOrEmpty(memberId) || memberId.Length > 64 ||
            !memberId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            throw new ArgumentException("Invalid member ID.", nameof(memberId));

        // No worktree, HEAD, canonical remote ref, or local branch is modified.
        var target = new Uri(_roomRoot, "submissions/" + memberId);
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var response = _http.Send(request);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new FileNotFoundException("This participant has not submitted a proposal.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxBytes)
            throw new InvalidDataException("Submission exceeds the 32 MiB limit.");
        byte[] data = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        if (data.Length > MaxBytes)
            throw new InvalidDataException("Submission exceeds the 32 MiB limit.");

        string temp = Path.Combine(Path.GetTempPath(),
            "extremeeditor-submission-" + Guid.NewGuid().ToString("N") + ".bundle");
        try
        {
            File.WriteAllBytes(temp, data);
            workspace.Transport.ExecuteGit("bundle", "verify", temp);
            // Keep the fetched objects out of canonical refs until fully verified.
            // FETCH_HEAD is Git-internal scratch state; HEAD itself is unchanged.
            workspace.Transport.ExecuteGit("fetch", "--no-tags", "--", temp,
                "refs/heads/" + branchName);
            string sha = workspace.Transport.ExecuteGit("rev-parse", "--verify",
                "FETCH_HEAD^{commit}").Trim();
            _ = workspace.ReadAt(sha);
            string quarantineRef = "refs/extremeeditor/submissions/" + memberId + "/" + branchName;
            workspace.Transport.ExecuteGit("update-ref", quarantineRef, sha);
            return sha;
        }
        finally { File.Delete(temp); }
    }
}
