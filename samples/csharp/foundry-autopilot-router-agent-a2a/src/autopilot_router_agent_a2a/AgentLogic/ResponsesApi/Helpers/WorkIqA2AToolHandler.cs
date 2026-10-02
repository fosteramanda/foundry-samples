namespace WorkstreamManager.AgentLogic.ResponsesApi.Helpers;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Azure;
using Azure.Core;
using WorkstreamManager.Models;
using WorkstreamManager.Services;

/// <summary>
/// Local tools that talk to the Work IQ agent-to-agent (A2A) surface directly, rather
/// than through a Foundry toolbox connection.
///
/// Why direct instead of a toolbox RemoteA2A connection: on the A2A protocol the target
/// agent is selected by URL PATH (<c>/a2a/{agentId}/</c>), so a toolbox connection can
/// only ever reach ONE agent, and the tool it generates
/// (<c>{connection}___SendMessage</c>) exposes no agentId parameter. Reaching N agents
/// that way means N connections. Calling the surface directly turns the agent id back
/// into a normal tool ARGUMENT, so a single tool reaches every agent published to the
/// tenant — the same shape the Work IQ MCP tool <c>ask</c> already has.
///
/// It also unlocks discovery, which has no toolbox equivalent: <c>GET /a2a/.agents</c>
/// and the per-agent agent card are plain HTTP GETs, not JSON-RPC methods, so a toolbox
/// proxy cannot surface them at all.
///
/// Identity: the bearer is an agent-USER token (user_fic), so Work IQ executes as the
/// digital worker itself. Every downstream permission check sees the agent user, not the
/// human who typed the message.
/// </summary>
internal class WorkIqA2AToolHandler
{
    internal const string DefaultBaseUrl = "https://workiq.svc.cloud.microsoft/a2a/";
    internal const string DefaultAudience = "fdcc1f02-fc51-4226-8753-f668596af7f7";

    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _audience;
    private readonly TokenCredential? _credential;
    private readonly ConversationStateStore? _conversationState;
    private readonly AgentMetadata _agentMetadata;
    private readonly Dictionary<string, string> _turnContexts = new(StringComparer.Ordinal);
    private string? _conversationScope;
    private readonly IDelegationCardStore? _interactions;
    internal Func<DelegationExchange, Task>? Observer { get; set; }
    internal bool CardDeliveryFailed { get; private set; }

    internal sealed record DelegationExchange(
        string Id, string AgentId, string DisplayName, string Question, string ParentScope,
        string ContextScope, string? ContextId, string Revision, bool Starting,
        DelegationOutcome Outcome, string Answer, string? TaskId = null);

    private sealed class InvocationContext(string? scope, string? contextId)
    {
        internal string? Scope { get; } = scope;
        internal string? ContextId { get; set; } = contextId;
    }

    // Agents that completed a call but returned no text. Work IQ reports these as
    // successful tasks with an empty artifact, which reads like a normal answer to a
    // model and invites it to invent one. Remembering them lets the tool say plainly
    // that the agent produced nothing.
    private readonly HashSet<string> _silentAgents = new(StringComparer.OrdinalIgnoreCase);

    // Agent cards are immutable for the life of a turn and cost one HTTP call each, so
    // cache them rather than refetching per listing.
    private readonly Dictionary<string, AgentCardSummary?> _cardCache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record AgentCardSummary(string? Description, List<string> Skills);

    // Display names seen during discovery, so the delegation trail can show "MCS Test"
    // rather than the opaque agent id the model actually passes.
    private readonly Dictionary<string, string> _nameCache = new(StringComparer.OrdinalIgnoreCase);

    // Agents delegated to during the CURRENT turn, in call order, with whether each one
    // actually produced an answer.
    //
    // This exists because attribution cannot be left to the model. The prompt asks it to name
    // the agent it consulted, but a prompt is a request, not a guarantee: a model that
    // delegates, gets nothing back, and then answers from its own knowledge produces text that
    // is indistinguishable from a successful hand-off. Recording the trail at the call site
    // makes the hand-off observable no matter what the model writes, and lets the host render a
    // cue the model cannot forget or contradict.
    private readonly List<DelegationRecord> _delegations = new();

    /// <param name="Answered">
    /// False when the agent accepted the request and returned no usable content — the silent
    /// failure this whole sample exists to surface.
    /// </param>
    /// <param name="Outcome">
    /// Answered, NoAnswer (the silent failure this sample exists to surface), or Pending (accepted
    /// and still working — a follow-up is queued). Collapsing Pending into NoAnswer would tell the
    /// user an agent had nothing to say when an answer is in fact on its way.
    /// </param>
    internal sealed record DelegationRecord(string AgentId, string DisplayName, DelegationOutcome Outcome);

    internal enum DelegationOutcome
    {
        Answered,
        NoAnswer,
        Pending,
    }


    /// <summary>
    /// A delegation that was accepted but not finished within the turn. Captured here and drained
    /// by the caller after the turn, which is what turns a slow specialist into a follow-up
    /// message instead of a dead end.
    /// </summary>
    internal sealed record PendingHandoff(
        string AgentId,
        string DisplayName,
        string TaskId,
        string A2AUrl,
        string Question,
        string? CardId = null,
        bool CardVerification = false);

    private readonly List<PendingHandoff> _pendingHandoffs = new();

    /// <summary>Delegations still in flight at the end of the turn, for the follow-up poller.</summary>
    internal IReadOnlyList<PendingHandoff> PendingHandoffs => _pendingHandoffs;

    /// <summary>
    /// Clears the per-turn delegation trail. Called at the start of every turn: the handler
    /// instance can outlive a single turn, and a stale trail would attribute the previous
    /// turn's hand-off to this one.
    /// </summary>
    internal void BeginTurn(string? conversationScope = null)
    {
        _delegations.Clear();
        _pendingHandoffs.Clear();
        _conversationScope = conversationScope;
        CardDeliveryFailed = false;
    }

    /// <summary>Delegations made during the current turn, in call order.</summary>
    internal IReadOnlyList<DelegationRecord> Delegations => _delegations;

