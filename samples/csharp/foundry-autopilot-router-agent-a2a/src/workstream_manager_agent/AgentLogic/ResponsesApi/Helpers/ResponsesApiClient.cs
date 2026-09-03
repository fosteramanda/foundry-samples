namespace WorkstreamManager.AgentLogic.ResponsesApi.Helpers;

using Azure.Core;
using Azure.Identity;
using WorkstreamManager.AgentLogic;
using WorkstreamManager.Models;
using WorkstreamManager.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

/// <summary>
/// Handles HTTP communication with the OpenAI Responses API, including
/// request building, tool-call loop execution, and response parsing.
/// </summary>
internal class ResponsesApiClient
{
    private readonly AgentMetadata _agentMetadata;
    private readonly ILogger _logger;
    private readonly IConfiguration _configuration;
    private readonly string _accessToken;
    private readonly List<McpServerConfig> _mcpServers;
    private readonly HttpClient _httpClient;
    private readonly ConversationStateStore? _conversationState;
    private readonly string _statePartitionKey;

    // MCP servers that recently failed the connector preflight, keyed by server URL, with the
    // UTC instant the quarantine expires. Process-wide and short-lived on purpose: a broken tool
    // source (e.g. a Foundry toolbox version holding a connection the proxy cannot resolve) is
    // usually broken for every turn, and re-probing it on each one would add latency for no gain;
    // but tool sources also get fixed out from under us, so the quarantine must expire by itself.
    private static readonly ConcurrentDictionary<string, (DateTime ExpiresUtc, string Reason)> _quarantinedMcpServers = new();

    /// <summary>
    /// Whether the routine (standing work) tools are attached this turn. Set by the owning service
    /// once the routine handler exists, so the prompt only describes scheduling when the agent can
    /// actually schedule — advertising tools it was not given is what makes an agent promise work
    /// it never set up.
    /// </summary>
    internal bool RoutinesEnabled { get; set; }

    /// <summary>
    /// Whether the work-item tracker tools are attached this turn. Set by the owning service from
    /// the handler's real state, not from configuration, so the prompt and the tool list cannot
    /// disagree.
    /// </summary>
    internal bool WorkItemsEnabled { get; set; } = true;

    internal ResponsesApiClient(
        AgentMetadata agentMetadata,
        ILogger logger,
        IConfiguration configuration,
        string accessToken,
        List<McpServerConfig> mcpServers,
        HttpClient httpClient,
        ConversationStateStore? conversationState = null)
    {
        _agentMetadata = agentMetadata ?? throw new ArgumentNullException(nameof(agentMetadata));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _accessToken = accessToken;
        _mcpServers = mcpServers ?? throw new ArgumentNullException(nameof(mcpServers));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _conversationState = conversationState;

        // Partition conversation state per agent instance, matching WorkItemService, so one
        // instance can never read another instance's conversation chain.
        _statePartitionKey = $"{_agentMetadata.TenantId}:{_agentMetadata.UserId}";
    }

    internal async Task<string> InvokeAsync(
        string input,
        string conversationId,
        string? instructionsOverride = null,
        bool includeMcpTools = true,
        bool persistResponseId = true,
        string? modelDeploymentOverride = null,
        bool usePreviousResponseId = true,
        List<JsonNode>? additionalTools = null,
        Func<string, string, Task<string?>>? localToolExecutor = null)
    {
        var endpoint = _configuration["AzureOpenAIEndpoint"] ?? throw new InvalidOperationException("AzureOpenAIEndpoint not configured");
        var deployment = string.IsNullOrWhiteSpace(modelDeploymentOverride)
            ? _configuration["ModelDeployment"] ?? throw new InvalidOperationException("ModelDeployment not configured")
            : modelDeploymentOverride.Trim();
        var instructions = instructionsOverride ?? AgentInstructions.GetInstructions(
            _agentMetadata,
            _configuration["SourceOfTruthAgentId"],
            _configuration["SourceOfTruthAgentName"],
            _configuration["ToolboxName"],
            RoutinesEnabled,
            WorkItemsEnabled);

        // Skip tool sources that are already quarantined from an earlier connector failure, so a
        // known-bad server does not fail this turn on the way to being discovered again.
        var activeServers = includeMcpTools
            ? _mcpServers.Where(server => !IsQuarantined(server)).ToList()
            : new List<McpServerConfig>();

        // Deliberately NOT materialized: BuildRequestBody enumerates this on every send, so
        // dropping a server from activeServers after a connector failure rebuilds the tools array
        // without having to re-project it here.
        var mcpTools = activeServers.Select(server =>
            {
                var headers = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {_accessToken}"
                };
                // Per-server extra headers (e.g. the Foundry toolbox proxy's
                // "Foundry-Features" preview flag) layer on top of the bearer.
                if (server.Headers != null)
                {
                    foreach (var kv in server.Headers)
                    {
                        headers[kv.Key] = kv.Value;
                    }
                }

                return (object)new
                {
                    type = "mcp",
                    server_label = server.McpServerName,
                    server_url = server.Url,
                    server_description = $"MCP server: {server.McpServerName}",
                    require_approval = "never",
                    headers,
                };
            });

