using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using WorkstreamManager.AgentLogic.ResponsesApi.Helpers;
using WorkstreamManager.Models;

namespace WorkstreamManager.Services;

/// <summary>
/// Delivers answers from delegations that outlived their turn.
///
/// Why this exists: an A2A agent can accept a request and finish minutes later. Blocking the turn
/// on it would freeze the chat, and abandoning it would drop the question silently — the failure
/// mode this sample exists to expose. So the turn ends immediately with "I have asked X", and this
/// service collects the answer afterwards and posts it back into the same conversation.
///
/// Answers arrive out of order, which is why every follow-up restates the question it belongs to.
/// A bare answer landing after two unrelated turns is unreadable.
/// </summary>
public class DelegationFollowUpService(
    PendingDelegationStore store,
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<DelegationFollowUpService> logger) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(
        configuration.GetValue("DelegationFollowUpIntervalSeconds", 20));

    // Give up eventually. A task stuck in WORKING forever would otherwise be polled for the life
    // of the container, and the user would never learn that no answer is coming.
    private readonly int _maxAttempts = configuration.GetValue("DelegationFollowUpMaxAttempts", 45);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!store.IsAvailable)
        {
            logger.LogWarning(
                "Delegation follow-up disabled: no durable store configured. Slow agents will be " +
                "reported as still working and never followed up.");
            return;
        }

        var partitionKey = configuration["FoundryAgentName"]
            ?? Environment.GetEnvironmentVariable("FOUNDRY_AGENT_NAME")
            ?? "default";

        logger.LogInformation(
            "Delegation follow-up poller started. interval={Interval}s maxAttempts={MaxAttempts} partition={Partition}",
            _interval.TotalSeconds,
            _maxAttempts,
            partitionKey);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(partitionKey, stoppingToken);
            }
            catch (Exception ex)
            {
                // Never let one bad cycle kill the poller; every remaining pending delegation
                // would silently never be delivered.
                logger.LogError(ex, "Delegation follow-up cycle failed; continuing.");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollOnceAsync(string partitionKey, CancellationToken cancellationToken)
    {
        var pending = await store.ListAsync(partitionKey);
        if (pending.Count == 0)
        {
            return;
        }

        logger.LogInformation("Delegation follow-up: {Count} pending.", pending.Count);

        foreach (var item in pending)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var answer = await TryCollectAnswerAsync(item);

            if (answer == null)
            {
                item.Attempts++;
                if (item.Attempts >= _maxAttempts)
                {
                    // Tell the user rather than quietly forgetting. An unanswered question the
                    // user believes is still coming is worse than a clear "it never answered".
                    var delivered = await DeliverAsync(
                        item,
                        $"<p><i>\U0001F517 <b>{System.Net.WebUtility.HtmlEncode(item.DisplayName)}</b> never returned an answer " +
                        $"to your earlier question ({System.Net.WebUtility.HtmlEncode(Trim(item.Question))}). " +
                        "I have stopped waiting for it.</i></p>", null, timedOut: true);
                    if (delivered) await store.RemoveAsync(item.PartitionKey, item.RowKey);
                }
                else
                {
                    await store.UpdateAsync(item);
                }

                continue;
            }

            var name = System.Net.WebUtility.HtmlEncode(item.DisplayName);
            var body =
                $"<p><i>Following up on your earlier question: \u201C{System.Net.WebUtility.HtmlEncode(Trim(item.Question))}\u201D</i></p>" +
                $"<p>{answer}</p>" +
                $"<p><i>\U0001F517 Delegated to: <b>{name}</b></i></p>";

            if (await DeliverAsync(item, body, answer))
                await store.RemoveAsync(item.PartitionKey, item.RowKey);
        }
    }

    private async Task<string?> TryCollectAnswerAsync(PendingDelegationEntity item)
    {
        try
        {
            // Rebuild the delegating agent's identity: the poll must present the same agent-user
            // credential the original send used, and there is no inbound activity out here to
            // derive it from.
            var metadata = new AgentMetadata
            {
                UserId = Guid.TryParse(item.OwnerUserId, out var u) ? u : Guid.Empty,
                AgentId = Guid.TryParse(item.OwnerAgentId, out var a) ? a : Guid.Empty,
                AgentApplicationId = Guid.TryParse(item.OwnerAppId, out var ap) ? ap : Guid.Empty,
                TenantId = Guid.TryParse(item.OwnerTenantId, out var t) ? t : Guid.Empty,
            };

            using var scope = services.CreateScope();
            var tokenHelper = scope.ServiceProvider.GetService<AgentTokenHelper>();
            var httpClientFactory = scope.ServiceProvider.GetService<IHttpClientFactory>();
            var httpClient = httpClientFactory?.CreateClient() ?? new HttpClient();

            var handler = new WorkIqA2AToolHandler(metadata, tokenHelper, logger, httpClient, configuration);
            return await handler.TryFetchTaskAnswerAsync(item.A2AUrl, item.AgentId, item.TaskId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Follow-up poll failed for agent {AgentId} task {TaskId}; will retry.",
                item.AgentId,
                item.TaskId);
            return null;
        }
    }

    private async Task<bool> DeliverAsync(PendingDelegationEntity item, string html, string? answer, bool timedOut = false)
    {
        try
        {
            using var scope = services.CreateScope();
            var agent = scope.ServiceProvider.GetService<WorkstreamManager.AgentLogic.A365AgentApplication>();
            var adapter = scope.ServiceProvider.GetService<IChannelAdapter>();

            if (agent == null || adapter == null)
            {
                logger.LogError(
                    "Cannot deliver follow-up for {AgentId}: agent={AgentResolved} adapter={AdapterResolved}. " +
                    "The answer was retrieved but cannot be sent.",
                    item.AgentId,
                    agent != null,
                    adapter != null);
                return false;
            }

            var conversationState = scope.ServiceProvider.GetService<ConversationStateStore>();
            if (configuration.GetValue<bool>("EnableDelegationCards")
                && !string.IsNullOrWhiteSpace(item.CardId) && conversationState?.Table != null)
            {
                var metadata = new AgentMetadata
                {
                    TenantId = Guid.Parse(item.OwnerTenantId), UserId = Guid.Parse(item.OwnerUserId),
                    AgentId = Guid.Parse(item.OwnerAgentId), AgentApplicationId = Guid.Parse(item.OwnerAppId)
                };
                var cards = new DelegationCardStore(conversationState.Table);
                var card = await cards.GetAsync($"{metadata.TenantId:D}:{metadata.UserId:D}", item.CardId);
                if (card != null && !string.IsNullOrWhiteSpace(card.ActivityId))
                {
                    var conversation = DelegationCardChannel.ReadConversation(
                        card.ConversationJson, card.ConversationId, metadata.TenantId.ToString("D"));
                    var factory = scope.ServiceProvider.GetRequiredService<
                        WorkstreamManager.AgentLogic.ResponsesApi.ResponsesApiAgentLogicServiceFactory>();
                    var logic = await factory.CreateForAgentAsync(metadata);
                    var delivered = false;
                    await agent.Proactive.ContinueConversationAsync(adapter, conversation, async (context, _, token) =>
                    {
                        if (logic is not WorkstreamManager.AgentLogic.ResponsesApi.ResponsesApiAgentLogicService current
                            || !await current.CanDeliverDelegationCardAsync(context, token))
                        {
                            logger.LogWarning("Pending card delivery is no longer authorized. card={CardId}", item.CardId);
                            return;
                        }
                        var tokenHelper = scope.ServiceProvider.GetRequiredService<AgentTokenHelper>();
                        using var http = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient();
                        var router = new WorkIqA2AToolHandler(metadata, tokenHelper, logger, http, configuration,
                            conversationState, interactions: cards);
                        var caller = new StandingJobCaller(card.RequesterId, card.ManagerId, null);
                        var coordinator = new DelegationCardCoordinator(
                            metadata, cards, router, card.ConversationId, card.ConversationJson, card.OwnerName, caller,
                            async activity => (await context.SendActivityAsync(activity, token)).Id,
                            async activity => { await context.UpdateActivityAsync(activity, token); },
                            async text => { await context.SendActivityAsync(MessageFactory.Text(text), token); },
                            null, logger);
                        if (item.CardVerification)
                            await coordinator.CompleteVerificationAsync(card, answer ?? "", timedOut);
                        else
                            await coordinator.CompletePendingAsync(card, answer ?? "", timedOut);
                        foreach (var next in router.PendingHandoffs.Where(handoff => handoff.CardVerification))
                        {
                            var queued = await store.AddAsync(new PendingDelegationEntity
                            {
                                PartitionKey = item.PartitionKey, AgentId = next.AgentId, DisplayName = next.DisplayName,
                                TaskId = next.TaskId, A2AUrl = next.A2AUrl, Question = next.Question,
                                ProactiveConversationId = item.ProactiveConversationId, CardId = next.CardId ?? item.CardId,
                                CardVerification = true, ConversationJson = card.ConversationJson,
                                OwnerUserId = item.OwnerUserId, OwnerAgentId = item.OwnerAgentId,
                                OwnerAppId = item.OwnerAppId, OwnerTenantId = item.OwnerTenantId
                            });
                            if (!queued) throw new InvalidOperationException("The late verification could not be queued; the original handoff remains pending.");
                        }
                        delivered = true;
                    }, cancellationToken: CancellationToken.None);
                    return delivered;
                }
                logger.LogWarning("The pending delegation has no usable card receipt; delivering its actual text result. card={CardId}", item.CardId);
            }

            var activity = MessageFactory.Text(html);
            activity.TextFormat = "xml";

            await agent.Proactive.SendActivityAsync(
                adapter,
                item.ProactiveConversationId,
                activity,
                CancellationToken.None);

            logger.LogInformation(
                "Follow-up delivered for agent {AgentId} task {TaskId}.",
                item.AgentId,
                item.TaskId);
            return true;
        }
        catch (Exception ex)
        {
            // Do not remove the row on a delivery failure elsewhere in the caller — losing the
            // row here would mean the user never hears back at all.
            logger.LogError(
                ex,
                "Failed to deliver follow-up for agent {AgentId} task {TaskId}.",
                item.AgentId,
                item.TaskId);
            return false;
        }
    }

    private static string Trim(string s) =>
        string.IsNullOrWhiteSpace(s) ? "(question unavailable)"
        : s.Length <= 120 ? s
        : s[..117] + "...";
}
