namespace WorkstreamManager.AgentLogic.ResponsesApi;

using WorkstreamManager.Models;
using WorkstreamManager.Services;
using WorkstreamManager.AgentLogic.ResponsesApi.Helpers;
using Microsoft.Agents.A365.Notifications.Models;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;

/// <summary>
/// OpenAI Responses API-based implementation of AgentLogicService.
/// Uses MCP tool definitions directly via the Responses API's native MCP support.
/// </summary>
public class ResponsesApiAgentLogicService : IAgentLogicService
{
    private readonly ILogger _logger;
    private readonly IConfiguration _configuration;
    private readonly ResponsesApiClient _responsesApiClient;
    private readonly WorkItemToolHandler _workItemTools;
    private readonly TeamsActivityHelper _teamsHelper;
    private readonly AccessControlService _accessControl;
    private readonly AddressedToAgentGate _addressedToAgentGate;
    private readonly ReactionService _reactionService;

    public ResponsesApiAgentLogicService(
        AgentMetadata agent,
        IConfiguration configuration,
        ILogger logger,
        string accessToken,
        List<McpServerConfig> mcpServers,
        string? graphAccessToken = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var agentMetadata = agent ?? throw new ArgumentNullException(nameof(agent));

        var httpClient = new HttpClient();
        _responsesApiClient = new ResponsesApiClient(agentMetadata, _logger, _configuration, accessToken, mcpServers, httpClient);
        _reactionService = new ReactionService(_logger, graphAccessToken, httpClient);

        // Initialize WorkItemToolHandler
        WorkItemService? workItemService = null;
        var workItemsTableServiceUri = configuration["WorkItemsTableServiceUri"];
        if (!string.IsNullOrEmpty(workItemsTableServiceUri))
        {
            workItemService = new WorkItemService(configuration, new LoggerFactory().CreateLogger<WorkItemService>());
        }
        _workItemTools = new WorkItemToolHandler(agentMetadata, _logger, graphAccessToken, httpClient, workItemService, _reactionService);
        _teamsHelper = new TeamsActivityHelper(_logger);
        _accessControl = new AccessControlService(agentMetadata, _logger, _configuration, graphAccessToken, httpClient, _teamsHelper, _workItemTools);
        _addressedToAgentGate = new AddressedToAgentGate(_logger, _configuration, _responsesApiClient, _teamsHelper, httpClient, graphAccessToken);
    }