    /// <summary>
    /// Renders the turn's delegation trail as a short HTML cue for the end user, or an empty
    /// string when nothing was delegated — so a turn the agent answered itself looks exactly
    /// as it did before, with no cue to explain away. The cue is meant to be rare enough that
    /// its presence is informative.
    ///
    /// Deduplicated by agent id: a retry against the same agent is one hand-off from the
    /// user's point of view, not two. An agent counts as having answered if any of its calls
    /// produced content.
    /// </summary>
    internal string BuildDelegationCue()
    {
        if (_delegations.Count == 0)
        {
            return string.Empty;
        }

        var parts = _delegations
            .GroupBy(d => d.AgentId, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var name = System.Net.WebUtility.HtmlEncode(g.First().DisplayName);

                // Best outcome wins for an agent asked more than once: an answer supersedes a
                // failed first attempt, and a queued follow-up supersedes a bare no-answer.
                if (g.Any(d => d.Outcome == DelegationOutcome.Answered))
                {
                    return $"<b>{name}</b>";
                }
                if (g.Any(d => d.Outcome == DelegationOutcome.Pending))
                {
                    return $"<b>{name}</b> \u2014 still working, will follow up";
                }
                // Report the no-answer case explicitly. It is the case the user most needs to
                // see, because the reply that follows it was written without the specialist's
                // input even when it reads like an authoritative answer.
                return $"<b>{name}</b> \u2014 no answer";
            })
            .ToList();

