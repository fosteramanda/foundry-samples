namespace WorkstreamManager.AgentLogic;

using WorkstreamManager.AgentLogic.ResponsesApi;
using WorkstreamManager.Models;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Core.Models;
using AgentNotification;
using Microsoft.Agents.A365.Notifications.Models;
using System.Collections.Concurrent;

/// <summary>
/// This is main handler for incoming activities, and is linked to Agent SDK infrastructure.
/// This will need to resolve the incoming activity to the correct agent instance.
/// </summary>
public class A365AgentApplication : AgentApplication
{
    private readonly ResponsesApiAgentLogicServiceFactory _factory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<A365AgentApplication> _logger;

    // Process-local dedupe for message activities. Foundry's hosted-agent passthrough is
    // at-least-once: on a cold-start race the upstream YARP forwarder can return 5xx and
    // Bot Service will retry, but both deliveries land on the same (now-warm) container.
    // Skipping the second delivery prevents duplicate Graph setReaction calls, duplicate
    // Responses API invocations (which would otherwise share previous_response_id), and
    // duplicate outgoing replies.
    private static readonly ConcurrentDictionary<string, DateTime> _processedActivityIds = new();
    private static readonly TimeSpan _activityDedupeTtl = TimeSpan.FromMinutes(5);
    private static DateTime _nextDedupeSweepUtc = DateTime.UtcNow + TimeSpan.FromMinutes(1);

