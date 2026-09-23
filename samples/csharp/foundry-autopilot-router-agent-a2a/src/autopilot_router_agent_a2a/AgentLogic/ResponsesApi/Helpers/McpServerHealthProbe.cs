namespace WorkstreamManager.AgentLogic.ResponsesApi.Helpers;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// Verdict for a single MCP server preflight.
/// </summary>
internal enum McpServerHealth
{
    /// <summary>The server completed the handshake and enumerated its tools.</summary>
    Healthy,

    /// <summary>
    /// The server definitively failed: it could not enumerate its tools, or it rejected our
    /// credentials. Only this verdict is allowed to remove a server from a request.
    /// </summary>
    Unhealthy,

    /// <summary>
    /// The probe could not reach a conclusion (transport failure, timeout, a handshake the probe
    /// itself may have got wrong). Treated as healthy: wrongly stripping a working tool source is
    /// a worse outcome than the failure being recovered from.
    /// </summary>
    Inconclusive,
}

internal readonly record struct McpServerProbeResult(McpServerHealth Health, string Detail);

/// <summary>
/// Performs the same preflight the Responses API does before it will accept an MCP server in a
/// request's <c>tools</c> array: the streamable-HTTP handshake (<c>initialize</c> →
/// <c>notifications/initialized</c> → <c>tools/list</c>).
///
/// The Responses API attaches every declared MCP server up front, and a single server that fails
/// to enumerate its tools fails the entire call with
/// <c>400 { "type": "external_connector_error", "param": "tools" }</c> — the model never runs and
/// the user gets no answer. That is how one broken tool source (for example a Foundry toolbox
/// version holding a connection the proxy cannot resolve) takes down every turn, including turns
/// that would never have used it.
///
/// This probe lets the agent find the offending server itself, drop it, and retry, so a bad tool
/// source degrades the agent instead of disabling it.
///
/// Verdicts are deliberately asymmetric. <c>tools/list</c> is the question the Responses API
/// actually asks, so a failure there is conclusive. A failed <c>initialize</c> usually means the
/// probe and the server disagree about the protocol, not that the server is broken, so it only
/// yields <see cref="McpServerHealth.Unhealthy"/> for statuses that are unambiguous regardless of
/// dialect (auth rejection, server error, failed dependency).
/// </summary>
internal sealed class McpServerHealthProbe
{
    /// <summary>Version offered first; superseded by whatever the server says it supports.</summary>
    private const string PreferredProtocolVersion = "2025-06-18";

    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;

