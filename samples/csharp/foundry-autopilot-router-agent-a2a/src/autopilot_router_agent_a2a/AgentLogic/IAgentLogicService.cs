namespace WorkstreamManager.AgentLogic;

using Microsoft.Agents.A365.Notifications.Models;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.State;

public interface IAgentLogicService
{
    /// <summary>
    /// Handles email notification events
    /// </summary>
    Task HandleEmailNotificationAsync(ITurnContext turnContext, ITurnState turnState, AgentNotificationActivity emailEvent);

    /// <summary>
    /// Handles document comment notification events (Word, Excel, PowerPoint)
    /// </summary>
    Task HandleCommentNotificationAsync(ITurnContext turnContext, ITurnState turnState, AgentNotificationActivity commentEvent);

    /// <summary>
    /// Handles Teams message events
    /// </summary>
    Task HandleTeamsMessageAsync(ITurnContext turnContext, ITurnState turnState, AgentNotificationActivity teamsEvent);

    /// <summary>
    /// Handles installation update events
    /// </summary>
    Task HandleInstallationUpdateAsync(ITurnContext turnContext, ITurnState turnState, AgentNotificationActivity installationEvent);

    /// <summary>
    /// Handles a standard activity protocol message
    /// </summary>
    /// <returns></returns>
    Task NewActivityReceived(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken);

    /// <summary>
    /// True when the turn just handed work to another agent that had not finished in time.
    /// The host uses this to decide whether it is worth capturing a proactive conversation.
    /// </summary>
    bool HasPendingDelegations => false;

    /// <summary>
    /// Persists delegations still in flight, so a background poller can deliver their answers
    /// after the turn ends. Called only when <see cref="HasPendingDelegations"/> is true, with
    /// the id of a conversation the host has already stored for proactive delivery.
    ///
    /// Default no-op so implementations without asynchronous delegation are unaffected.
    /// </summary>
    Task PersistPendingDelegationsAsync(string proactiveConversationId) => Task.CompletedTask;
}