    public A365AgentApplication(
        AgentApplicationOptions options,
        ResponsesApiAgentLogicServiceFactory factory,
        ILogger<A365AgentApplication> logger,
        IConfiguration configuration) : base(options)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        // Configure the agent to handle message activities
        ConfigureMessageHandling();
        _logger = logger;
    }

    /// <summary>
    /// Configures message handling for the agent.
    /// </summary>
    private void ConfigureMessageHandling()
    {
        // Handle Email notifications using the AgentNotification extension. Inbound emails are
        // delivered here by the A365 channel; HandleEmailNotificationAsync drafts a reply and
        // sends it back via the activity protocol (no Mail MCP tool required).
        this.OnAgenticEmailNotification(async (turnContext, turnState, agentNotificationActivity, cancellationToken) =>
        {
            var agent = await GetAgentFromRecipient(turnContext.Activity);
            var agentService = await _factory.CreateAsync(agent, turnContext, UserAuthorization);
            await agentService.HandleEmailNotificationAsync(turnContext, turnState, agentNotificationActivity);
        });

        // Handle Word notifications
        this.OnAgenticWordNotification(async (turnContext, turnState, agentNotificationActivity, cancellationToken) =>
        {
            var agent = await GetAgentFromRecipient(turnContext.Activity);
            var agentService = await _factory.CreateAsync(agent, turnContext, UserAuthorization);

            // A Word notification is always a document-comment event, so route it to the
            // dedicated comment handler. That handler retrieves the document + its comments via
            // the Word / OneDrive-SharePoint MCP tools and replies to the comment. The
            // IsMessagingEnabled flag gates Teams-style chat messaging (and is currently always
            // false), so it must not decide whether a Word comment gets handled.
            await agentService.HandleCommentNotificationAsync(turnContext, turnState, agentNotificationActivity);
        });

        // Handle Excel notifications
        this.OnAgenticExcelNotification(async (turnContext, turnState, agentNotificationActivity, cancellationToken) =>
        {
            var agent = await GetAgentFromRecipient(turnContext.Activity);
            var agentService = await _factory.CreateAsync(agent, turnContext, UserAuthorization);

            if (agent.IsMessagingEnabled)
            {
                // Use the specific comment notification handler for Excel documents
                await agentService.HandleCommentNotificationAsync(turnContext, turnState, agentNotificationActivity);
            }
            else
            {
                await agentService.NewActivityReceived(turnContext, turnState, cancellationToken);
            }
        });

        // Handle PowerPoint notifications
        this.OnAgenticPowerPointNotification(async (turnContext, turnState, agentNotificationActivity, cancellationToken) =>
        {
            var agent = await GetAgentFromRecipient(turnContext.Activity);
            var agentService = await _factory.CreateAsync(agent, turnContext, UserAuthorization);

            if (agent.IsMessagingEnabled)
            {
                // Use the specific comment notification handler for PowerPoint documents
                await agentService.HandleCommentNotificationAsync(turnContext, turnState, agentNotificationActivity);
            }
            else
            {
                await agentService.NewActivityReceived(turnContext, turnState, cancellationToken);
            }
        });
        
        OnActivity(ActivityTypes.Message, async (turnContext, turnState, cancellationToken) =>
        {
            // Suppress duplicate deliveries of the same activity (see _processedActivityIds).
            var activityId = turnContext.Activity.Id;
            if (!string.IsNullOrEmpty(activityId))
            {
                var now = DateTime.UtcNow;
                if (!_processedActivityIds.TryAdd(activityId, now))
                {
                    _logger.LogWarning(
                        "Duplicate message activity suppressed. activityId={ActivityId} channelId={ChannelId} conversationId={ConversationId}",
                        activityId,
                        turnContext.Activity.ChannelId,
                        turnContext.Activity.Conversation?.Id);
                    return;
                }

                // Best-effort sweep of expired entries to keep the dictionary bounded.
                if (now >= _nextDedupeSweepUtc)
                {
                    _nextDedupeSweepUtc = now + TimeSpan.FromMinutes(1);
                    var cutoff = now - _activityDedupeTtl;
                    foreach (var kv in _processedActivityIds)
                    {
                        if (kv.Value < cutoff)
                        {
                            _processedActivityIds.TryRemove(kv.Key, out _);
                        }
                    }
                }
            }

            // Reactions (👍 for "I'm replying", 📌 for "I logged a work item") are posted from
            // the agent logic service after the addressed-to-agent gate decides the agent will
            // actually reply, so messages the agent stays silent on get no reaction at all.
            // See ReactionService + ResponsesApiAgentLogicService for the gating.

            // Open a Teams "typing indicator" stream for 1:1 chats so the user sees the
            // agent is working. We skip streaming for Teams group chats and channel
            // conversations because the response there flows through SendActivityAsync with
            // @-mention + reply-blockquote entities; StreamingResponse.QueueTextChunk cannot
            // carry activity entities, so streaming would strip the groupchat markup.
            var inChannelId = turnContext.Activity.ChannelId?.ToString();
            var inConversationType = turnContext.Activity.Conversation?.ConversationType;
            var inIsGroup = turnContext.Activity.Conversation?.IsGroup;
            var isTeamsGroupOrChannel = string.Equals(inChannelId, "msteams", StringComparison.OrdinalIgnoreCase)
                && (inIsGroup == true
                    || string.Equals(inConversationType, "groupChat", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(inConversationType, "channel", StringComparison.OrdinalIgnoreCase));
            var streamingStarted = false;

            try
            {
                _logger.LogInformation("Received message activity: {ActivityId} from {UserId} activity {activity}", turnContext.Activity.Id, turnContext.Activity.From?.Id, System.Text.Json.JsonSerializer.Serialize(turnContext.Activity));
                // Based on the recipient, determine which agent to use
                var agent = await GetAgentFromRecipient(turnContext.Activity);

                // Get agent logic service from factory
                var agentService = await _factory.CreateAsync(agent, turnContext, UserAuthorization);

                // Ignoring all other channel Ids to prevent duplicate notifications.
                if (agent.IsMessagingEnabled && turnContext.Activity.ChannelId != "msteams")
                {
                    return;
                }

                // Let the user know the agent is working on the prompt. Gated behind the
                // EnableStreamingUpdates config flag (default false): when false the response
                // is delivered as a single SendActivityAsync, when true the streaming pipeline
                // surfaces a "Working on your request..." typing indicator that resolves to the
                // streamed text.
                var enableStreamingUpdates = _configuration.GetValue<bool>("EnableStreamingUpdates");
                if (!isTeamsGroupOrChannel && enableStreamingUpdates)
                {
                    await turnContext.StreamingResponse.QueueInformativeUpdateAsync(
                        "Working on your request...",
                        cancellationToken);
                    streamingStarted = true;
                }

                // Execute logic
                await agentService.NewActivityReceived(turnContext, turnState, cancellationToken);

                // If the turn handed work to an agent that had not finished, capture where to
                // deliver the answer. Done only when something is actually outstanding, so the
                // common case writes nothing. StoreConversationAsync must run inside the turn —
                // the conversation reference it captures does not exist outside one.
                if (agentService.HasPendingDelegations)
                {
                    var proactiveConversationId = await Proactive.StoreConversationAsync(turnContext, cancellationToken);
                    await agentService.PersistPendingDelegationsAsync(proactiveConversationId);
                    _logger.LogInformation(
                        "Captured proactive conversation {ConversationId} for pending delegation follow-up.",
                        proactiveConversationId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message activity");

                // Surface the hosted-agent session id alongside the exception so the user can
                // hand it to support / look it up in agent logs.
                var sessionId = Environment.GetEnvironmentVariable("FOUNDRY_AGENT_SESSION_ID");
                var sessionLine = string.IsNullOrEmpty(sessionId) ? "(not set)" : sessionId;
                var errorText =
                    $"Sorry, something went wrong while processing your message.{Environment.NewLine}" +
                    $"FOUNDRY_AGENT_SESSION_ID: {sessionLine}{Environment.NewLine}" +
                    $"Exception:{Environment.NewLine}{ex}";

                if (streamingStarted)
                {
                    try
                    {
                        turnContext.StreamingResponse.QueueTextChunk(errorText);
                    }
                    catch (Exception streamEx)
                    {
                        _logger.LogWarning(streamEx, "Failed to queue error text on streaming response");
                    }
                }
                else
                {
                    await turnContext.SendActivitiesAsync(new IActivity[]
                    {
                        new Activity
                        {
                            Type = ActivityTypes.Message,
                            Text = errorText
                        }
                    }, cancellationToken);
                }
            }
            finally
            {
                if (streamingStarted)
                {
                    try
                    {
                        // Always finalize the stream so the channel renders a final message
                        // instead of falling back to "No text was streamed".
                        await turnContext.StreamingResponse.EndStreamAsync(cancellationToken);
                    }
                    catch (Exception streamEx)
                    {
                        _logger.LogWarning(streamEx, "Failed to end streaming response");
                    }
                }
            }
        });

        // Keep existing handlers for backward compatibility
        OnActivity(ActivityTypes.Event, async (turnContext, turnState, cancellationToken) =>
        {
            var agent = await GetAgentFromRecipient(turnContext.Activity);
            var agentService = await _factory.CreateAsync(agent, turnContext, UserAuthorization);

            await agentService.NewActivityReceived(turnContext, turnState, cancellationToken);
        });

        OnActivity(ActivityTypes.InstallationUpdate, async (turnContext, turnState, cancellationToken) =>
        {
            var agent = await GetAgentFromRecipient(turnContext.Activity);
            var agentService = await _factory.CreateAsync(agent, turnContext, UserAuthorization);

            if (agent.IsMessagingEnabled)
            {
				// Create AgentNotificationActivity for installation updates
				var agentNotificationActivity = new AgentNotificationActivity(turnContext.Activity);
				await agentService.HandleInstallationUpdateAsync(turnContext, turnState, agentNotificationActivity);
            }
            else
            {
                await agentService.NewActivityReceived(turnContext, turnState, cancellationToken);
			}
		});
    }

    private async Task<AgentMetadata> GetAgentFromRecipient(IActivity activity)
    {
        ChannelAccount recipient = activity.Recipient;
        ConversationAccount conversation = activity.Conversation;

        if (recipient == null)  
        {
            throw new ArgumentNullException(nameof(recipient), "Recipient cannot be null.");
        }

        // Recipient will have an ID, but this may not be sufficient to determine the agent.
        // ChannelAccount recipient currently has an AadObjectId, which we can try using to identify the user.
        // If activityProtocol and SDK is changed to pass a new field, we can update this code to use that instead.
        var aadObjectId = Guid.TryParse(recipient.AadObjectId, out var parsedId) ? parsedId : Guid.Empty;
        var id = recipient.Id;
        var tenantId = Guid.TryParse(conversation.TenantId, out var parsedTenantId) ? parsedTenantId : Guid.Empty;
        var metadata = ConstructAgentMetadataFromActivity(activity);

        return metadata;
    }

    private AgentMetadata ConstructAgentMetadataFromActivity(IActivity activity)
    {
        if (activity == null)
        {
            throw new ArgumentNullException(nameof(activity), "Activity cannot be null.");
        }

        var recipient = activity.Recipient;
        var conversation = activity.Conversation;

        if (recipient == null || conversation == null)
        {
            throw new ArgumentException("Activity must have a recipient and conversation.");
        }

        // A scheduled run's recipient carries no tenantId, but the conversation does. Falling
        // back keeps the cross-tenant guard and token acquisition working on routine-driven
        // turns; without it tenantId is Guid.Empty and those checks see a tenant-less activity.
        var tenantId = Guid.TryParse(recipient.TenantId, out var parsedTenantId)
            ? parsedTenantId
            : Guid.TryParse(conversation.TenantId, out var parsedConversationTenantId)
                ? parsedConversationTenantId
                : Guid.Empty;

        // AAI
        var agenticAppId = Guid.TryParse(recipient.AgenticAppId, out var parsedAgenticAppId) ? parsedAgenticAppId : Guid.Empty;

        var agenticUserId = recipient.AgenticUserId ?? recipient.AadObjectId;

        return new AgentMetadata
        {
            UserId = ResolveAgentUserId(agenticUserId, recipient.Id),
            AgentId = agenticAppId,
            AgentApplicationId = recipient.Properties != null
                && recipient.Properties.TryGetValue("agenticAppBlueprintId", out var agentAppBlueprintId)
                && Guid.TryParse(agentAppBlueprintId.ToString(), out var parsedBlueprintId)
                    ? parsedBlueprintId
                    : Guid.TryParse(recipient.Id, out var parsedId) ? parsedId : Guid.Empty,
            TenantId = tenantId,
        };
    }

    /// <summary>
    /// Resolves the agent user's directory object id from the activity's recipient.
    ///
    /// Why this is not just Guid.Parse: a scheduled run (a routine) delivers a SYNTHETIC
    /// activity whose recipient carries only an MRI-style id plus the agentic app and blueprint
    /// ids — no agenticUserId and no aadObjectId, both of which a real Teams activity supplies.
    /// Parsing null threw ArgumentNullException out of GetAgentFromRecipient before any handler
    /// ran, so every scheduled run died silently: the turn was lost, no model call, no email, and
    /// the only visible symptom was a 401 from the catch block trying to post an error back.
    ///
    /// The id is recoverable from the MRI. Teams MRIs take the form "8:orgid:{objectId}", and the
    /// trailing segment is the same GUID that aadObjectId carries on a real activity — verifiable
    /// on any inbound message, where from.id "8:orgid:X" accompanies from.aadObjectId "X".
    /// </summary>
    private static Guid ResolveAgentUserId(string? agenticUserId, string? recipientId)
    {
        if (Guid.TryParse(agenticUserId, out var direct))
        {
            return direct;
        }

        if (!string.IsNullOrWhiteSpace(recipientId))
        {
            var delimiter = recipientId.LastIndexOf(':');
            var candidate = delimiter >= 0 && delimiter < recipientId.Length - 1
                ? recipientId[(delimiter + 1)..]
                : recipientId;

            if (Guid.TryParse(candidate, out var fromMri))
            {
                return fromMri;
            }
        }

        // Fail loudly and specifically. Returning Guid.Empty would let the turn continue and then
        // fail deeper in token acquisition, where the cause is far harder to see.
        throw new ArgumentException(
            $"Could not resolve the agent user id from the activity recipient. " +
            $"agenticUserId/aadObjectId were absent and recipient.Id ('{recipientId}') " +
            "does not contain a GUID.");
    }
}

