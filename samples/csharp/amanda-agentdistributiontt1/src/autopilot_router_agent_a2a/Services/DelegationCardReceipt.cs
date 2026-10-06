using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Agents.Core.Models;

namespace WorkstreamManager.Services;

internal static class DelegationCardReceipt
{
    internal static async Task<string> ResolveAsync(
        string? immediateId, Activity activity, HttpClient http, string? graphToken,
        string conversationId, Guid agentUserId, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(immediateId)) return immediateId;
        var marker = activity.Attachments?.SingleOrDefault()?.Name;
        if (string.IsNullOrWhiteSpace(marker) || !marker.StartsWith("delegation-card-", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(graphToken) || string.IsNullOrWhiteSpace(conversationId))
            throw new InvalidOperationException("The card was accepted without a message ID and its exact receipt cannot be read. It was not sent again.");
        var url = "https://graph.microsoft.com/v1.0/chats/" + Uri.EscapeDataString(conversationId)
            + "/messages?$top=30";
        for (var attempt = 0; attempt < 8; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", graphToken);
            using var response = await http.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var matches = Find(document.RootElement, marker, agentUserId);
            if (matches.Count > 1)
                throw new InvalidOperationException("More than one sent card matches this receipt. No arbitrary message was selected.");
            if (matches.Count == 1) return matches[0];
            await Task.Delay(TimeSpan.FromMilliseconds(750), token);
        }
        throw new InvalidOperationException("The accepted card's exact message receipt did not become available. It was not sent again.");
    }

    internal static IReadOnlyList<string> Find(JsonElement root, string marker, Guid owner)
    {
        if (!root.TryGetProperty("value", out var messages) || messages.ValueKind != JsonValueKind.Array)
            throw new JsonException("The chat-message response contains no message collection.");
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages.EnumerateArray())
        {
            if (!message.TryGetProperty("from", out var from) || from.ValueKind != JsonValueKind.Object
                || !from.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object
                || !user.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || !Guid.TryParse(id.GetString(), out var sender) || sender != owner
                || !message.TryGetProperty("id", out var messageId) || messageId.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(messageId.GetString())
                || !message.TryGetProperty("attachments", out var attachments) || attachments.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var attachment in attachments.EnumerateArray())
            {
                if (!attachment.TryGetProperty("contentType", out var type)
                    || type.GetString() != "application/vnd.microsoft.card.adaptive"
                    || !attachment.TryGetProperty("content", out var content))
                    continue;
                using var card = JsonDocument.Parse(content.ValueKind == JsonValueKind.String
                    ? content.GetString() ?? "{}" : content.GetRawText());
                if (HasMarker(card.RootElement, marker))
                    result.Add(messageId.GetString()!);
            }
        }
        return result.ToArray();
    }

    private static bool HasMarker(JsonElement node, string marker)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == marker)
                return true;
            return node.EnumerateObject().Any(property => HasMarker(property.Value, marker));
        }
        return node.ValueKind == JsonValueKind.Array && node.EnumerateArray().Any(value => HasMarker(value, marker));
    }
}