        return $"<p><i>\U0001F517 Delegated to: {string.Join(" \u00B7 ", parts)}</i></p>";
    }

    internal WorkIqA2AToolHandler(
        AgentMetadata agentMetadata,
        AgentTokenHelper? tokenHelper,
        ILogger logger,
        HttpClient httpClient,
        IConfiguration configuration,
        ConversationStateStore? conversationState = null,
        TokenCredential? credential = null,
        IDelegationCardStore? interactions = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _agentMetadata = agentMetadata;
        _conversationState = conversationState;
        _interactions = interactions?.IsAvailable == true ? interactions : null;

        var configuredBase = configuration["WorkIqA2ABaseUrl"];
        _baseUrl = (string.IsNullOrWhiteSpace(configuredBase) ? DefaultBaseUrl : configuredBase.Trim()).TrimEnd('/') + "/";

        var configuredAudience = configuration["WorkIqAudience"];
        _audience = string.IsNullOrWhiteSpace(configuredAudience) ? DefaultAudience : configuredAudience.Trim();

        // One credential covers discovery and invocation: both are A2A on the same host and
        // audience (fdcc1f02-…).
        _credential = credential ?? (tokenHelper is null ? null : new AgentTokenCredential(tokenHelper, agentMetadata));
    }

    /// <summary>
    /// Whether these tools are usable. Disabled only when there is no token helper to
    /// mint an agent-user token with.
    /// </summary>
    internal bool IsEnabled => _credential != null;

    internal List<JsonNode> GetToolDefinitions()
    {
        if (!IsEnabled)
        {
            return [];
        }

        return
        [
            JsonNode.Parse("""
            {
                "type": "function",
                "name": "list_workiq_agents",
                "description": "Lists the agents published to this tenant that can be reached over Work IQ agent-to-agent (A2A), with their agent IDs, names, providers and — unless you turn it off — what each one actually does. Use this whenever you are asked which agents you can reach, delegate to, or work with, and to pick an agent for a task. Never answer that from memory.",
                "parameters": {
                    "type": "object",
                    "properties": {
                        "include_descriptions": { "type": "boolean", "description": "Fetch each agent's card so the list includes descriptions and skills. Defaults to true. Set false only when you need the bare list of names quickly." }
                    },
                    "additionalProperties": false
                }
            }
            """)!,

            JsonNode.Parse("""
            {
                "type": "function",
                "name": "get_workiq_agent_card",
                "description": "Gets the A2A agent card for one agent: its description, skills and capabilities. Use this to find out what an agent actually does before routing work to it, instead of guessing from its name.",
                "parameters": {
                    "type": "object",
                    "properties": {
                        "agent_id": { "type": "string", "description": "Agent ID exactly as returned by list_workiq_agents" }
                    },
                    "required": ["agent_id"],
                    "additionalProperties": false
                }
            }
            """)!,

            JsonNode.Parse("""
            {
                "type": "function",
                "name": "ask_workiq_agent",
                "description": "Sends a question to a specific agent over Work IQ A2A and returns its answer. Pass the agent_id from list_workiq_agents. Follow-ups continue that specialist's conversation within this chat, email thread or standing job. Set start_new_conversation only for an explicitly new, unrelated task, never to retry an uncertain write. Name the delegated agent. A reply or preview is not proof of a saved artifact; preserve any real approval requirement.",
                "parameters": {
                    "type": "object",
                    "properties": {
                        "agent_id": { "type": "string", "description": "Agent ID exactly as returned by list_workiq_agents" },
                        "message": { "type": "string", "description": "The question or instruction to send, phrased as you would to a teammate" },
                        "start_new_conversation": { "type": "boolean", "description": "Discard only this specialist's saved continuation for the current parent conversation. Defaults to false. Does not cancel or delete any remote task." }
                    },
                    "required": ["agent_id", "message"],
                    "additionalProperties": false
                }
            }
            """)!,
        ];
    }

    internal Task<string?> TryExecuteAsync(string toolName, string arguments) =>
        TryExecuteCoreAsync(toolName, arguments, null);

    private async Task<string?> TryExecuteCoreAsync(string toolName, string arguments, string? scopeOverride)
    {
        if (!IsEnabled)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            var args = doc.RootElement;

            switch (toolName)
            {
                case "list_workiq_agents":
                {
                    var includeDescriptions = !args.TryGetProperty("include_descriptions", out var incProp)
                        || incProp.ValueKind != JsonValueKind.False;
                    return await ListAgentsAsync(includeDescriptions);
                }

                case "get_workiq_agent_card":
                    return await GetAgentCardAsync(GetStringArg(args, "agent_id"));

                case "ask_workiq_agent":
                    return await AskAgentAsync(
                        GetStringArg(args, "agent_id"), GetStringArg(args, "message"),
                        args.TryGetProperty("start_new_conversation", out var reset) && reset.GetBoolean(),
                        scopeOverride);

                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            // Surface the failure to the model as tool output rather than throwing: a
            // thrown exception aborts the whole turn and the user gets no reply at all.
            _logger.LogWarning(ex, "Work IQ A2A tool '{ToolName}' failed.", toolName);
            return $"The Work IQ A2A call failed: {ex.Message}";
        }
    }

    private static string GetStringArg(JsonElement args, string name) =>
        args.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? string.Empty
            : string.Empty;

    private async Task<HttpRequestMessage> BuildRequestAsync(HttpMethod method, string url, string? rpcMethod = null)
    {
        var token = await _credential!.GetTokenAsync(
            new TokenRequestContext([$"{_audience}/.default"]),
            CancellationToken.None);

        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        request.Headers.TryAddWithoutValidation("A2A-Version", rpcMethod?.Contains('/') == true ? "0.3" : "1.0");
        return request;
    }

    private async Task<string> ListAgentsAsync(bool includeDescriptions)
    {
        using var request = await BuildRequestAsync(HttpMethod.Get, $"{_baseUrl}.agents");
        using var response = await _httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Work IQ A2A list agents failed. status={Status} body={Body}",
                (int)response.StatusCode,
                Truncate(body, 500));
            return $"Could not list agents (HTTP {(int)response.StatusCode}).";
        }

        _logger.LogInformation("Work IQ A2A list agents succeeded ({Length} bytes).", body.Length);

        if (!includeDescriptions)
        {
            return AnnotateSilent(body);
        }

        // The listing endpoint returns only {agentId,name,provider}; the description and
        // skills live on each agent's card. Without them the model has nothing but a name
        // to route on, which is exactly how agents get picked wrongly or invented.
        JsonArray? agents;
        try
        {
            agents = JsonNode.Parse(body) as JsonArray;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Work IQ A2A agent list could not be parsed; returning it unenriched.");
            return AnnotateSilent(body);
        }

        if (agents == null || agents.Count == 0)
        {
            return AnnotateSilent(body);
        }

        var ids = agents
            .Select(a => a?["agentId"]?.GetValue<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cards = await FetchCardsAsync(ids);

        var enriched = new JsonArray();
        foreach (var agent in agents)
        {
            var id = agent?["agentId"]?.GetValue<string>() ?? string.Empty;
            var displayName = agent?["name"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(displayName))
            {
                _nameCache[id] = displayName;
            }

            var entry = new JsonObject
            {
                ["agentId"] = id,
                ["name"] = displayName,
                ["provider"] = agent?["provider"]?.GetValue<string>(),
            };

            if (cards.TryGetValue(id, out var card) && card != null)
            {
                entry["description"] = card.Description;
                if (card.Skills.Count > 0)
                {
                    entry["skills"] = new JsonArray(card.Skills.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
                }
            }
            else
            {
                // Say the description is unavailable rather than omitting the field, so the
                // model does not fill the gap with a guess about what the agent does.
                entry["description"] = "(no agent card published)";
            }

            if (_silentAgents.Contains(id))
            {
                entry["note"] = "previously returned no content when asked";
            }

            enriched.Add(entry);
        }

        var described = cards.Count(c => c.Value != null);
        _logger.LogInformation(
            "Work IQ A2A listing enriched with {Described}/{Total} agent card(s).",
            described,
            ids.Count);

        return enriched.ToJsonString();
    }

    /// <summary>
    /// Fetches agent cards concurrently, bounded so a large tenant cannot fan out into
    /// hundreds of simultaneous requests. Cached per handler instance.
    /// </summary>
    private async Task<Dictionary<string, AgentCardSummary?>> FetchCardsAsync(List<string> agentIds)
    {
        const int maxConcurrency = 6;
        var results = new Dictionary<string, AgentCardSummary?>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<string>();

        foreach (var id in agentIds)
        {
            if (_cardCache.TryGetValue(id, out var cached))
            {
                results[id] = cached;
            }
            else
            {
                pending.Add(id);
            }
        }

        if (pending.Count > 0)
        {
            using var gate = new SemaphoreSlim(maxConcurrency);
            var tasks = pending.Select(async id =>
            {
                await gate.WaitAsync();
                try
                {
                    return (Id: id, Card: await TryGetCardSummaryAsync(id));
                }
                finally
                {
                    gate.Release();
                }
            });

            foreach (var (id, card) in await Task.WhenAll(tasks))
            {
                _cardCache[id] = card;
                results[id] = card;
            }
        }

        return results;
    }

    private async Task<AgentCardSummary?> TryGetCardSummaryAsync(string agentId)
    {
        try
        {
            var url = $"{_baseUrl}{Uri.EscapeDataString(agentId)}/.well-known/agent-card.json";
            using var request = await BuildRequestAsync(HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Work IQ A2A agent card unavailable. agentId={AgentId} status={Status}",
                    agentId,
                    (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            var card = JsonNode.Parse(body);
            var description = card?["description"]?.GetValue<string>();
            var skills = new List<string>();
            if (card?["skills"] is JsonArray skillArray)
            {
                foreach (var skill in skillArray)
                {
                    var skillName = skill?["name"]?.GetValue<string>() ?? skill?["id"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(skillName))
                    {
                        skills.Add(skillName);
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(description) && skills.Count == 0)
            {
                return null;
            }

            return new AgentCardSummary(description, skills);
        }
        catch (Exception ex)
        {
            // One unreachable card must not fail the whole listing.
            _logger.LogInformation(ex, "Work IQ A2A agent card fetch failed. agentId={AgentId}", agentId);
            return null;
        }
    }

    private string AnnotateSilent(string body)
    {
        if (_silentAgents.Count == 0)
        {
            return body;
        }

        // Annotate rather than filter: the model should still see the agent exists.
        return body + "\n\nNote: these agents returned no content when previously asked: "
            + string.Join(", ", _silentAgents) + ".";
    }

    private async Task<string> GetAgentCardAsync(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return "agent_id is required. Call list_workiq_agents first to get one.";
        }

        var url = $"{_baseUrl}{Uri.EscapeDataString(agentId)}/.well-known/agent-card.json";
        using var request = await BuildRequestAsync(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Work IQ A2A agent card failed. agentId={AgentId} status={Status} body={Body}",
                agentId,
                (int)response.StatusCode,
                Truncate(body, 500));
            return $"Could not get the agent card for '{agentId}' (HTTP {(int)response.StatusCode}).";
        }

        // A 200 with an empty body is a documented dead end on the v0.3 card path; report
        // it as missing rather than handing the model an empty object to interpret.
        if (string.IsNullOrWhiteSpace(body))
        {
            return $"Agent '{agentId}' returned an empty agent card, so its capabilities are not published.";
        }

        return StripBinaryFields(body, agentId);
    }

    /// <summary>
    /// Removes embedded binary payloads from an agent card. Work IQ cards carry the agent
    /// icon as a base64 data URI, which can be tens of kilobytes — passing that into the
    /// model's context costs tokens and latency for something it can never use. Everything
    /// else on the card is preserved verbatim.
    /// </summary>
    private string StripBinaryFields(string body, string agentId)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject card)
            {
                return body;
            }
            if (card["name"]?.GetValue<string>() is { Length: > 0 } name)
                _nameCache[agentId] = name;

            var removed = 0;
            foreach (var field in new[] { "iconUrl", "icon", "image" })
            {
                if (card[field]?.GetValue<string>() is string value
                    && value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    removed += value.Length;
                    card[field] = "(embedded image omitted)";
                }
            }

            if (removed > 0)
            {
                _logger.LogInformation(
                    "Work IQ A2A agent card: stripped {Bytes} bytes of embedded image data. agentId={AgentId}",
                    removed,
                    agentId);
            }

            return card.ToJsonString();
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private async Task<string> AskAgentAsync(
        string agentId, string message, bool startNewConversation = false, string? scopeOverride = null)
        => (await ExecuteDelegationAsync(agentId, message, startNewConversation, scopeOverride)).Answer;

    internal Task<DelegationExchange> AskBoundAsync(DelegationCardEntity card, string message)
    {
        if (card.PartitionKey != ContextPartition || card.OwnerAgentId != _agentMetadata.AgentId.ToString("D")
            || card.ContextScope != ScopeFor(card.ParentScope, card.AgentId))
            throw new UnauthorizedAccessException("The saved delegation does not belong to this agent instance.");
        return ExecuteDelegationAsync(card.AgentId, message, false, card.ParentScope,
            card.ContextId, card.ContextRevision, false, card.RowKey["card-".Length..]);
    }

    internal async Task<DelegationExchange> AskVerificationAsync(DelegationCardEntity card, string question)
    {
        var firstPending = _pendingHandoffs.Count;
        var result = await ExecuteDelegationAsync(card.AgentId, question, true,
            "card-verification:" + card.RowKey, notify: false);
        for (var index = firstPending; index < _pendingHandoffs.Count; index++)
            _pendingHandoffs[index] = _pendingHandoffs[index] with
            {
                CardId = card.RowKey["card-".Length..], CardVerification = true
            };
        return result;
    }

    private string? ScopeFor(string? parentScope, string agentId) =>
        !string.IsNullOrWhiteSpace(parentScope) && _agentMetadata.TenantId != Guid.Empty && _agentMetadata.UserId != Guid.Empty
            ? JsonSerializer.Serialize(new[] { _baseUrl, _agentMetadata.AgentId.ToString("D"), parentScope, agentId })
            : null;

    private async Task<DelegationExchange> ExecuteDelegationAsync(
        string agentId, string message, bool startNewConversation, string? scopeOverride,
        string? expectedContext = null, string? expectedRevision = null, bool notify = true, string? operationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!_nameCache.ContainsKey(agentId))
            await GetAgentCardAsync(agentId);

        // Pure A2A: discovery and invocation both go through the Work IQ A2A gateway.
        //
        // This sample is deliberately one half of an A/B pair. Its twin,
        // foundry-autopilot-router-agent, does the same job entirely over Work IQ MCP
        // (`list_agents` + `ask`). Same tenant, same target agents, one variable — the
        // transport — so the two can be compared in production rather than in a script.
        //
        // Measured 18 Aug 2026, both transports reach a Copilot Studio agent and return its
        // canary marker; both support multi-turn. They differ on failure reporting: `ask`
        // returns a structured error with a requestId, the A2A send returns HTTP 404 with a
        // literal null body. Foundry-backed agents return nothing on either — a platform
        // defect, not a transport choice.
        //
        // copilot_chat on the Agent 365 server is not an option here and is not a fallback:
        // it reaches no agent at all, and answers as generic Copilot with no attribution
        // field in the payload to reveal it. Removed from this sample entirely.
        var parentScope = scopeOverride ?? _conversationScope;
        var scope = ScopeFor(parentScope, agentId);
        if (scope == null)
            _logger.LogWarning("A2A delegation has no authenticated conversation scope; downstream context will not be reused.");
        DelegationScopeLease? lease = null;
        var callId = operationId ?? Guid.NewGuid().ToString("N");
        DelegationExchange? exchange = null;
        var sent = false;
        try
        {
            if (_interactions != null && scope != null)
                lease = await _interactions.AcquireScopeAsync(ContextPartition, scope, expectedRevision);
            else if (expectedRevision != null)
                throw new InvalidOperationException("Durable approval coordination is unavailable. No Save request was sent.");
            if (scope != null && startNewConversation)
            {
                if (_conversationState != null)
                    await _conversationState.ClearA2AContextAsync(ContextPartition, scope);
                _turnContexts.Remove(scope);
            }
            var priorContext = scope == null ? null : _conversationState != null
                ? await _conversationState.LoadA2AContextAsync(ContextPartition, scope)
                : _turnContexts.GetValueOrDefault(scope);
            if (expectedContext != null && priorContext != expectedContext)
                throw new InvalidOperationException("The specialist conversation changed. No Save request was sent.");
            if (_conversationState == null && scope != null)
                _logger.LogWarning("A2A continuation has no shared store; it is available only within this handler.");
            var context = new InvocationContext(scope, priorContext);
            exchange = new DelegationExchange(callId, agentId, ResolveDisplayName(agentId), message,
                parentScope ?? "", scope ?? "", priorContext, lease?.Revision ?? "", true,
                DelegationOutcome.Pending, "");
            if (notify) await NotifyAsync(exchange);
            var pendingBefore = _pendingHandoffs.Count;
            sent = true;
            var answer = await AskViaA2AAsync(agentId, message, context);
            var outcome = _pendingHandoffs.Count > pendingBefore
                ? DelegationOutcome.Pending
                : HasCompletedAnswer(answer) ? DelegationOutcome.Answered : DelegationOutcome.NoAnswer;
            for (var i = pendingBefore; i < _pendingHandoffs.Count; i++)
                _pendingHandoffs[i] = _pendingHandoffs[i] with { CardId = callId };
            exchange = exchange with
            {
                Starting = false, Answer = answer, Outcome = outcome, ContextId = context.ContextId,
                TaskId = _pendingHandoffs.Skip(pendingBefore).FirstOrDefault()?.TaskId
            };
        }
        catch (Exception ex) when (sent && ex is HttpRequestException or OperationCanceledException or RequestFailedException or InvalidOperationException)
        {
            _logger.LogError(ex, "Delegation outcome is uncertain after sending to {AgentId}; the operation must not be repeated.", agentId);
            exchange = exchange! with
            {
                Starting = false, Outcome = DelegationOutcome.NoAnswer,
                Answer = "ERROR: The specialist request may already have run. Do not repeat this operation automatically. " + ex.Message
            };
        }
        finally
        {
            if (lease != null)
            {
                try { await _interactions!.ReleaseScopeAsync(lease); }
                catch (Exception ex) when (ex is RequestFailedException or InvalidOperationException)
                {
                    _logger.LogError(ex, "Could not release delegation coordination for {AgentId}.", agentId);
                    if (sent && exchange != null)
                        exchange = exchange with
                        {
                            Starting = false, Outcome = DelegationOutcome.NoAnswer,
                            Answer = "ERROR: The specialist request was sent but its completion coordination failed. Do not repeat it. " + exchange.Answer
                        };
                    else throw;
                }
            }
        }
        if (exchange == null) throw new InvalidOperationException("The delegation produced no receipt.");
        _delegations.Add(new DelegationRecord(agentId, ResolveDisplayName(agentId), exchange.Outcome));
        if (notify) await NotifyAsync(exchange);
        return exchange;
    }

    private async Task NotifyAsync(DelegationExchange exchange)
    {
        if (Observer == null) return;
        try { await Observer(exchange); }
        catch (Exception ex) when (ex is RequestFailedException or HttpRequestException or OperationCanceledException
            or Microsoft.Agents.Core.Errors.ErrorResponseException or InvalidOperationException or FormatException or JsonException)
        {
            CardDeliveryFailed = true;
            _logger.LogError(ex, "Native delegation card could not be delivered or updated. operation={OperationId} agent={AgentId}",
                exchange.Id, exchange.AgentId);
        }
    }

    /// <summary>
    /// Friendly name for an agent id, falling back to the id itself. The fallback matters:
    /// the model can pass an id it got from somewhere other than discovery, and showing the
    /// raw id is honest, whereas showing nothing would hide that a hand-off happened.
    /// </summary>
    internal async Task<string> AskForStandingJobAsync(string jobId, string agentId, string question)
    {
        if (!_nameCache.ContainsKey(agentId))
            await GetAgentCardAsync(agentId);
        var pendingBefore = _pendingHandoffs.Count;
        var answer = await TryExecuteCoreAsync("ask_workiq_agent",
            JsonSerializer.Serialize(new { agent_id = agentId, message = question }), "job:" + jobId);
        var pending = _pendingHandoffs.Skip(pendingBefore).ToArray();
        if (pending.Length > 0)
        {
            // Job receipts own this result; the ordinary proactive poller must not promise
            // delivery through an unrelated conversation or bypass the job's recipient policy.
            _pendingHandoffs.RemoveRange(pendingBefore, pending.Length);
            return JsonSerializer.Serialize(new
            {
                outcome = "pending", agent_id = agentId, agent_name = ResolveDisplayName(agentId),
                task_ids = pending.Select(item => item.TaskId),
                detail = "The specialist is still working. No completed answer or later delivery is confirmed. Do not resend automatically."
            });
        }
        var answered = HasCompletedAnswer(answer);
        return JsonSerializer.Serialize(new
        {
            outcome = answered ? "answered" : "no_answer", agent_id = agentId,
            agent_name = ResolveDisplayName(agentId), answer = answered ? answer : null,
            detail = answered ? null : answer
        });
    }

    // This checks for returned answer content, not a committed business operation.
    internal static bool HasCompletedAnswer(string? answer) =>
        !string.IsNullOrWhiteSpace(answer)
        && !answer.StartsWith("Agent '", StringComparison.Ordinal)
        && !answer.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
        && !answer.StartsWith("The Work IQ A2A call failed:", StringComparison.OrdinalIgnoreCase)
        && !answer.Equals("(no response)", StringComparison.OrdinalIgnoreCase);

    private string ResolveDisplayName(string agentId) =>
        _nameCache.TryGetValue(agentId, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : agentId;

    /// <summary>
    /// Minimal MCP streamable-HTTP client: initialize, then tools/call. Handles both plain JSON
    /// and SSE-framed responses, since the servers use either.
    /// </summary>
    private async Task<(bool Ok, string? Result, string? Error)> CallMcpToolAsync(
        string serverUrl,
        string bearer,
        string toolName,
        JsonObject arguments)
    {
        static string? ExtractJsonPayload(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            if (body.TrimStart().StartsWith('{'))
            {
                return body;
            }

            var frames = body
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                .Select(l => l[5..].Trim())
                .Where(l => l.StartsWith('{'));

            return string.Concat(frames) is { Length: > 0 } joined ? joined : null;
        }

        HttpRequestMessage NewRequest(string json, string? sessionId)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, serverUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-11-25");
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                req.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
            }
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return req;
        }

        var initBody = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = "2025-11-25",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "autopilot-router", ["version"] = "1.0" },
            },
        }.ToJsonString();

        string? sessionId = null;
        using (var initRequest = NewRequest(initBody, null))
        using (var initResponse = await _httpClient.SendAsync(initRequest))
        {
            if (!initResponse.IsSuccessStatusCode)
            {
                var initError = await initResponse.Content.ReadAsStringAsync();
                return (false, null, $"initialize HTTP {(int)initResponse.StatusCode}: {Truncate(initError, 300)}");
            }

            if (initResponse.Headers.TryGetValues("Mcp-Session-Id", out var values))
            {
                sessionId = values.FirstOrDefault();
            }
        }

        var callBody = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = arguments,
            },
        }.ToJsonString();

        using var callRequest = NewRequest(callBody, sessionId);
        using var callResponse = await _httpClient.SendAsync(callRequest);
        var body = await callResponse.Content.ReadAsStringAsync();

        if (!callResponse.IsSuccessStatusCode)
        {
            return (false, null, $"tools/call HTTP {(int)callResponse.StatusCode}: {Truncate(body, 300)}");
        }

        var payload = ExtractJsonPayload(body);
        if (payload == null)
        {
            return (false, null, $"unparseable response: {Truncate(body, 300)}");
        }

        var node = JsonNode.Parse(payload);
        if (node?["error"] != null)
        {
            return (false, null, node["error"]?["message"]?.GetValue<string>() ?? "unknown JSON-RPC error");
        }

        // MCP returns tool output as content parts; the payload we want is the text part.
        var text = (node?["result"]?["content"] as JsonArray)?
            .Select(c => c?["text"]?.GetValue<string>())
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

        return (true, text, null);
    }

    private async Task<string> AskViaA2AAsync(string agentId, string message, InvocationContext context)
    {
        var url = $"{_baseUrl}{Uri.EscapeDataString(agentId)}/";

        // Negotiate v1 first. A legacy retry is permitted only when the server
        // rejects the method/shape before accepting the request.
        var attempts = new (string Method, string? Role)[]
        {
            ("SendMessage", "ROLE_USER"),
            ("message/send", "user"),
        };

        string? lastError = null;
        string? lastTaskId = null;
        foreach (var (method, role) in attempts)
        {
            var pendingBefore = _pendingHandoffs.Count;
            var (text, retryable, error) = await SendMessageAsync(url, agentId, message, method, role, context);
            if (!retryable)
            {
                if (_pendingHandoffs.Count > pendingBefore)
                    return text;
                if (!string.IsNullOrWhiteSpace(text) && !text.StartsWith("Agent '", StringComparison.Ordinal))
                {
                    return text;
                }

                // Non-retryable but empty: the send was accepted and produced no text on the
                // synchronous response. Before calling the agent silent, exhaust the two other
                // channels the A2A protocol can deliver an answer on.
                lastTaskId = _lastTaskId;
                var recovered = await TryRecoverAnswerAsync(url, agentId, lastTaskId, context);
                return recovered ?? text;
            }

            lastError = error;
            _logger.LogInformation(
                "Work IQ A2A send rejected shape (method={Method} role={Role}) for agent {AgentId}: {Error}. Trying next.",
                method,
                role ?? "(omitted)",
                agentId,
                error);
        }

        return $"Agent '{agentId}' rejected every supported A2A send shape. Last error: {lastError}";
    }

    // Task id from the most recent send, so the recovery path can poll it.
    private string? _lastTaskId;

    /// <summary>
    /// Second and third chances at an answer the synchronous send did not carry.
    ///
    /// A2A can deliver an agent's output three ways: inline on the send response, on the task
    /// when fetched later, or as incremental artifact-update events over a stream. An agent
    /// whose text arrives on the stream looks completely silent to a caller that only reads
    /// the synchronous response — which is exactly the shape we observed (a completed task
    /// whose "Answer" artifact carried only a sensitivity-label part).
    /// </summary>
    private async Task<string?> TryRecoverAnswerAsync(
        string url, string agentId, string? taskId, InvocationContext context)
    {
        if (!string.IsNullOrWhiteSpace(taskId))
        {
            foreach (var method in new[] { "GetTask", "tasks/get" })
            {
                var text = await TryGetTaskAsync(url, agentId, taskId!, method, context);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _logger.LogInformation(
                        "Work IQ A2A recovered answer via {Method} for agent {AgentId} ({Length} chars).",
                        method,
                        agentId,
                        text.Length);
                    return text;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(taskId))
            return null;
        foreach (var method in new[] { "SubscribeToTask", "tasks/resubscribe" })
        {
            var text = await TrySubscribeAsync(url, agentId, taskId, method, context);
            if (!string.IsNullOrWhiteSpace(text))
            {
                _logger.LogInformation(
                    "Work IQ A2A recovered answer via {Method} for agent {AgentId} ({Length} chars).",
                    method,
                    agentId,
                    text.Length);
                return text;
            }
        }

        return null;
    }

    /// <summary>
    /// Polls a previously-accepted A2A task for its answer, from outside any turn.
    ///
    /// Exists for the follow-up poller: the in-turn recovery path gives a slow agent only a few
    /// seconds, but an agent that takes minutes can be collected later. Returns null while the
    /// task is still unfinished, so the caller can tell "not yet" from "nothing to say".
    /// </summary>
    internal async Task<string?> TryFetchTaskAnswerAsync(string a2aUrl, string agentId, string taskId)
    {
        foreach (var method in new[] { "GetTask", "tasks/get" })
        {
            var text = await TryGetTaskAsync(a2aUrl, agentId, taskId, method);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private async Task<string?> TryGetTaskAsync(
        string url, string agentId, string taskId, string method, InvocationContext? context = null)
    {
        try
        {
            var payload = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = Guid.NewGuid().ToString("N"),
                ["method"] = method,
                ["params"] = new JsonObject { ["id"] = taskId, ["name"] = $"tasks/{taskId}" },
            };

            using var request = await BuildRequestAsync(HttpMethod.Post, url, method);
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var root = JsonNode.Parse(body);
            if (root?["error"] != null)
            {
                _logger.LogInformation(
                    "Work IQ A2A {Method} rejected for agent {AgentId}: {Error}",
                    method,
                    agentId,
                    root["error"]?["message"]?.GetValue<string>());
                return null;
            }

            var text = ExtractText(root?["result"]);
            if (context != null)
                await PreserveContextAsync(root?["result"], context, agentId);
            if (TaskFailure(root?["result"], agentId) is { } failure)
                return failure;
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogInformation(
                    "Work IQ A2A {Method} returned no text for agent {AgentId}. raw={Raw}",
                    method,
                    agentId,
                    Truncate(body, 1200));
            }

            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Work IQ A2A {Method} failed for agent {AgentId}.", method, agentId);
            return null;
        }
    }

    /// <summary>
    /// Subscribes to an accepted task without sending the user's request again.
    /// </summary>
    private async Task<string?> TrySubscribeAsync(
        string url, string agentId, string taskId, string method, InvocationContext context)
    {
        try
        {
            var payload = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = Guid.NewGuid().ToString("N"),
                ["method"] = method,
                ["params"] = new JsonObject { ["id"] = taskId, ["name"] = $"tasks/{taskId}" },
            };

            using var request = await BuildRequestAsync(HttpMethod.Post, url, method);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Work IQ A2A {Method} failed for agent {AgentId}. status={Status}",
                    method,
                    agentId,
                    (int)response.StatusCode);
                return null;
            }

            var chunks = new List<string>();
            foreach (var line in body.Split('\n'))
            {
                var trimmed = line.Trim();
                var json = trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? trimmed[5..].Trim()
                    : trimmed;

                if (string.IsNullOrWhiteSpace(json) || json[0] != '{')
                {
                    continue;
                }

                JsonNode? frame;
                try
                {
                    frame = JsonNode.Parse(json);
                }
                catch (JsonException)
                {
                    continue;
                }

                var result = frame?["result"];
                if (result == null)
                {
                    continue;
                }
                await PreserveContextAsync(result, context, agentId);
                if (TaskFailure(result, agentId) is { } failure)
                    return failure;

                // Artifact-update events nest the payload one level deeper than a task does.
                var text = ExtractText(result)
                    + ExtractText(result["artifactUpdate"]?["artifact"])
                    + ExtractText(result["artifact"]);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    chunks.Add(text.Trim());
                }
            }

            var joined = string.Join("\n\n", chunks.Distinct()).Trim();
            if (string.IsNullOrWhiteSpace(joined))
            {
                _logger.LogInformation(
                    "Work IQ A2A {Method} produced no text for agent {AgentId}. raw={Raw}",
                    method,
                    agentId,
                    Truncate(body, 1200));
                return null;
            }

            return joined;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Work IQ A2A {Method} failed for agent {AgentId}.", method, agentId);
            return null;
        }
    }

    private async Task<(string Text, bool Retryable, string? Error)> SendMessageAsync(
        string url,
        string agentId,
        string message,
        string method,
        string? role,
        InvocationContext context)
    {
        var legacy = method.Contains('/');
        var part = new JsonObject { ["text"] = message };
        if (legacy)
            part["kind"] = "text";
        var messageNode = new JsonObject
        {
            ["messageId"] = Guid.NewGuid().ToString(),
            ["parts"] = new JsonArray(part),
        };
        if (legacy)
            messageNode["kind"] = "message";
        if (!string.IsNullOrWhiteSpace(context.ContextId))
            messageNode["contextId"] = context.ContextId;

        if (role != null)
        {
            messageNode["role"] = role;
        }

        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Guid.NewGuid().ToString("N"),
            ["method"] = method,
            ["params"] = new JsonObject
            {
                ["message"] = messageNode,
            },
        };

        using var request = await BuildRequestAsync(HttpMethod.Post, url, method);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        _logger.LogInformation(
            "Work IQ A2A send. agentId={AgentId} continued={Continued} contextHash={ContextHash}",
            agentId, context.ContextId != null, ContextHash(context.ContextId));

        using var response = await _httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Work IQ A2A send failed. agentId={AgentId} method={Method} status={Status} body={Body}",
                agentId,
                method,
                (int)response.StatusCode,
                Truncate(body, 500));
            return ($"ERROR: agent '{agentId}' could not be reached (HTTP {(int)response.StatusCode}). Report this failure to the user verbatim; do not describe it as an empty or missing reply.", false, null);
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return ($"ERROR: agent '{agentId}' returned a response that could not be parsed.", false, null);
        }

        var error = root?["error"];
        if (error != null)
        {
            var code = error["code"]?.GetValue<int>() ?? 0;
            var errorMessage = error["message"]?.GetValue<string>() ?? "unknown error";

            // -32601 wrong method, -32602 wrong payload shape: both mean "try another
            // shape", not "this agent has nothing to say".
            if (code == -32601 || code == -32602)
            {
                return (string.Empty, true, $"{code} {errorMessage}");
            }

            _logger.LogWarning(
                "Work IQ A2A send returned a JSON-RPC error. agentId={AgentId} code={Code} message={Message}",
                agentId,
                code,
                errorMessage);
            return ($"ERROR: agent '{agentId}' returned a protocol error ({code}): {errorMessage}. Report this to the user verbatim; it is a failure, not an empty answer.", false, null);
        }

        var text = ExtractText(root?["result"]);
        try
        {
            await PreserveContextAsync(root?["result"], context, agentId);
        }
        catch (Exception ex) when (ex is RequestFailedException or InvalidOperationException or JsonException or HttpRequestException)
        {
            _logger.LogError(ex, "A2A request was accepted, but its continuation could not be retained for {AgentId}.", agentId);
            return ("ERROR: The specialist already processed this request, but its conversation continuation could not be retained. " +
                    "Do not repeat the request: its side effects may already exist. " +
                    $"Specialist response: {text}", false, null);
        }
        if (TaskFailure(root?["result"], agentId) is { } failure)
            return (failure, false, null);

        // Remember the task id even when the answer is empty: the recovery path polls it.
        _lastTaskId = root?["result"]?["task"]?["id"]?.GetValue<string>()
            ?? root?["result"]?["id"]?.GetValue<string>();

        // Log the artifact shape on EVERY send, success or not. Whether a working agent's
        // response also carries the sensitivity-label part is the difference between "the
        // label displaces the text" and "the label is incidental and the text is dropped for
        // another reason" — and those are two very different bug reports.
        LogArtifactShape(agentId, root, string.IsNullOrWhiteSpace(text) ? "no-text" : $"text:{text.Length}");

        if (string.IsNullOrWhiteSpace(text))
        {
            // Distinguish three cases that all look like "no answer" from the outside:
            //  - the task is still running and needs polling (state submitted/working)
            //  - the task completed carrying only non-text parts (the "silent agent" case)
            //  - the payload nests text somewhere this extractor does not look
            // Without the raw body in the log all three are indistinguishable, which is how
            // a protocol bug gets mistaken for an agent having nothing to say.
            var state = root?["result"]?["status"]?["state"]?.GetValue<string>()
                ?? root?["result"]?["task"]?["status"]?["state"]?.GetValue<string>()
                ?? "(none)";
            _logger.LogWarning(
                "Work IQ A2A agent {AgentId} returned no text. state={State} rawResult={Raw}",
                agentId,
                state,
                Truncate(body, 1500));

            // A2A v1.0 is protobuf-backed, so states arrive as enum names
            // (TASK_STATE_WORKING) rather than the v0.3 lowercase spelling ("working").
            // Match both, or an in-flight task gets misreported as a silent agent.
            var pending = state.Contains("SUBMITTED", StringComparison.OrdinalIgnoreCase)
                || state.Contains("WORKING", StringComparison.OrdinalIgnoreCase);
            if (pending)
            {
                // The agent took the work but has not finished. Capture enough to poll it after
                // the turn ends, so a slow specialist becomes a follow-up message rather than a
                // dead end. Falls back to today's behaviour when there is no task id to poll —
                // an answer we cannot retrieve is better reported than pretended.
                if (!string.IsNullOrWhiteSpace(_lastTaskId))
                {
                    _pendingHandoffs.Add(new PendingHandoff(
                        agentId,
                        ResolveDisplayName(agentId),
                        _lastTaskId!,
                        url,
                        message));

                    return ($"Agent '{agentId}' accepted the request and is still working (state={state}). " +
                            "Tell the user you have asked that agent and will follow up as soon as it answers. " +
                            "Do NOT answer the question on its behalf and do not repeat the question back.", false, null);
                }

                return ($"Agent '{agentId}' accepted the request and is still working (state={state}); it did not return an answer synchronously.", false, null);
            }

            _silentAgents.Add(agentId);
            return ($"Agent '{agentId}' accepted the request but returned no content (state={state}). Tell the user that agent produced no answer; do not answer on its behalf.", false, null);
        }

        _logger.LogInformation("Work IQ A2A agent {AgentId} answered ({Length} chars).", agentId, text.Length);
        return (text, false, null);
    }

    private string ContextPartition => $"{_agentMetadata.TenantId:D}:{_agentMetadata.UserId:D}";

    private static string? TaskFailure(JsonNode? result, string agentId)
    {
        var task = result?["task"] ?? result?["statusUpdate"] ?? result;
        var state = task?["status"]?["state"]?.GetValue<string>();
        return state is "TASK_STATE_FAILED" or "TASK_STATE_CANCELED" or "TASK_STATE_REJECTED"
            or "failed" or "canceled" or "rejected"
            ? $"ERROR: Agent '{agentId}' returned task state {state}. {ExtractText(task)}"
            : null;
    }

    private static string ContextHash(string? value) => value == null
        ? "(none)"
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];

    internal static string? ExtractContextId(JsonNode? result)
    {
        if (result is not JsonObject)
            return null;
        var contexts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in new[] { result, result["task"], result["message"], result["statusUpdate"], result["artifactUpdate"] })
        {
            if (node?["contextId"] is not { } context)
                continue;
            if (context is not JsonValue value || !value.TryGetValue<string>(out var id) || string.IsNullOrWhiteSpace(id))
                throw new JsonException("The A2A response contains an invalid contextId.");
            contexts.Add(id);
        }
        if (contexts.Count > 1)
            throw new JsonException("The A2A response contains conflicting conversation identifiers.");
        return contexts.SingleOrDefault();
    }

    private async Task PreserveContextAsync(JsonNode? result, InvocationContext context, string agentId)
    {
        var returned = ExtractContextId(result);
        if (returned == null)
        {
            if (context.ContextId == null)
                _logger.LogWarning("A2A agent {AgentId} returned no conversation identifier; follow-up continuity is unavailable.", agentId);
            return;
        }
        if (context.ContextId != null && context.ContextId != returned)
            throw new InvalidOperationException("The specialist returned a different conversation identifier for a continued request.");
        if (context.Scope != null && context.ContextId != returned)
        {
            if (_conversationState != null)
                await _conversationState.SaveA2AContextAsync(ContextPartition, context.Scope, returned, context.ContextId);
            _turnContexts[context.Scope] = returned;
        }
        context.ContextId = returned;
        _logger.LogInformation("Work IQ A2A context retained. agentId={AgentId} contextHash={ContextHash}",
            agentId, ContextHash(returned));
    }

    /// <summary>
    /// Pulls the human-readable text out of an A2A result. Text can arrive on the task's
    /// artifacts, on the status message, on the task history, or — when the agent answers
    /// with a bare message rather than a task — directly on the result. Non-text parts
    /// (data parts) carry rendering metadata that must not be shown to the user.
    ///
    /// Be exhaustive here on purpose: an answer this method fails to find is indistinguishable
    /// from an agent that said nothing, and that mistake gets reported as a broken agent.
    /// </summary>
    private static string ExtractText(JsonNode? result)
    {
        if (result == null)
        {
            return string.Empty;
        }

        // The gateway wraps the task under result.task on A2A v1.0 and returns it bare on
        // v0.3. Unwrap so every lookup below works on both shapes.
        var task = result["task"] ?? result;

        var chunks = new List<string>();

        void CollectParts(JsonNode? parts)
        {
            if (parts is not JsonArray array)
            {
                return;
            }

            foreach (var part in array)
            {
                if (part == null)
                {
                    continue;
                }

                // v0.3 tags text parts with kind:"text"; v1.0 parts carry no kind and are
                // identified by having a text field (non-text parts carry data/mediaType
                // instead, e.g. the sensitivity-label part). Accept either rather than
                // requiring a discriminator that only one version sends.
                var kind = part["kind"]?.GetValue<string>();
                if (kind != null && !kind.Equals("text", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = part["text"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    chunks.Add(value);
                }
            }
        }

        if (task["artifacts"] is JsonArray artifacts)
        {
            foreach (var artifact in artifacts)
            {
                CollectParts(artifact?["parts"]);
            }
        }

        CollectParts(task["status"]?["message"]?["parts"]);
        CollectParts(task["parts"]);
        CollectParts(task["message"]?["parts"]);
        CollectParts(result["message"]?["parts"]);

        // Task history holds the agent's turns when the answer is not promoted onto an
        // artifact. Skip the user's own turns so the caller is never handed its own text back.
        if (task["history"] is JsonArray history)
        {
            foreach (var entry in history)
            {
                var role = entry?["role"]?.GetValue<string>();
                if (role != null && role.Contains("USER", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                CollectParts(entry?["parts"]);
            }
        }

        return string.Join("\n\n", chunks.Distinct()).Trim();
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];

    /// <summary>
    /// Records the artifact/part shape of an A2A response so working and non-working agents
    /// can be compared on identical terms.
    /// </summary>
    private void LogArtifactShape(string agentId, JsonNode? root, string outcome)
    {
        try
        {
            var task = root?["result"]?["task"] ?? root?["result"];
            var descriptions = new List<string>();

            if (task?["artifacts"] is JsonArray artifacts)
            {
                foreach (var artifact in artifacts)
                {
                    var name = artifact?["name"]?.GetValue<string>() ?? "(unnamed)";
                    var parts = artifact?["parts"] as JsonArray;
                    var partDescriptions = parts?.Select(p =>
                    {
                        var kind = p?["kind"]?.GetValue<string>();
                        var media = p?["mediaType"]?.GetValue<string>();
                        var hasText = p?["text"] != null;
                        return $"kind={kind ?? "-"},mediaType={media ?? "-"},hasText={hasText}";
                    }) ?? [];
                    descriptions.Add($"{name}[{string.Join(" | ", partDescriptions)}]");
                }
            }

            var statusParts = (task?["status"]?["message"]?["parts"] as JsonArray)?.Count ?? 0;

            _logger.LogInformation(
                "Work IQ A2A artifact shape. agentId={AgentId} outcome={Outcome} statusMessageParts={StatusParts} artifacts={Artifacts}",
                agentId,
                outcome,
                statusParts,
                descriptions.Count == 0 ? "(none)" : string.Join(" ;; ", descriptions));
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Work IQ A2A artifact-shape logging failed for {AgentId}.", agentId);
        }
    }
}
