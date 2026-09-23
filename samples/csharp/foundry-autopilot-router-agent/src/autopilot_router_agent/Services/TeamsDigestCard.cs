namespace WorkstreamManager.Services;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public sealed record DigestOwner(string Id, string Name);
public sealed record DigestTask(
    string Id, string Title, int PercentComplete, DateTimeOffset? DueDate,
    IReadOnlyList<DigestOwner> Owners);
public sealed record PlannerDigestSnapshot(string BoardName, IReadOnlyList<DigestTask> Tasks);
public sealed record DigestSection(string Heading, string Text);
public sealed record RenderedTeamsDigest(JsonObject Card, string Summary, IReadOnlyList<DigestOwner> Owners);

public static class TeamsDigestCard
{
    public const string ContentType = "application/vnd.microsoft.card.adaptive";
    public const int CardByteBudget = 20 * 1024;
    public const int MessageByteBudget = 28 * 1024;
    public const int VisibleTaskLimit = 6;
    public const int MentionLimit = 20;

    public static RenderedTeamsDigest Build(
        string title, IReadOnlyList<DigestSection> sections, PlannerDigestSnapshot? board,
        DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var tasks = board?.Tasks.Where(task => task.PercentComplete < 100)
            .OrderBy(task => task.DueDate ?? DateTimeOffset.MaxValue)
            .ThenBy(task => task.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        var owners = tasks.SelectMany(task => task.Owners)
            .DistinctBy(owner => owner.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(owner => owner.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var overdue = tasks.Count(task => IsPastDue(task, localNow, timeZone));
        var unassigned = tasks.Count(task => task.Owners.Count == 0);
        var summary = board == null
            ? title
            : $"{title}: {tasks.Length} open, {overdue} past due, {unassigned} unassigned.";

        // The smaller application budget leaves room for Graph's attachment and mention envelope.
        for (var visible = Math.Min(VisibleTaskLimit, tasks.Length); visible >= 0; visible--)
        {
            var body = new JsonArray
            {
                Text(title, size: "Large", weight: "Bolder"),
                Text(
                    $"Updated {localNow.ToString("MMM d, yyyy h:mm tt", CultureInfo.InvariantCulture)} ({timeZone.Id})",
                    size: "Small", subtle: true),
            };

            if (board != null)
            {
                body.Add(new JsonObject
                {
                    ["type"] = "ColumnSet",
                    ["spacing"] = "Medium",
                    ["columns"] = new JsonArray(
                        Metric("Open", tasks.Length),
                        Metric("Past due", overdue, overdue > 0 ? "Attention" : "Default"),
                        Metric("No owner", unassigned, unassigned > 0 ? "Warning" : "Default")),
                });
            }

            foreach (var section in sections)
            {
                body.Add(new JsonObject
                {
                    ["type"] = "Container",
                    ["spacing"] = "Medium",
                    ["separator"] = true,
                    ["items"] = new JsonArray(
                        Text(section.Heading, weight: "Bolder"),
                        Text(section.Text)),
                });
            }

            if (board != null)
            {
                body.Add(new JsonObject
                {
                    ["type"] = "TextBlock",
                    ["text"] = EscapeText(board.BoardName),
                    ["weight"] = "Bolder",
                    ["wrap"] = true,
                    ["spacing"] = "Medium",
                    ["separator"] = true,
                });

                if (tasks.Length == 0)
                {
                    body.Add(Text("No open items.", subtle: true));
                }

                foreach (var task in tasks.Take(visible))
                {
                    var ownerText = task.Owners.Count == 0
                        ? "Unassigned"
                        : string.Join(", ", task.Owners.Select(owner => owner.Name));
                    var state = task.PercentComplete > 0 ? "In progress" : "Not started";
                    var due = task.DueDate.HasValue
                        ? TimeZoneInfo.ConvertTime(task.DueDate.Value, timeZone)
                            .ToString("MMM d", CultureInfo.InvariantCulture)
                        : null;
                    var dueText = due == null ? "No due date"
                        : IsPastDue(task, localNow, timeZone) ? $"Past due {due}" : $"Due {due}";

                    body.Add(new JsonObject
                    {
                        ["type"] = "Container",
                        ["spacing"] = "Medium",
                        ["separator"] = true,
                        ["items"] = new JsonArray(
                            new JsonObject
                            {
                                ["type"] = "ColumnSet",
                                ["columns"] = new JsonArray(
                                    new JsonObject
                                    {
                                        ["type"] = "Column",
                                        ["width"] = "stretch",
                                        ["items"] = new JsonArray(Text(task.Title, weight: "Bolder")),
                                    },
                                    new JsonObject
                                    {
                                        ["type"] = "Column",
                                        ["width"] = "auto",
                                        ["items"] = new JsonArray(Text(dueText, size: "Small",
                                            color: IsPastDue(task, localNow, timeZone) ? "Attention" : "Default")),
                                    }),
                            },
                            Text($"{ownerText} | {state}", size: "Small", subtle: true)),
                    });
                }

                if (visible < tasks.Length)
                {
                    body.Add(Text(
                        $"Showing {visible} of {tasks.Length} open items, earliest due first. Open Planner for the full board.",
                        size: "Small", subtle: true));
                }
            }

            var fallback = new StringBuilder(summary);
            foreach (var section in sections)
            {
                fallback.Append($"\n{section.Heading}: {section.Text}");
            }
            foreach (var task in tasks.Take(visible))
            {
                fallback.Append($"\n{task.Title}: ");
                fallback.Append(task.Owners.Count == 0 ? "Unassigned"
                    : string.Join(", ", task.Owners.Select(owner => owner.Name)));
            }
            if (visible < tasks.Length)
            {
                fallback.Append($"\nShowing {visible} of {tasks.Length} items. Open Planner for all items.");
            }

            var card = new JsonObject
            {
                ["$schema"] = "https://adaptivecards.io/schemas/adaptive-card.json",
                ["type"] = "AdaptiveCard",
                ["version"] = "1.2",
                ["fallbackText"] = fallback.ToString(),
                ["msteams"] = new JsonObject { ["width"] = "Full" },
                ["body"] = body,
            };
            if (board != null)
            {
                // Graph-posted cards support OpenUrl actions, not bot invoke/submit callbacks.
                card["actions"] = new JsonArray(new JsonObject
                {
                    ["type"] = "Action.OpenUrl",
                    ["title"] = "Open Planner",
                    ["url"] = "https://planner.cloud.microsoft/",
                });
            }

            if (Encoding.UTF8.GetByteCount(card.ToJsonString()) <= CardByteBudget)
            {
                return new RenderedTeamsDigest(card, summary, owners);
            }
        }

        throw new ArgumentException("The digest exceeds the card size budget. Shorten its summary sections.");
    }

    public static JsonObject BuildGraphMessage(RenderedTeamsDigest digest, bool mentionOwners)
    {
        const string attachmentId = "digest-card";
        var text = new StringBuilder();
        var mentions = new JsonArray();
        if (mentionOwners && digest.Owners.Count > 0)
        {
            text.Append("<p>");
            foreach (var owner in digest.Owners.Take(MentionLimit))
            {
                if (!Guid.TryParse(owner.Id, out _))
                {
                    throw new ArgumentException("An owner is missing a valid directory ID; mentions were not sent.");
                }
                var index = mentions.Count;
                if (index > 0)
                {
                    text.Append(", ");
                }
                text.Append($"<at id=\"{index}\">{WebUtility.HtmlEncode(owner.Name)}</at>");
                mentions.Add(new JsonObject
                {
                    ["id"] = index,
                    ["mentionText"] = owner.Name,
                    ["mentioned"] = new JsonObject
                    {
                        ["user"] = new JsonObject
                        {
                            ["id"] = owner.Id,
                            ["displayName"] = owner.Name,
                            ["userIdentityType"] = "aadUser",
                        },
                    },
                });
            }
            text.Append("</p>");
            if (digest.Owners.Count > MentionLimit)
            {
                text.Append($"<p>Mentions limited to {MentionLimit} of {digest.Owners.Count} owners.</p>");
            }
        }
        text.Append($"<attachment id=\"{attachmentId}\"></attachment>");

        var message = new JsonObject
        {
            ["body"] = new JsonObject { ["contentType"] = "html", ["content"] = text.ToString() },
            ["attachments"] = new JsonArray(new JsonObject
            {
                ["id"] = attachmentId,
                ["contentType"] = ContentType,
                ["contentUrl"] = null,
                ["content"] = digest.Card.ToJsonString(),
                ["name"] = "Digest",
            }),
            ["mentions"] = mentions,
        };
        if (Encoding.UTF8.GetByteCount(message.ToJsonString()) > MessageByteBudget)
        {
            throw new ArgumentException("The digest and mentions exceed the message size budget. Shorten the digest.");
        }
        return message;
    }

    private static bool IsPastDue(DigestTask task, DateTimeOffset localNow, TimeZoneInfo zone) =>
        task.DueDate.HasValue
        && TimeZoneInfo.ConvertTime(task.DueDate.Value, zone).Date < localNow.Date;

    private static JsonObject Metric(string label, int value, string color = "Default") => new()
    {
        ["type"] = "Column",
        ["width"] = "stretch",
        ["style"] = "emphasis",
        ["items"] = new JsonArray(
            Text(value.ToString(CultureInfo.InvariantCulture), size: "ExtraLarge", weight: "Bolder", color: color),
            Text(label, size: "Small", subtle: true)),
    };

    private static JsonObject Text(
        string text, string size = "Default", string weight = "Default",
        bool subtle = false, string color = "Default") => new()
    {
        ["type"] = "TextBlock",
        ["text"] = EscapeText(text),
        ["wrap"] = true,
        ["spacing"] = "Small",
        ["size"] = size,
        ["weight"] = weight,
        ["isSubtle"] = subtle,
        ["color"] = color,
    };

    private static string EscapeText(string text) =>
        Regex.Replace(WebUtility.HtmlEncode(text), @"([\\`*_\[\]])", @"\$1");
}