    internal McpServerHealthProbe(ILogger logger, HttpClient httpClient, TimeSpan timeout)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeout = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(20);
    }

    /// <summary>
    /// Runs the MCP handshake against <paramref name="serverUrl"/> using <paramref name="headers"/>
    /// (the same Authorization + per-server headers the Responses API would send).
    /// </summary>
    internal async Task<McpServerProbeResult> ProbeAsync(
        string serverLabel,
        string serverUrl,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        try
        {
            var protocolVersion = PreferredProtocolVersion;
            var (initStatus, initBody, sessionId) = await InitializeAsync(serverUrl, headers, protocolVersion, timeoutCts.Token);

            // Protocol negotiation: a server that refuses our version advertises the ones it
            // accepts. Without this a healthy server would look broken and get its tools stripped.
            if (TryGetSupportedProtocolVersion(initBody, out var negotiatedVersion))
            {
                _logger.LogInformation(
                    "MCP preflight: '{ServerLabel}' rejected protocol {Requested}; retrying with {Negotiated}.",
                    serverLabel,
                    protocolVersion,
                    negotiatedVersion);
                protocolVersion = negotiatedVersion;
                (initStatus, initBody, sessionId) = await InitializeAsync(serverUrl, headers, protocolVersion, timeoutCts.Token);
            }

            if (IsConclusiveFailureStatus(initStatus))
            {
                return new McpServerProbeResult(
                    McpServerHealth.Unhealthy,
                    $"initialize returned HTTP {(int)initStatus} {initStatus}: {Trim(initBody)}");
            }

            if (!IsSuccess(initStatus))
            {
                return new McpServerProbeResult(
                    McpServerHealth.Inconclusive,
                    $"initialize returned HTTP {(int)initStatus} {initStatus}: {Trim(initBody)}");
            }

            if (TryGetJsonRpcError(initBody, out var initError))
            {
                return new McpServerProbeResult(
                    McpServerHealth.Inconclusive,
                    $"initialize returned JSON-RPC error (probe may be speaking the wrong dialect): {Trim(initError)}");
            }

            // Best effort: some servers require the initialized notification before answering
            // tools/list, others ignore it entirely. A failure here is never conclusive.
            try
            {
                await PostAsync(
                    serverUrl, headers, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
                    sessionId, protocolVersion, timeoutCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MCP preflight: initialized notification failed for {ServerLabel}; continuing.", serverLabel);
            }

            var (listStatus, listBody, _) = await PostAsync(
                serverUrl, headers, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}",
                sessionId, protocolVersion, timeoutCts.Token);

            if (!IsSuccess(listStatus))
            {
                return new McpServerProbeResult(
                    McpServerHealth.Unhealthy,
                    $"tools/list returned HTTP {(int)listStatus} {listStatus}: {Trim(listBody)}");
            }

            if (TryGetJsonRpcError(listBody, out var listError))
            {
                return new McpServerProbeResult(McpServerHealth.Unhealthy, $"tools/list returned JSON-RPC error: {Trim(listError)}");
            }

            return new McpServerProbeResult(McpServerHealth.Healthy, "ok");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The probe ran out of its own budget. Not evidence the server is broken - the
            // Responses API gets a longer one than we allow ourselves here.
            return new McpServerProbeResult(McpServerHealth.Inconclusive, $"probe timed out after {_timeout.TotalSeconds:0}s");
        }
        catch (Exception ex)
        {
            return new McpServerProbeResult(McpServerHealth.Inconclusive, $"probe threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private Task<(HttpStatusCode Status, string Body, string? SessionId)> InitializeAsync(
        string serverUrl,
        IReadOnlyDictionary<string, string> headers,
        string protocolVersion,
        CancellationToken cancellationToken)
    {
        var payload =
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{" +
            $"\"protocolVersion\":\"{protocolVersion}\"," +
            "\"capabilities\":{},\"clientInfo\":{\"name\":\"workstream-manager-preflight\",\"version\":\"1.0\"}}}";

        return PostAsync(serverUrl, headers, payload, null, protocolVersion, cancellationToken);
    }

    private async Task<(HttpStatusCode Status, string Body, string? SessionId)> PostAsync(
        string url,
        IReadOnlyDictionary<string, string> headers,
        string payload,
        string? sessionId,
        string protocolVersion,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", protocolVersion);

        foreach (var header in headers)
        {
            if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                var value = header.Value;
                const string bearerPrefix = "Bearer ";
                request.Headers.Authorization = value.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
                    ? new AuthenticationHeaderValue("Bearer", value[bearerPrefix.Length..])
                    : new AuthenticationHeaderValue("Bearer", value);
                continue;
            }

            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (!string.IsNullOrEmpty(sessionId))
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        string? returnedSessionId = null;
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues))
        {
            returnedSessionId = sessionValues.FirstOrDefault();
        }

        return (response.StatusCode, body, returnedSessionId ?? sessionId);
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    /// <summary>
    /// Statuses that mean the server is unusable no matter which MCP dialect the caller speaks:
    /// the credentials were rejected, a dependency failed, or the server itself errored. Anything
    /// else (notably a plain 400) may just be the probe's own request shape, so it stays
    /// inconclusive.
    /// </summary>
    private static bool IsConclusiveFailureStatus(HttpStatusCode status) =>
        status is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound
            or HttpStatusCode.FailedDependency
        || (int)status >= 500;

    /// <summary>
    /// Reads the protocol version a server advertises when it rejects the one we offered
    /// (JSON-RPC error with <c>data.supported</c>). Returns the newest entry it lists.
    /// </summary>
    private static bool TryGetSupportedProtocolVersion(string body, out string version)
    {
        version = string.Empty;

        if (!TryGetJsonRpcErrorElement(body, out var errorElement))
        {
            return false;
        }

        if (!errorElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("supported", out var supported) ||
            supported.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        string? newest = null;
        foreach (var item in supported.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var candidate = item.GetString();
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (newest is null || string.CompareOrdinal(candidate, newest) > 0)
            {
                newest = candidate;
            }
        }

        if (newest is null || string.Equals(newest, PreferredProtocolVersion, StringComparison.Ordinal))
        {
            return false;
        }

        version = newest;
        return true;
    }

    private static bool TryGetJsonRpcError(string body, out string error)
    {
        if (TryGetJsonRpcErrorElement(body, out var element))
        {
            error = element.GetRawText();
            return true;
        }

        error = string.Empty;
        return false;
    }

    /// <summary>
    /// Extracts the JSON-RPC <c>error</c> member, if any. Handles both a plain JSON body and the
    /// SSE framing (<c>data: {...}</c>) that streamable-HTTP MCP servers may reply with.
    /// </summary>
    private static bool TryGetJsonRpcErrorElement(string body, out JsonElement error)
    {
        error = default;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        foreach (var candidate in EnumerateJsonPayloads(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("error", out var errorElement) &&
                    errorElement.ValueKind == JsonValueKind.Object)
                {
                    // Clone so the element outlives the JsonDocument being disposed.
                    error = errorElement.Clone();
                    return true;
                }
            }
            catch (JsonException)
            {
                // Not JSON - fall through to the next candidate.
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateJsonPayloads(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            yield return body;
            yield break;
        }

        foreach (var line in body.Split('\n'))
        {
            var candidate = line.TrimStart();
            if (candidate.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                yield return candidate[5..].Trim();
            }
        }
    }

    private static string Trim(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(empty)";
        }

        var collapsed = value.Replace('\r', ' ').Replace('\n', ' ');
        const int maxChars = 600;
        return collapsed.Length <= maxChars ? collapsed : collapsed[..maxChars] + "…";
    }
}