        // Local function tools (like create_work_item) are independent of MCP server tools.
        // Honor additionalTools regardless of includeMcpTools so callers can run an MCP-free
        // pass with only local tools available (e.g. passive work-item detection).
        var localTools = additionalTools ?? [];

        _logger.LogInformation(
            "Invoking Responses API with {McpToolCount} MCP tool servers and {LocalToolCount} local tools (persistResponseId={Persist}, model={Deployment}, usePreviousResponseId={UsePreviousResponseId})",
            activeServers.Count,
            localTools.Count,
            persistResponseId,
            deployment,
            usePreviousResponseId);

        // Fingerprint the attached MCP servers. A Responses API chain captures its tool
        // inventory when the chain STARTS: continuing with previous_response_id does not
        // re-enumerate, so a conversation begun before a tool existed never sees that tool and
        // the model correctly reports it as unavailable - indefinitely. Changing the server set
        // therefore has to start a fresh chain. Only MCP servers are fingerprinted, and only
        // when they are attached at all, so the tool-less judge and passive-detection passes
        // keep their conversation context.
        var toolFingerprint = includeMcpTools ? ComputeToolFingerprint(activeServers, localTools) : null;

        string? previousResponseId = null;
        if (usePreviousResponseId)
        {
            previousResponseId = await LoadPreviousResponseIdAsync(conversationId, toolFingerprint);
            if (previousResponseId != null)
            {
                _logger.LogInformation("Continuing conversation {ConversationId} with previous_response_id: {PreviousResponseId}", conversationId, previousResponseId);
            }
        }
        else
        {
            _logger.LogInformation("Skipping previous_response_id lookup for conversation {ConversationId}", conversationId);
        }

        var requestUrl = $"{endpoint.TrimEnd('/')}/openai/responses?api-version=2025-03-01-preview";

        // Local function so the closure reads previousResponseId by reference: clearing it below
        // is enough to rebuild the body without it.
        Dictionary<string, object> BuildInitialRequestBody() =>
            BuildRequestBody(input, deployment, instructions, includeMcpTools, mcpTools, localTools, previousResponseId);

        var (success, responseContent, errorBody) = await SendWithConnectorRecoveryAsync(
            requestUrl,
            activeServers,
            BuildInitialRequestBody);

        // A stored previous_response_id can outlive the response it points at - the service's
        // response store expires entries, and this id is cached on the container's own disk. Left
        // alone the conversation is bricked rather than degraded: the dangling id is re-sent on
        // every future turn and never replaced, because the call fails before SaveResponseId runs.
        // Dropping it costs the model's memory of the thread, which is already gone anyway.
        if (!success && previousResponseId != null && IsPreviousResponseNotFound(errorBody))
        {
            _logger.LogWarning(
                "previous_response_id {PreviousResponseId} no longer exists for conversation {ConversationId}; clearing it and retrying without prior conversation state.",
                previousResponseId,
                conversationId);

            ClearPreviousResponseId(conversationId);
            await ClearPreviousResponseIdAsync(conversationId);
            previousResponseId = null;

            (success, responseContent, errorBody) = await SendWithConnectorRecoveryAsync(
                requestUrl,
                activeServers,
                BuildInitialRequestBody);
        }

