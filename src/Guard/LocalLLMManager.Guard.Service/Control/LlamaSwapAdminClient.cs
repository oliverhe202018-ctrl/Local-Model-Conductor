using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LocalLLMManager.Guard.Service.Security;

namespace LocalLLMManager.Guard.Service.Control;

public sealed record LlamaSwapSmokeResult(
    int StatusCode,
    bool Succeeded,
    string? ErrorSource,
    string? ErrorMessage,
    string RawBody,
    long ElapsedMs);

public sealed record LlamaSwapEventSnapshot(
    bool SnapshotReceived,
    int InFlightCount,
    bool IsPaused,
    IReadOnlyList<string> RunningModels);

/// <summary>
/// Guard-ONLY HTTP/SSE client communicating with internal llama-swap v275 using Guard's private secret.
/// Guard NEVER spawns or kills llama-server directly; all managed lifecycle mutations go through this client.
/// </summary>
public sealed class LlamaSwapAdminClient : IDisposable
{
    private readonly Uri _baseUri;
    private readonly string _internalAdminKey;
    private readonly HttpClient _http;
    private readonly HttpClient _sseHttp;

    public LlamaSwapAdminClient(string internalBaseUrl, string internalAdminKey)
    {
        if (!ReadOnlyEndpointGuard.IsStrictLoopbackHttpUrl(internalBaseUrl, out var parsed, out var err) || parsed is null)
            throw new InvalidOperationException($"Invalid internal llama-swap URL: {err}");
        _baseUri = parsed;
        _internalAdminKey = internalAdminKey ?? string.Empty;

        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(3)
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(180) };