    public async Task NewActivityReceived(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        var incomingText = turnContext.Activity.Text;
        _logger.LogInformation("New activity received (Responses API): {IncomingText}", incomingText);

        var sender = turnContext.Activity.From;
        var rawUserMessage = incomingText ?? string.Empty;

        // Global AP tenant guard: if we can determine that the sender is from outside this
        // digital worker's tenant, return a deterministic canned response and skip LLM work.
        if (await _accessControl.TryHandleCrossTenantActivityAsync(turnContext, cancellationToken))
        {
            return;
        }

        if (turnContext.Activity.ChannelId == "msteams")
        {
            incomingText = $"Respond to this chat message with chat id {turnContext.Activity.Conversation.Id} " +
                           $"From: {sender?.Name} ({sender?.Id})\n" +
                           $"Message: {incomingText}";
        }
        else if (turnContext.Activity.Type == ActivityTypes.InstallationUpdate)
        {
            incomingText = $"You were just added as a digital worker. Please introduce yourself to {sender!.Id} with information on what you can do.";
        }

        var conversationId = turnContext.Activity.Conversation?.Id ?? "default";
        // Optional DM access control: in Teams 1:1 chats, only this digital worker's resolved
        // manager can trigger an LLM call. Everyone else gets a deterministic canned response.
        if (await _accessControl.TryHandleRestrictedDirectMessageAsync(turnContext, cancellationToken))
        {
            return;
        }

        // Optional group-chat access control: in Teams group chats, every participant must be
        // manager-approved (manager or allowlisted) before any LLM-based processing occurs.
        if (await _accessControl.TryHandleRestrictedGroupChatAsync(turnContext, cancellationToken))
        {
            return;
        }

        // Only respond if the message is actually addressed to this agent. In 1:1 personal
        // chats every message is by definition agent-directed; in group chats / channels we
        // use a mix of structured @-mention detection + a YES/NO LLM judge so the agent only
        // chimes in when actually named (or referenced via "you" in an active thread it has
        // been participating in). The verdict also captures whether the user explicitly
        // @-mentioned the agent so the response can mirror that addressing register.
        var verdict = await _addressedToAgentGate.ShouldRespondAsync(turnContext, rawUserMessage, conversationId);
        if (!verdict.ShouldRespond)
        {
            _logger.LogInformation(
                "Skipping reply: message not addressed to agent. activityId={ActivityId} channelId={ChannelId} conversationId={ConversationId}",
                turnContext.Activity.Id,
                turnContext.Activity.ChannelId,
                conversationId);

            // Even when the agent isn't being addressed, optionally run the passive scanner
            // for commitments worth tracking. If a work item gets captured, the 📌 reaction
            // fires automatically and the agent stays silent (no text reply).
            await TryPassiveWorkItemDetectionAsync(turnContext, rawUserMessage, conversationId);
            return;
        }

        // 👍 acknowledges the message the agent is about to answer, while the LLM call runs.
        // Fire-and-forget so it never delays the reply; CancellationToken.None because the
        // turn's token gets disposed when this method returns and we want the reaction POST
        // to complete on its own. If this turn ends up capturing a work item, the 📌 posted
        // by create_work_item overwrites the 👍 (Graph setReaction keeps one reaction per bot
        // per message), which is the stronger "this was tracked" signal.
        if (turnContext.Activity.Type == ActivityTypes.Message)
        {
            _ = _reactionService.SetReactionAsync("👍", turnContext.Activity, CancellationToken.None);
        }

        // Capture activity context for 📌 reaction on work item creation
        _workItemTools.SetCurrentActivityContext(turnContext.Activity);

        var response = await _responsesApiClient.InvokeAsync(
            input: incomingText ?? string.Empty,
            conversationId: conversationId,
            additionalTools: _workItemTools.GetToolDefinitions(),
            localToolExecutor: _workItemTools.TryExecuteAsync);

        // For Teams group chat / channel we send a regular activity so the groupchat features
        // (@-mention entity + Teams reply blockquote) flow through unchanged. StreamingResponse
        // .QueueTextChunk delivers text only, not activity entities, so it cannot carry mention
        // markup. For 1:1 chats we use the streaming text path so the typing indicator the
        // Message handler opened in A365AgentApplication has a final chunk to render.
        //
        // The streaming path is additionally gated on the EnableStreamingUpdates config flag.
        // The Message handler only opens a stream (via QueueInformativeUpdateAsync) when that
        // flag is true; if we queued text here while the flag is false there would be no
        // opened stream to render into, so we must fall through to SendActivityAsync instead.
        var enableStreamingUpdates = _configuration.GetValue<bool>("EnableStreamingUpdates");
        var outChannelId = turnContext.Activity.ChannelId?.ToString();
        var outConversationType = turnContext.Activity.Conversation?.ConversationType;
        var outIsGroup = turnContext.Activity.Conversation?.IsGroup;
        var isTeamsGroupOrChannel = string.Equals(outChannelId, "msteams", StringComparison.OrdinalIgnoreCase)
            && (outIsGroup == true
                || string.Equals(outConversationType, "groupChat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(outConversationType, "channel", StringComparison.OrdinalIgnoreCase));

        if (turnContext.Activity.Type == ActivityTypes.Message && !isTeamsGroupOrChannel && enableStreamingUpdates)
        {
            var finalText = string.IsNullOrWhiteSpace(response) ? "Done." : response;
            turnContext.StreamingResponse.QueueTextChunk(finalText);
        }
        else if (!string.IsNullOrEmpty(response))
        {
            var outboundActivity = _teamsHelper.BuildResponseActivity(
                turnContext,
                response,
                includeMention: verdict.WasExplicitlyMentioned);
            await turnContext.SendActivityAsync(outboundActivity, cancellationToken);
        }
    }

    /// <summary>
    /// Passive work-item detection pass for messages NOT addressed to the agent. The agent
    /// silently scans the message for commitments worth tracking. If create_work_item is called
    /// the 📌 reaction fires automatically via WorkItemToolHandler. The LLM is instructed to
    /// produce no text response either way - this never adds a chat message. Skipped entirely
    /// when disabled by config or when work-item tools aren't configured.
    /// </summary>
    private async Task TryPassiveWorkItemDetectionAsync(
        ITurnContext turnContext,
        string userMessage,
        string conversationId)
    {
        var enablePassiveWorkItemDetection = _configuration.GetValue("EnablePassiveWorkItemDetection", true);
        if (!enablePassiveWorkItemDetection)
        {
            _logger.LogInformation(
                "Passive work-item detection disabled via config; skipping. activityId={ActivityId} conversationId={ConversationId}",
                turnContext.Activity.Id,
                conversationId);
            return;
        }

        var toolDefinitions = _workItemTools.GetToolDefinitions();
        if (toolDefinitions.Count == 0)
        {
            return;
        }

        if (turnContext.Activity.Type != ActivityTypes.Message)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(userMessage))
        {
            return;
        }

        // Tell WorkItemToolHandler which message to react to if a work item is captured.
        _workItemTools.SetCurrentActivityContext(turnContext.Activity);

        var sender = turnContext.Activity.From;
        var observed =
            $"Observed message in chat {turnContext.Activity.Conversation?.Id} " +
            $"from {sender?.Name} ({sender?.Id}): {userMessage}";

        var instructions =
            "You are a silent observer of a Teams group chat. Your ONLY job is to detect " +
            "commitments and action items mentioned in the message below and, if one is present " +
            "with a clear owner AND a clear deliverable, call create_work_item to track it.\n" +
            "\n" +
            "Examples of trackable commitments (CAPTURE these):\n" +
            "- \"Amanda will file a bug for that.\"\n" +
            "- \"Sustineo, remember to add notes to the doc by tomorrow.\"\n" +
            "- \"Can you revise the wording on the Figma screen by Friday?\"\n" +
            "- \"I'll send the recap by EOD.\"\n" +
            "\n" +
            "Do NOT capture:\n" +
            "- Questions, opinions, jokes, or general discussion.\n" +
            "- Past tense / already-completed work (\"I sent that yesterday\").\n" +
            "- Anything without a clear owner OR a clear deliverable.\n" +
            "- Hypothetical or aspirational statements (\"we should probably ...\").\n" +
            "\n" +
            "You MUST return an empty string as your text response. The user is NOT talking to " +
            "you - do NOT greet, confirm, explain, or ask clarifying questions. The 📌 reaction " +
            "posted on create_work_item is the only signal you may produce. If no trackable " +
            "commitment is present, do nothing and return empty.\n" +
            "\n" +
            "When you DO call create_work_item, infer name (short title), description, owner, " +
            "and eta from the message. Convert relative dates (\"tomorrow\", \"end of next week\") " +
            "to absolute ISO 8601 datetimes. If the owner isn't named, do NOT capture (a " +
            "commitment without an owner isn't trackable).";

        try
        {
            _logger.LogInformation(
                "Passive work-item detection: scanning message. activityId={ActivityId} senderName={SenderName} conversationId={ConversationId}",
                turnContext.Activity.Id,
                sender?.Name,
                conversationId);

            await _responsesApiClient.InvokeAsync(
                input: observed,
                conversationId: conversationId,
                instructionsOverride: instructions,
                includeMcpTools: false,
                persistResponseId: false,
                // Intentionally stateful: reuses prior conversation context to improve
                // multi-turn commitment capture (for example "Yep, I'll do it by Friday.").
                usePreviousResponseId: true,
                additionalTools: toolDefinitions,
                localToolExecutor: _workItemTools.TryExecuteAsync);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Passive work-item detection failed; ignoring. activityId={ActivityId} conversationId={ConversationId}", turnContext.Activity.Id, conversationId);
        }
    }

    public Task HandleCommentNotificationAsync(ITurnContext turnContext, ITurnState turnState, AgentNotificationActivity commentEvent)
    {
        _logger.LogInformation("Processing comment notification (Responses API)");
        return Task.CompletedTask;
    }

    public Task HandleTeamsMessageAsync(ITurnContext turnContext, ITurnState turnState, AgentNotificationActivity teamsEvent)
    {
        _logger.LogInformation("Processing Teams message (Responses API)");
        return Task.CompletedTask;
    }

    public Task HandleInstallationUpdateAsync(ITurnContext turnContext, ITurnState turnState, AgentNotificationActivity installationEvent)
    {
        _logger.LogInformation("Processing installation update (Responses API)");
        return Task.CompletedTask;
    }

}

