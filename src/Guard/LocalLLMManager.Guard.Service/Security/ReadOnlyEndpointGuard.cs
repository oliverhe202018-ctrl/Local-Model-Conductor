using System.Net;

namespace LocalLLMManager.Guard.Service.Security;

/// <summary>
/// Inherited from LocalLLMManager.Core.Utilities.ReadOnlyEndpointGuard (Phase 3.1).
/// Strictly enforces loopback-only access and source-audited llama-swap v275 read-only endpoints.
/// Blocks GET /upstream/{model}/* which triggers on-demand model loading in llama-swap v275.
/// </summary>
public static class ReadOnlyEndpointGuard
{
    private static readonly string[] AllowedReadOnlyPaths =
    [
        "/health",
        "/v1/models",
        "/models",
        "/running",
        "/props",
        "/slots",
        "/api/version",
        "/api/profiles",
        "/api/events",
        "/logs",
        "/logs/stream",
        "/logs/stream/proxy",
        "/logs/stream/upstream",
        "/logs/stream/http"
    ];

    public static bool IsStrictLoopbackHttpUrl(string? url, out Uri? parsed, out string reason)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            reason = "Invalid or empty URL.";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Unsupported scheme '{uri.Scheme}'. Only loopback http:// is allowed.";
            return false;
        }

        var host = uri.Host;
        var isLoopback = string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

        if (!isLoopback)
        {
            reason = $"Non-loopback host '{host}' rejected.";
            return false;
        }

        parsed = uri;
        reason = "OK";
        return true;
    }

    public static bool IsAllowedReadOnlyGetPath(string? relativePathOrUrl, out string reason)
    {
        if (string.IsNullOrWhiteSpace(relativePathOrUrl))
        {
            reason = "REJECTED: empty path.";
            return false;
        }

        var path = relativePathOrUrl.Trim();
        if (Uri.TryCreate(path, UriKind.Absolute, out var absUri))
        {
            if (!IsStrictLoopbackHttpUrl(path, out _, out var loopErr))
            {
                reason = $"REJECTED_NON_LOOPBACK: {loopErr}";
                return false;
            }
            path = absUri.AbsolutePath;
        }
        else
        {
            var qIdx = path.IndexOf('?');
            if (qIdx >= 0) path = path[..qIdx];
        }

        if (!path.StartsWith('/'))
            path = "/" + path;

        if (path.StartsWith("/upstream", StringComparison.OrdinalIgnoreCase))
        {
            reason = "FORBIDDEN_SIDE_EFFECT: llama-swap v275 GET /upstream/{model}/* triggers on-demand model loading.";
            return false;
        }

        if (path.StartsWith("/api/models/unload", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/models/pause", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/models/resume", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/profiles/active", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/unload", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"FORBIDDEN_MUTATING_ENDPOINT: {path} is not read-only.";
            return false;
        }

        foreach (var allowed in AllowedReadOnlyPaths)
        {
            if (string.Equals(path, allowed, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"ALLOWED_VERIFIED_READ_ONLY: {path}";
                return true;
            }
        }

        if (path.StartsWith("/logs/stream/", StringComparison.OrdinalIgnoreCase) &&
            path.Length > "/logs/stream/".Length &&
            !path.Contains("..", StringComparison.Ordinal))
        {
            reason = $"ALLOWED_VERIFIED_READ_ONLY_LOG_STREAM: {path}";
            return true;
        }

        reason = $"REJECTED_UNAUDITED_ENDPOINT: {path} is not in the source-verified read-only whitelist.";
        return false;
    }
}