        if (!success)
        {
            return responseContent;
        }

        for (var iteration = 0; iteration < 10; iteration++)
        {
            var functionCalls = ExtractFunctionCalls(responseContent);
            if (functionCalls.Count == 0)
            {
                break;
            }

            var currentResponseId = TryExtractResponseId(responseContent);
            if (string.IsNullOrWhiteSpace(currentResponseId))
            {
                _logger.LogError("Responses API returned function calls without a response id.");
                return "I encountered an error processing your request.";
            }

            var toolOutputs = new List<object>();
            foreach (var functionCall in functionCalls)
            {
                var toolOutput = localToolExecutor is null
                    ? null
                    : await localToolExecutor(functionCall.Name, functionCall.Arguments);
                toolOutputs.Add(new
                {
                    type = "function_call_output",
                    call_id = functionCall.CallId,
                    output = toolOutput ?? $"Error: Unsupported tool '{functionCall.Name}'."
                });
            }

            (success, responseContent, errorBody) = await SendWithConnectorRecoveryAsync(
                requestUrl,
                activeServers,
                () => BuildRequestBody(toolOutputs, deployment, instructions, includeMcpTools, mcpTools, localTools, currentResponseId));
            if (!success)
            {
                return responseContent;
            }
        }

        if (persistResponseId)
        {
            SaveResponseId(conversationId, responseContent, toolFingerprint);
            await SaveResponseIdAsync(conversationId, responseContent, toolFingerprint);
        }

