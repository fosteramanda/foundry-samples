using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Agents.Core.Models;

namespace WorkstreamManager.Services;

internal sealed class ScheduledChatDelivery(HttpClient httpClient, string? graphToken, ILogger logger)
{
    internal static bool IsScheduledChat(IActivity activity) =>
        activity.Type == ActivityTypes.Message
        && string.Equals(activity.ChannelId?.ToString(), "msteams", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrWhiteSpace(activity.Id)
        && !string.IsNullOrWhiteSpace(activity.Conversation?.Id);

    internal async Task SendAsync(IActivity activity, string html, CancellationToken cancellationToken)
    {
        if (!IsScheduledChat(activity) || string.IsNullOrWhiteSpace(graphToken) || string.IsNullOrWhiteSpace(html))
        {
            logger.LogError("Scheduled chat delivery lacks its activity, agent-user token, or content.");
            throw new InvalidOperationException("Scheduled chat delivery is not configured.");
        }
        var chatId = activity.Conversation!.Id;
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.microsoft.com/v1.0/chats/{Uri.EscapeDataString(chatId)}/messages");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", graphToken);
        request.Content = new StringContent(new JsonObject
        {
            ["body"] = new JsonObject { ["contentType"] = "html", ["content"] = html }
        }.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Scheduled chat Graph delivery failed: HTTP {Status}.", (int)response.StatusCode);
            throw new HttpRequestException("Scheduled chat Graph delivery was rejected.", null, response.StatusCode);
        }
        var id = JsonNode.Parse(body)?["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id))
        {
            logger.LogError("Scheduled chat delivery returned no message receipt; not retrying.");
            throw new InvalidOperationException("Scheduled chat delivery is unconfirmed.");
        }
        logger.LogInformation("Scheduled chat delivered through Graph: chat={ChatId} message={MessageId}", chatId, id);
    }
}
