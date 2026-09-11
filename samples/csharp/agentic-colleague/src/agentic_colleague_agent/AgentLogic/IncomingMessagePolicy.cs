using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.Core.Models;

namespace AgenticColleague.AgentLogic;

public static class IncomingMessagePolicy
{
    public static bool ShouldIgnore(IActivity activity)
    {
        if (activity.Type != ActivityTypes.Message || activity.ChannelId != "msteams") { return false; }
        var data = JsonSerializer.SerializeToNode(activity.ChannelData);
        if (data is System.Text.Json.Nodes.JsonObject channel
            && (channel["messageType"]?.ToString() == "systemEventMessage"
                || channel["eventType"]?.ToString() is "meetingStart" or "meetingEnd"))
        {
            return true;
        }

        if (HasText(activity.Text) || activity.Value != null) { return false; }
        return !activity.Attachments?.Any(attachment =>
            !string.Equals(attachment.ContentType, "text/html", StringComparison.OrdinalIgnoreCase)
            || HasText(attachment.Content?.ToString())) ?? true;
    }

    private static bool HasText(string? text) => !string.IsNullOrWhiteSpace(
        WebUtility.HtmlDecode(Regex.Replace(text ?? string.Empty, "<[^>]*>", string.Empty)));
}