        var sseHandler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(3)
        };
        _sseHttp = new HttpClient(sseHandler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath, object? jsonBody = null)
    {
        var req = new HttpRequestMessage(method, new Uri(_baseUri, relativePath));
        if (!string.IsNullOrWhiteSpace(_internalAdminKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _internalAdminKey);
        if (jsonBody is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(jsonBody), Encoding.UTF8, "application/json");
        return req;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var req = CreateRequest(HttpMethod.Get, "/health");
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<(string? ActiveProfile, IReadOnlyList<string> Profiles)> GetProfilesAsync(CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, "/api/profiles");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return (null, []);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        string? active = doc.RootElement.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString()
            : null;
        var list = new List<string>();
        if (doc.RootElement.TryGetProperty("profiles", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in arr.EnumerateArray())
            {
                if (p.TryGetProperty("id", out var idEl) && idEl.GetString() is string id)
                    list.Add(id);
            }
        }
        return (active, list);
    }

    public async Task<IReadOnlyList<string>> GetRunningModelsAsync(CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, "/running");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var list = new List<string>();
        if (doc.RootElement.TryGetProperty("running", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.TryGetProperty("model", out var m) && m.GetString() is string name)
                    list.Add(name);
            }
        }
        return list;
    }

    public async Task<(bool Ok, int StatusCode, string Body)> SetActiveProfileAsync(string profileName, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Put, "/api/profiles/active", new { name = profileName });
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (resp.IsSuccessStatusCode, (int)resp.StatusCode, LogRedactor.Redact(body, _internalAdminKey));
    }

    public async Task<(bool Ok, int StatusCode, string Body)> UnloadAllAsync(CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Post, "/api/models/unload");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (resp.IsSuccessStatusCode, (int)resp.StatusCode, LogRedactor.Redact(body, _internalAdminKey));
    }

    public async Task<(bool Ok, int StatusCode, string Body)> PauseInferenceAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Post, "/api/models/pause");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, LogRedactor.Redact(body, _internalAdminKey));
        }
        catch (Exception ex)
        {
            return (false, 503, LogRedactor.Redact(ex.Message, _internalAdminKey));
        }
    }

    public async Task<(bool Ok, int StatusCode, string Body)> ResumeInferenceAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Post, "/api/models/resume");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, LogRedactor.Redact(body, _internalAdminKey));
        }
        catch (Exception ex)
        {
            return (false, 503, LogRedactor.Redact(ex.Message, _internalAdminKey));
        }
    }

    public async Task<LlamaSwapEventSnapshot> ReadEventSnapshotAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        int inflight = 0;
        bool paused = false;
        var running = new List<string>();
        bool gotInflight = false;
        bool gotModels = false;

        try
        {
            using var req = CreateRequest(HttpMethod.Get, "/api/events");
            using var resp = await _sseHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new LlamaSwapEventSnapshot(false, 0, false, []);

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            // Read up to 200 lines (inherited Phase 3 fix preventing missed inflight frame!)
            for (int i = 0; i < 200 && !cts.IsCancellationRequested; i++)
            {
                var line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
                if (line is null) break;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var payload = line[5..].Trim();
                using var env = JsonDocument.Parse(payload);
                var msgType = env.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                var dataStr = env.RootElement.TryGetProperty("data", out var d) ? d.GetString() : null;
                if (string.IsNullOrWhiteSpace(dataStr)) continue;

                if (string.Equals(msgType, "inflight", StringComparison.Ordinal))
                {
                    using var idoc = JsonDocument.Parse(dataStr);
                    if (idoc.RootElement.TryGetProperty("requests", out var reqs) && reqs.ValueKind == JsonValueKind.Array)
                        inflight = reqs.GetArrayLength();
                    gotInflight = true;
                }
                else if (string.Equals(msgType, "modelStatus", StringComparison.Ordinal))
                {
                    using var mdoc = JsonDocument.Parse(dataStr);
                    if (mdoc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        running.Clear();
                        foreach (var m in mdoc.RootElement.EnumerateArray())
                        {
                            var st = m.TryGetProperty("state", out var sEl) ? sEl.GetString() : null;
                            var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                            if (!string.IsNullOrWhiteSpace(id) && (st == "ready" || st == "starting"))
                                running.Add(id);
                        }
                    }
                    gotModels = true;
                }
                else if (string.Equals(msgType, "pause", StringComparison.Ordinal) ||
                         string.Equals(msgType, "inferencePaused", StringComparison.Ordinal))
                {
                    using var pdoc = JsonDocument.Parse(dataStr);
                    if (pdoc.RootElement.TryGetProperty("paused", out var pEl) && pEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        paused = pEl.GetBoolean();
                }

                if (gotInflight && gotModels)
                    break;
            }

            return new LlamaSwapEventSnapshot(gotInflight || gotModels, inflight, paused, running);
        }
        catch
        {
            return new LlamaSwapEventSnapshot(false, 0, false, []);
        }
    }

    public async Task<LlamaSwapSmokeResult> RunControlledSmokeInferenceAsync(
        string logicalModelId = "local-coder",
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(25));

        try
        {
            var payload = new
            {
                model = logicalModelId,
                stream = false,
                max_tokens = 8,
                messages = new[] { new { role = "user", content = "Guard controlled inference smoke check." } }
            };
            using var req = CreateRequest(HttpMethod.Post, "/v1/chat/completions", payload);
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var raw = LogRedactor.Redact(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false), _internalAdminKey);
            sw.Stop();

            string? errSrc = null;
            string? errMsg = null;
            bool hasChoices = false;

            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("src", out var srcEl) && srcEl.ValueKind == JsonValueKind.String)
                    errSrc = srcEl.GetString();
                if (doc.RootElement.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.Object)
                {
                    if (errEl.TryGetProperty("message", out var msgEl))
                        errMsg = msgEl.GetString();
                }
                if (doc.RootElement.TryGetProperty("choices", out var chEl) && chEl.ValueKind == JsonValueKind.Array && chEl.GetArrayLength() > 0)
                    hasChoices = true;
            }
            catch
            {
                errMsg = raw;
            }

            var ok = resp.IsSuccessStatusCode && hasChoices;
            return new LlamaSwapSmokeResult((int)resp.StatusCode, ok, errSrc, errMsg, raw, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return new LlamaSwapSmokeResult(504, false, "guard-timeout", "Controlled inference smoke check timed out.", "timeout", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var redacted = LogRedactor.Redact(ex.Message, _internalAdminKey);
            return new LlamaSwapSmokeResult(502, false, "guard-transport", redacted, redacted, sw.ElapsedMilliseconds);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _sseHttp.Dispose();
    }
}
