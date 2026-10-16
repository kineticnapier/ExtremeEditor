using System.Net;
using System.Net.Http.Headers;

namespace ExtremeEditor.Core;

/// <summary>
/// Git-bundle transport for a conditional, opaque HTTPS object endpoint.
/// Contract: GET returns 404 or a bundle + a strong ETag; PUT requires
/// If-None-Match: * for creation, or If-Match: ETag for replacement.
/// The server MUST atomically enforce these preconditions (412 on mismatch).
/// It stores no audio and need not understand or execute Git.
/// </summary>
public sealed class HttpBundleTimeFirstSegmentRemote : ITimeFirstSegmentRemote
{
    private const int MaxBundleBytes = 32 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _token;

    public HttpBundleTimeFirstSegmentRemote(HttpClient http, Uri endpoint, string token)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (endpoint is null || !(endpoint.Scheme == Uri.UriSchemeHttps ||
              (endpoint.Scheme == Uri.UriSchemeHttp &&
               (endpoint.Host == "127.0.0.1" || endpoint.Host == "::1" ||
                endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))) ||
            !endpoint.IsAbsoluteUri || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("A plain HTTPS bundle endpoint is required.", nameof(endpoint));
        if (string.IsNullOrWhiteSpace(token) || token.Contains('\r') || token.Contains('\n'))
            throw new ArgumentException("An access token is required.", nameof(token));
        _endpoint = endpoint;
        _token = token;
    }

    public string Fetch(TimeFirstSegmentGitWorkspace workspace, string branchName)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        SegmentGitWorkspace.ValidateBranchName(branchName);
        (byte[]? bundle, _) = Download();
        if (bundle is null)
            throw new InvalidOperationException("The shared project has not been published yet.");
        return Import(workspace, branchName, bundle);
    }

    public string Push(TimeFirstSegmentGitWorkspace workspace, string branchName)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        SegmentGitWorkspace.ValidateBranchName(branchName);
        if (workspace.CurrentBranch != branchName)
            throw new InvalidOperationException("Push must target the checked-out branch.");
        workspace.AssertCleanOwnedWorktree();
        string local = workspace.HeadCommit;
        _ = workspace.ReadAt(local);
        (byte[]? previous, EntityTagHeaderValue? etag) = Download();
        if (previous is not null)
        {
            string remote = Import(workspace, branchName, previous);
            // Any divergence (including a remote advance) is a non-FF push.
            // ExecuteGit raises on the nonzero result of --is-ancestor.
            workspace.Transport.ExecuteGit("merge-base", "--is-ancestor", remote, local);
        }
        string temporary = TemporaryBundle();
        try
        {
            workspace.Transport.ExecuteGit("bundle", "create", temporary,
                "refs/heads/" + branchName);
            byte[] data = File.ReadAllBytes(temporary);
            if (data.Length > MaxBundleBytes)
                throw new InvalidOperationException("The Git bundle exceeds the 32 MiB transfer limit.");
            using var request = NewRequest(HttpMethod.Put);
            request.Content = new ByteArrayContent(data);
            request.Content.Headers.ContentType =
                new MediaTypeHeaderValue("application/octet-stream");
            if (previous is null)
                request.Headers.TryAddWithoutValidation("If-None-Match", "*");
            else
                request.Headers.IfMatch.Add(etag!);
            using HttpResponseMessage response = _http.Send(request);
            if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
                throw new InvalidOperationException("The shared project changed; fetch and merge before retrying.");
            response.EnsureSuccessStatusCode();
            return local;
        }
        finally { File.Delete(temporary); }
    }

    private (byte[]? Data, EntityTagHeaderValue? ETag) Download()
    {
        using var request = NewRequest(HttpMethod.Get);
        using HttpResponseMessage response = _http.Send(request);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return (null, null);
        response.EnsureSuccessStatusCode();
        EntityTagHeaderValue? tag = response.Headers.ETag;
        if (tag is null || tag.IsWeak)
            throw new InvalidDataException("The bundle server must return a strong ETag.");
        if (response.Content.Headers.ContentLength > MaxBundleBytes)
            throw new InvalidDataException("Remote Git bundle exceeds transfer limit.");
        byte[] data = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        if (data.Length > MaxBundleBytes)
            throw new InvalidDataException("Remote Git bundle exceeds transfer limit.");
        return (data, tag);
    }

    private string Import(TimeFirstSegmentGitWorkspace workspace, string branchName, byte[] data)
    {
        string temporary = TemporaryBundle();
        try
        {
            File.WriteAllBytes(temporary, data);
            workspace.Transport.ExecuteGit("bundle", "verify", temporary);
            workspace.Transport.ExecuteGit("fetch", "--no-tags", "--", temporary,
                "+refs/heads/" + branchName + ":refs/remotes/origin/" + branchName);
            string sha = workspace.Transport.ExecuteGit("rev-parse", "--verify",
                "refs/remotes/origin/" + branchName + "^{commit}").Trim();
            _ = workspace.ReadAt(sha); // never accept malformed time-first snapshots
            return sha;
        }
        finally { File.Delete(temporary); }
    }

    private HttpRequestMessage NewRequest(HttpMethod method)
    {
        var request = new HttpRequestMessage(method, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    private static string TemporaryBundle() =>
        Path.Combine(Path.GetTempPath(), "extremeeditor-" + Guid.NewGuid().ToString("N") + ".bundle");
}
