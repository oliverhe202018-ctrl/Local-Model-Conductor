using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace LocalLLMManager.Guard.Service.Security;

public sealed class GuardInferenceFacadeOptions
{
    public string InternalLlamaSwapBaseUrl { get; init; } = "http://127.0.0.1:19259";
    public string InternalLlamaSwapAdminKey { get; init; } = string.Empty;
    public string AgentInferenceApiKey { get; init; } = string.Empty;
    public bool RequireAgentInferenceApiKey { get; init; } = true;
}

/// <summary>
/// P0 Management API Authority Isolation Facade.
///
/// Sits on the fixed client-facing OpenAI loopback port (isolated: 19250; future prod: 19000) and strictly forwards
/// ONLY audited inference + read-only discovery endpoints to internal llama-swap v275 (locked with Guard's private
/// 256-bit key).
///
/// CRITICAL GUARANTEES:
/// 1. ZERO second request queue: streams HTTP/SSE chunks 1:1 immediately via HttpCompletionOption.ResponseHeadersRead
///    with response buffering disabled so llama-swap v275's native FIFO queue & SSE Queue position heartbeats work unmodified.
/// 2. HARD-REJECTS ALL management/admin/side-effect routes (/api/*, /unload, /upstream/*, /ui/*, /logs*) with HTTP 403/401,
///    regardless of whether the caller is unauthenticated OR presents a valid Agent inference API key.
/// 3. Agent Inference API key is strictly separate from Guard's internal llama-swap secret and is stripped before forwarding.
/// </summary>
public sealed class GuardInferenceFacadeMiddleware : IDisposable
{
    private static readonly HashSet<string> AllowedGetPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/health",
        "/v1/models",
        "/models"
    };

    private static readonly HashSet<string> AllowedPostPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/v1/chat/completions",
        "/v1/completions",
        "/v1/responses",
        "/v1/messages",
        "/v1/messages/count_tokens",
        "/v1/embeddings",
        "/v1/rerank",
        "/v1/reranking",
        "/rerank",
        "/reranking"
    };

    private static readonly string[] ManagementPrefixes =
    [
        "/api/",
        "/unload",
        "/running",
        "/upstream",
        "/ui",
        "/logs",
        "/metrics"
    ];

    private readonly GuardInferenceFacadeOptions _options;
    private readonly Uri _internalBaseUri;
    private readonly HttpClient _proxyClient;

    public GuardInferenceFacadeMiddleware(GuardInferenceFacadeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (!ReadOnlyEndpointGuard.IsStrictLoopbackHttpUrl(options.InternalLlamaSwapBaseUrl, out var parsed, out var err) || parsed is null)
            throw new InvalidOperationException($"Internal llama-swap URL rejected: {err}");
        _internalBaseUri = parsed;

        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 64
        };
        _proxyClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task HandleAsync(HttpContext context)
    {
        var method = context.Request.Method;
        var path = context.Request.Path.Value ?? "/";

        // 1. Explicitly reject ALL management / admin / side-effect routes FIRST (never reachable from Agent port!)
        if (IsManagementOrRestrictedPath(path))
        {
            var providedKey = ExtractClientApiKey(context.Request);
            var statusCode = string.IsNullOrEmpty(providedKey) ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden;
            var code = string.IsNullOrEmpty(providedKey)
                ? "UNAUTHORIZED_MANAGEMENT_API"
                : "FORBIDDEN_AGENT_CANNOT_ACCESS_MANAGEMENT_API";

            await WriteJsonErrorAsync(
                context,
                statusCode,
                code,
                $"Management endpoint '{method} {path}' is isolated to LocalLLMManager.Guard and forbidden on the Agent inference listener.");
            return;
        }

        // 2. Allow unauthenticated GET /health for liveness probes; require Agent Inference Key for /v1/* when enabled
        var isHealth = HttpMethods.IsGet(method) && path.Equals("/health", StringComparison.OrdinalIgnoreCase);
        if (!isHealth && _options.RequireAgentInferenceApiKey)
        {
            var providedKey = ExtractClientApiKey(context.Request);
            if (!GuardCredentialStore.ConstantTimeEquals(providedKey, _options.AgentInferenceApiKey))
            {
                await WriteJsonErrorAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    "UNAUTHORIZED_INVALID_INFERENCE_KEY",
                    "Missing or invalid Agent inference API key.");
                return;
            }
        }

        // 3. Enforce strict allowlist of inference & model-list routes
        var allowed = (HttpMethods.IsGet(method) && AllowedGetPaths.Contains(path))
            || (HttpMethods.IsPost(method) && AllowedPostPaths.Contains(path));

        if (!allowed)
        {
            await WriteJsonErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                "FORBIDDEN_ENDPOINT_NOT_IN_INFERENCE_ALLOWLIST",
                $"Endpoint '{method} {path}' is not permitted on the Agent inference facade.");
            return;
        }

        // 4. Zero-buffering 1:1 streaming pass-through to internal llama-swap v275 (ZERO second queue!)
        await ForwardStreamingAsync(context, path);
    }

    private async Task ForwardStreamingAsync(HttpContext context, string path)
    {
        var targetUri = new Uri(_internalBaseUri, path + context.Request.QueryString.Value);
        using var upstreamReq = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);

        // Copy request body for POST without buffering entire stream unnecessarily
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            upstreamReq.Content = new StreamContent(context.Request.Body);
            if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
                upstreamReq.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
        }

        // Copy safe client headers (explicitly STRIP Authorization / x-api-key so Agent key never crosses boundary)
        foreach (var header in context.Request.Headers)
        {
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            upstreamReq.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        // Inject Guard's private internal llama-swap bearer key (never visible to client)
        if (!string.IsNullOrWhiteSpace(_options.InternalLlamaSwapAdminKey))
            upstreamReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.InternalLlamaSwapAdminKey);

        try
        {
            using var upstreamResp = await _proxyClient.SendAsync(
                upstreamReq,
                HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted);

            // Disable Kestrel response buffering so SSE chunks flush immediately
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            context.Response.StatusCode = (int)upstreamResp.StatusCode;
            foreach (var h in upstreamResp.Headers)
            {
                if (h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                    continue;
                context.Response.Headers[h.Key] = h.Value.ToArray();
            }
            foreach (var h in upstreamResp.Content.Headers)
            {
                if (h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                    continue;
                context.Response.Headers[h.Key] = h.Value.ToArray();
            }

            await using var upstreamStream = await upstreamResp.Content.ReadAsStreamAsync(context.RequestAborted);
            var buffer = new byte[4096];
            while (true)
            {
                var read = await upstreamStream.ReadAsync(buffer, context.RequestAborted);
                if (read <= 0) break;
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected cleanly
        }
        catch (Exception ex)
        {
            if (!context.Response.HasStarted)
            {
                await WriteJsonErrorAsync(
                    context,
                    StatusCodes.Status502BadGateway,
                    "UPSTREAM_LLAMA_SWAP_UNAVAILABLE",
                    LogRedactor.Redact($"Failed to reach internal llama-swap gateway: {ex.Message}", _options.InternalLlamaSwapAdminKey));
            }
        }
    }

    private static bool IsManagementOrRestrictedPath(string path)
    {
        foreach (var prefix in ManagementPrefixes)
        {
            if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string? ExtractClientApiKey(HttpRequest req)
    {
        if (req.Headers.TryGetValue("x-api-key", out var xApiKey) && !string.IsNullOrWhiteSpace(xApiKey))
            return xApiKey.ToString().Trim();

        if (req.Headers.TryGetValue("Authorization", out var auth) && !string.IsNullOrWhiteSpace(auth))
        {
            var raw = auth.ToString().Trim();
            if (raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return raw[7..].Trim();
        }
        return null;
    }

    private static async Task WriteJsonErrorAsync(HttpContext context, int statusCode, string code, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        var payload = JsonSerializer.Serialize(new
        {
            src = "LocalLLMManager.Guard",
            error = new
            {
                code,
                message,
                type = statusCode == 401 ? "unauthorized" : "forbidden"
            }
        });
        await context.Response.WriteAsync(payload, context.RequestAborted);
    }

    public void Dispose() => _proxyClient.Dispose();
}
