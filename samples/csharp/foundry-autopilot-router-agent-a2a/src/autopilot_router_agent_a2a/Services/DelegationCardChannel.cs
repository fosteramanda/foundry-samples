using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App.Proactive;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Core.Serialization;
using WorkstreamManager.Models;

namespace WorkstreamManager.Services;

internal static class DelegationCardChannel
{
    internal static Conversation Capture(ITurnContext context, AgentMetadata owner, string? configuredClientId)
    {
        var reference = context.Activity.GetConversationReference();
        if (!string.Equals(reference.Conversation?.TenantId, owner.TenantId.ToString("D"), StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(context.Activity.Recipient?.AgenticUserId, out var recipient) || recipient != owner.UserId)
            throw new UnauthorizedAccessException("The Teams route does not belong to this agent user and tenant.");
        var identity = context.Identity;
        if (identity?.FindFirst("aud")?.Value is { Length: > 0 })
            return new Conversation(identity, reference);
        if (!Guid.TryParse(configuredClientId, out var appId) || appId == Guid.Empty || appId != owner.AgentApplicationId
            || !string.Equals(context.Activity.Recipient?.Role, "agenticUser", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A verified configured agent application is required for proactive Teams delivery.");
        // Agentic ingress may be anonymous inside the hosted container; route outgoing auth through the configured app.
        return new Conversation(AgentClaims.CreateIdentity(appId.ToString("D")), reference);
    }

    internal static Conversation ReadConversation(string json, string conversationId, string tenantId)
    {
        var conversation = ProtocolJsonSerializer.ToObject<Conversation>(json)
            ?? throw new InvalidOperationException("The saved Teams delivery reference is unavailable.");
        if (conversation.Reference?.Conversation?.Id != conversationId
            || conversation.Reference.ChannelId?.ToString() != "msteams"
            || !string.Equals(conversation.Reference.Conversation.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The saved Teams delivery reference does not match this tenant and conversation.");
        if (string.IsNullOrWhiteSpace(conversation.Identity.FindFirst("aud")?.Value))
            throw new InvalidOperationException("The saved Teams route has no application audience. Refresh it through an authorized personal Teams turn.");
        return conversation;
    }

    internal static Task<ResourceResponse> SendAsync(
        IChannelAdapter adapter, Conversation conversation, Activity activity, CancellationToken token) =>
        Proactive.SendActivityAsync(adapter, conversation, activity, token);

    internal static async Task UpdateAsync(
        IChannelAdapter adapter, Conversation conversation, Activity activity, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(activity.Id))
            throw new InvalidOperationException("An existing card message ID is required for an update.");
        await adapter.ContinueConversationAsync(conversation.Identity, conversation.Reference,
            async (context, cancellation) => { await context.UpdateActivityAsync(activity, cancellation); }, token);
    }
}
