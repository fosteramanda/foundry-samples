using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App.Proactive;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Core.Serialization;

namespace WorkstreamManager.Services;

internal static class DelegationCardChannel
{
    internal static Conversation ReadConversation(string json, string conversationId, string tenantId)
    {
        var conversation = ProtocolJsonSerializer.ToObject<Conversation>(json)
            ?? throw new InvalidOperationException("The saved Teams delivery reference is unavailable.");
        if (conversation.Reference?.Conversation?.Id != conversationId
            || conversation.Reference.ChannelId?.ToString() != "msteams"
            || !string.Equals(conversation.Reference.Conversation.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The saved Teams delivery reference does not match this tenant and conversation.");
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