        return ExtractOutputText(responseContent);
    }

    /// <summary>
    /// Loads the chain pointer, preferring durable storage and falling back to the
    /// container-local file when no table is configured.
    ///
    /// When durable storage is available but has no row yet, the local file is read once and
    /// migrated up. That keeps conversations alive across the switchover instead of silently
    /// resetting everyone's context the moment this ships.
    /// </summary>
    private async Task<string?> LoadPreviousResponseIdAsync(string conversationId, string? expectedToolFingerprint)
    {
        if (_conversationState is { IsDurable: true })
        {
            var durable = await _conversationState.LoadAsync(_statePartitionKey, conversationId, expectedToolFingerprint);
            if (durable != null)
            {
                return durable;
            }

            // Only migrate when the local file still agrees with the current tool set; otherwise
            // the fingerprint check has already decided this chain must restart.
            var local = LoadPreviousResponseId(conversationId, expectedToolFingerprint);
            if (local != null)
            {
                _logger.LogInformation("Migrating conversation {ConversationId} from local file to durable storage.", conversationId);
                await _conversationState.SaveAsync(_statePartitionKey, conversationId, local, expectedToolFingerprint);
            }

            return local;
        }

        return LoadPreviousResponseId(conversationId, expectedToolFingerprint);
    }

    private async Task SaveResponseIdAsync(string conversationId, string responseJson, string? toolFingerprint)
    {
        if (_conversationState is not { IsDurable: true })
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("id", out var idProp))
            {
                var responseId = idProp.GetString();
                if (!string.IsNullOrEmpty(responseId))
                {
                    await _conversationState.SaveAsync(_statePartitionKey, conversationId, responseId, toolFingerprint);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist conversation state for {ConversationId}.", conversationId);
        }
    }

    private async Task ClearPreviousResponseIdAsync(string conversationId)
    {
        if (_conversationState is { IsDurable: true })
        {
            await _conversationState.ClearAsync(_statePartitionKey, conversationId);
        }
    }

    internal string? LoadPreviousResponseId(string conversationId, string? expectedToolFingerprint = null)
    {
        try
        {
            var filePath = GetResponseIdFilePath(conversationId);
            if (File.Exists(filePath))
            {
                var stored = File.ReadAllText(filePath).Trim();
                if (string.IsNullOrEmpty(stored))
                {
                    return null;
                }

                // Format: "v2:<toolFingerprint>:<responseId>". Anything else is a legacy file
                // written before tool fingerprinting; treat it as a mismatch so the chain
                // restarts once and picks up the current tool set.
                string? storedFingerprint = null;
                var id = stored;
                if (stored.StartsWith("v2:", StringComparison.Ordinal))
                {
                    var parts = stored.Split(':', 3);
                    if (parts.Length == 3)
                    {
                        storedFingerprint = parts[1];
                        id = parts[2];
                    }
                }

                if (expectedToolFingerprint != null && storedFingerprint != expectedToolFingerprint)
                {
                    _logger.LogInformation(
                        "Tool set changed for conversation {ConversationId} (stored={StoredFingerprint}, current={CurrentFingerprint}); " +
                        "starting a fresh response chain so the model sees the current tools.",
                        conversationId,
                        storedFingerprint ?? "(legacy)",
                        expectedToolFingerprint);
                    return null;
                }

                return string.IsNullOrEmpty(id) ? null : id;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load previous_response_id for conversation {ConversationId}", conversationId);
        }

        return null;
    }

    internal static DateTimeOffset? TryParseDateTimeOffsetProperty(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case DateTimeOffset dto:
                return dto;
            case DateTime dt:
                var utc = dt.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                    : dt.ToUniversalTime();
                return new DateTimeOffset(utc);
            default:
                var text = value.ToString();
                return DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
        }
    }

    private Dictionary<string, object> BuildRequestBody(
        object inputPayload,
        string deployment,
        string instructions,
        bool includeMcpTools,
        IEnumerable<object> mcpTools,
        IReadOnlyCollection<JsonNode> localTools,
        string? priorResponseId)
    {
        var requestBody = new Dictionary<string, object>
        {
            ["model"] = deployment,
            ["instructions"] = instructions,
            ["input"] = inputPayload,
        };

        // mcpTools is empty when includeMcpTools is false; localTools is empty when caller
        // didn't pass additionalTools. So this naturally produces the right set for all three
        // call modes: full agent (mcp + local), judge (neither), passive detection (local only).
        var allTools = new List<object>();
        allTools.AddRange(mcpTools);
        foreach (var localTool in localTools)
        {
            allTools.Add(localTool);
        }

        if (allTools.Count > 0)
        {
            requestBody["tools"] = allTools;
        }

        if (priorResponseId != null)
        {
            requestBody["previous_response_id"] = priorResponseId;
        }

        return requestBody;
    }

    /// <summary>
    /// Sends the request and, when it fails because the Responses API could not connect to one of
    /// the attached MCP servers, finds the offending server, quarantines it, and retries once
    /// without it.
    ///
    /// The Responses API validates every MCP server in <c>tools</c> before it runs the model, so a
    /// single unreachable tool source returns
    /// <c>400 { "type": "external_connector_error", "param": "tools" }</c> and the turn produces no
    /// answer at all — even for a question that needed none of those tools. Recovering here keeps a
    /// broken tool source degrading to "that tool is missing" instead of "the agent is down".
    /// </summary>
    private async Task<(bool Success, string Content, string? ErrorBody)> SendWithConnectorRecoveryAsync(
        string requestUrl,
        List<McpServerConfig> activeServers,
        Func<Dictionary<string, object>> buildRequestBody)
    {
        var (success, content, errorBody) = await SendRequestAsync(requestUrl, buildRequestBody());
        if (success || activeServers.Count == 0 || !IsMcpConnectorError(errorBody))
        {
            return (success, content, errorBody);
        }

        if (!_configuration.GetValue("EnableMcpConnectorRecovery", true))
        {
            _logger.LogWarning("MCP connector error detected but recovery is disabled via EnableMcpConnectorRecovery.");
            return (success, content, errorBody);
        }

        var removed = await QuarantineUnreachableServersAsync(activeServers);
        if (removed.Count == 0)
        {
            _logger.LogError(
                "MCP connector error, but no attached server failed the preflight - retrying would send the same tools. Attached: {Servers}",
                string.Join(", ", activeServers.Select(s => s.McpServerName)));
            return (success, content, errorBody);
        }

        _logger.LogWarning(
            "Retrying Responses API without unreachable MCP server(s): {RemovedServers}. Remaining: {RemainingServers}",
            string.Join(", ", removed),
            activeServers.Count == 0 ? "(none)" : string.Join(", ", activeServers.Select(s => s.McpServerName)));

        return await SendRequestAsync(requestUrl, buildRequestBody());
    }

    /// <summary>
    /// True when the service rejected the request solely because the <c>previous_response_id</c> we
    /// sent no longer exists in the response store.
    /// </summary>
    internal static bool IsPreviousResponseNotFound(string? errorBody)
    {
        if (string.IsNullOrWhiteSpace(errorBody))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            if (!doc.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var code = error.TryGetProperty("code", out var codeProp) ? codeProp.GetString() : null;
            if (string.Equals(code, "previous_response_not_found", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var param = error.TryGetProperty("param", out var paramProp) ? paramProp.GetString() : null;
            var message = error.TryGetProperty("message", out var messageProp) ? messageProp.GetString() : null;
            return string.Equals(param, "previous_response_id", StringComparison.OrdinalIgnoreCase)
                && message?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes the cached response id for a conversation so the next turn starts a fresh chain
    /// instead of re-sending an id the service has already discarded.
    /// </summary>
    internal void ClearPreviousResponseId(string conversationId)
    {
        try
        {
            var filePath = GetResponseIdFilePath(conversationId);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.LogInformation("Cleared stale previous_response_id for conversation {ConversationId}", conversationId);
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: the retry below still drops the id for this turn, and a successful
            // response overwrites the file anyway.
            _logger.LogWarning(ex, "Failed to clear previous_response_id for conversation {ConversationId}", conversationId);
        }
    }

    /// <summary>
    /// Preflights every attached MCP server in parallel and removes the ones that definitively
    /// fail from <paramref name="activeServers"/>, recording them in the process-wide quarantine.
    /// Inconclusive probes leave the server attached: stripping a working tool source on a flaky
    /// probe would be a worse failure than the one being recovered from.
    /// </summary>
    private async Task<List<string>> QuarantineUnreachableServersAsync(List<McpServerConfig> activeServers)
    {
        var probeTimeout = TimeSpan.FromSeconds(_configuration.GetValue("McpHealthProbeTimeoutSeconds", 20));
        var probe = new McpServerHealthProbe(_logger, _httpClient, probeTimeout);

        var results = await Task.WhenAll(activeServers.Select(async server =>
        {
            var result = await probe.ProbeAsync(server.McpServerName, server.Url, BuildMcpHeaders(server));
            return (Server: server, Result: result);
        }));

        var quarantineFor = TimeSpan.FromSeconds(_configuration.GetValue("McpQuarantineSeconds", 300));
        var removed = new List<string>();

        foreach (var (server, result) in results)
        {
            switch (result.Health)
            {
                case McpServerHealth.Unhealthy:
                    _logger.LogError(
                        "MCP server '{ServerLabel}' ({ServerUrl}) failed preflight and is quarantined for {QuarantineSeconds}s: {Detail}",
                        server.McpServerName,
                        server.Url,
                        quarantineFor.TotalSeconds,
                        result.Detail);
                    _quarantinedMcpServers[server.Url] = (DateTime.UtcNow + quarantineFor, result.Detail);
                    activeServers.Remove(server);
                    removed.Add(server.McpServerName);
                    break;

                case McpServerHealth.Inconclusive:
                    _logger.LogWarning(
                        "MCP server '{ServerLabel}' preflight was inconclusive; keeping it attached: {Detail}",
                        server.McpServerName,
                        result.Detail);
                    break;

                default:
                    _logger.LogInformation("MCP server '{ServerLabel}' passed preflight.", server.McpServerName);
                    break;
            }
        }

        return removed;
    }

    /// <summary>
    /// Reproduces the headers the Responses API sends to an MCP server: the shared agent-user
    /// bearer, overridden by any per-server headers (the Foundry toolbox proxy supplies its own
    /// Authorization plus the preview feature flag).
    /// </summary>
    private Dictionary<string, string> BuildMcpHeaders(McpServerConfig server)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"Bearer {_accessToken}",
        };

        if (server.Headers != null)
        {
            foreach (var kv in server.Headers)
            {
                headers[kv.Key] = kv.Value;
            }
        }

        return headers;
    }

    private bool IsQuarantined(McpServerConfig server)
    {
        if (!_quarantinedMcpServers.TryGetValue(server.Url, out var entry))
        {
            return false;
        }

        if (DateTime.UtcNow >= entry.ExpiresUtc)
        {
            _quarantinedMcpServers.TryRemove(server.Url, out _);
            return false;
        }

        _logger.LogWarning(
            "Skipping quarantined MCP server '{ServerLabel}' until {ExpiresUtc:o}: {Reason}",
            server.McpServerName,
            entry.ExpiresUtc,
            entry.Reason);
        return true;
    }

    /// <summary>
    /// True when the Responses API rejected the request because it could not reach an MCP server
    /// listed in <c>tools</c>, rather than because of the prompt, the model, or the local tools.
    /// </summary>
    internal static bool IsMcpConnectorError(string? errorBody)
    {
        if (string.IsNullOrWhiteSpace(errorBody))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            if (!doc.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var type = error.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
            if (string.Equals(type, "external_connector_error", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var param = error.TryGetProperty("param", out var paramProp) ? paramProp.GetString() : null;
            var code = error.TryGetProperty("code", out var codeProp) ? codeProp.GetString() : null;
            return string.Equals(param, "tools", StringComparison.OrdinalIgnoreCase)
                && string.Equals(code, "http_error", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<(bool Success, string Content, string? ErrorBody)> SendRequestAsync(string requestUrl, Dictionary<string, object> requestBody)
    {
        var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        _logger.LogInformation("Responses API request ({Bytes} bytes): {Request}", json.Length, json);

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var instanceClientId = Environment.GetEnvironmentVariable("FOUNDRY_AGENT_DEFAULT_INSTANCE_CLIENT_ID")
            ?? throw new InvalidOperationException("FOUNDRY_AGENT_DEFAULT_INSTANCE_CLIENT_ID environment variable is not set.");
        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = instanceClientId,
        });
        var token = await credential.GetTokenAsync(new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }), CancellationToken.None);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        var response = await _httpClient.SendAsync(request);
        var responseContent = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Responses API call failed with status {StatusCode}: {Response}", response.StatusCode, responseContent);
            return (false, BuildFailureMessage(response.StatusCode, responseContent), responseContent);
        }

        _logger.LogInformation("Responses API response ({Bytes} bytes): {Response}", responseContent.Length, responseContent);
        return (true, responseContent, null);
    }

    /// <summary>
    /// Builds the message the user actually sees when the Responses API rejects the call. A bare
    /// "Status: BadRequest" is unactionable - it does not say whether the prompt, the model, or a
    /// tool source failed - so append the service's own reason when it gives one.
    /// </summary>
    private static string BuildFailureMessage(HttpStatusCode statusCode, string responseContent)
    {
        var message = $"I encountered an error processing your request. Status: {statusCode}";

        try
        {
            using var doc = JsonDocument.Parse(responseContent);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("message", out var messageProp) &&
                messageProp.ValueKind == JsonValueKind.String)
            {
                var reason = messageProp.GetString();
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    const int maxChars = 300;
                    if (reason.Length > maxChars)
                    {
                        reason = reason[..maxChars] + "…";
                    }

                    message += $" ({reason})";
                }
            }
        }
        catch (JsonException)
        {
            // Non-JSON error body - the status alone is all we can report.
        }

        return message;
    }

    private List<ResponsesApiFunctionCall> ExtractFunctionCalls(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var functionCalls = new List<ResponsesApiFunctionCall>();
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var typeProp) ||
                    !string.Equals(typeProp.GetString(), "function_call", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var callId = item.TryGetProperty("call_id", out var callIdProp) ? callIdProp.GetString() : null;
                var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                var arguments = item.TryGetProperty("arguments", out var argumentsProp)
                    ? argumentsProp.ValueKind == JsonValueKind.String
                        ? argumentsProp.GetString() ?? "{}"
                        : argumentsProp.GetRawText()
                    : "{}";

                if (!string.IsNullOrWhiteSpace(callId) && !string.IsNullOrWhiteSpace(name))
                {
                    functionCalls.Add(new ResponsesApiFunctionCall(callId, name, arguments));
                }
            }

            return functionCalls;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract function calls from Responses API response.");
            return [];
        }
    }

    private string? TryExtractResponseId(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            return doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract response id from Responses API response.");
            return null;
        }
    }

    /// <summary>
    /// Stable short hash of the attached MCP server set. Used to detect that a conversation's
    /// stored response chain predates the current tools, so the chain can be restarted rather
    /// than pinning an inventory the model can no longer act on.
    /// </summary>
    /// <summary>
    /// Fingerprints everything the model can call this turn: the MCP server set AND the
    /// locally-executed function tools.
    ///
    /// A response chain freezes its tool inventory at the point the chain started, so a
    /// conversation begun before a tool existed will never see that tool — and, worse, the
    /// model will keep restating whatever conclusion it reached without it. Hashing only
    /// the MCP servers missed this: shipping a new local tool changed nothing in the
    /// fingerprint, so live conversations silently kept the old tool set until they were
    /// abandoned.
    /// </summary>
    private static string ComputeToolFingerprint(IEnumerable<McpServerConfig> servers, IEnumerable<JsonNode>? localTools)
    {
        var parts = servers
            .Select(s => $"mcp:{s.McpServerName}@{s.Url}")
            .ToList();

        if (localTools != null)
        {
            parts.AddRange(localTools
                .Select(t => t?["name"]?.GetValue<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => $"fn:{n}"));
        }

        var canonical = string.Join("|", parts.OrderBy(s => s, StringComparer.Ordinal));

        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static string GetResponseStoreDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".a365agent");
    }

    private static string GetResponseIdFilePath(string conversationId)
    {
        // Hash the conversation ID so the filename is always a fixed, filesystem-safe length.
        // Some channels (notably Word comment notifications) deliver very long conversation IDs
        // whose base64 form blows past Linux's 255-byte NAME_MAX. The hash is deterministic, so
        // LoadPreviousResponseId and SaveResponseId still resolve to the same file across calls
        // in the same conversation.
        var hashBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(conversationId));
        var safeId = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return Path.Combine(GetResponseStoreDir(), $"{safeId}.responseid");
    }

    private void SaveResponseId(string conversationId, string responseJson, string? toolFingerprint)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("id", out var idProp))
            {
                var responseId = idProp.GetString();
                if (!string.IsNullOrEmpty(responseId))
                {
                    var dir = GetResponseStoreDir();
                    Directory.CreateDirectory(dir);
                    var payload = $"v2:{toolFingerprint ?? string.Empty}:{responseId}";
                    File.WriteAllText(GetResponseIdFilePath(conversationId), payload);
                    _logger.LogDebug("Saved response_id {ResponseId} for conversation {ConversationId}", responseId, conversationId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save response_id for conversation {ConversationId}", conversationId);
        }
    }

    private string ExtractOutputText(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            {
                var textParts = new StringBuilder();
                foreach (var item in output.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var type) && type.GetString() == "message")
                    {
                        if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var contentItem in content.EnumerateArray())
                            {
                                if (contentItem.TryGetProperty("type", out var contentType) &&
                                    contentType.GetString() == "output_text" &&
                                    contentItem.TryGetProperty("text", out var text))
                                {
                                    textParts.Append(text.GetString());
                                }
                            }
                        }
                    }
                }

                return textParts.ToString();
            }

            if (root.TryGetProperty("output_text", out var simpleText))
            {
                return simpleText.GetString() ?? string.Empty;
            }

            _logger.LogWarning("Could not extract output text from Responses API response");
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing Responses API response");
            return string.Empty;
        }
    }
}

internal record ResponsesApiFunctionCall(string CallId, string Name, string Arguments);

