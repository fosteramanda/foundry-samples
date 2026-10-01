using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WorkstreamManager.Services;

internal static class StandingBriefRenderer
{
    internal static string Render(
        StandingJob job, int version, string summary, string question, IReadOnlyList<string> options,
        IReadOnlyList<string> agenda, IReadOnlyList<StandingJobInput> inputs,
        IReadOnlyList<StandingJobDecision> decisions, JsonArray tasks,
        IReadOnlyList<StandingJobReceipt> specialists, IReadOnlyList<StandingJobEvent> events,
        IReadOnlyList<StandingBriefChange> changes)
    {
        var html = new StringBuilder();
        static string H(string text) => WebUtility.HtmlEncode(text);
        void Heading(string text) => html.Append($"<h2 style=\"font-weight:normal;font-size:16pt;color:#14675d\">{H(text)}</h2>");
        void Paragraph(string text) => html.Append($"<p>{H(text)}</p>");
        string Owner(string id) => job.Members.FirstOrDefault(member => member.Id == id) is { } member
            ? member.DisplayName ?? member.Email : "Owner not confirmed";
        var evidence = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Cite(string eventId, string quote)
        {
            if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(quote)) return;
            if (!evidence.TryGetValue(eventId, out var quotes)) evidence[eventId] = quotes = [];
            quotes.Add(quote);
        }
        html.Append("<div style=\"font-family:Aptos,Calibri,sans-serif;font-size:11pt;color:#172e33\">");
        html.Append($"<h1 style=\"font-weight:normal;font-size:24pt;color:#14675d\">{H(job.Title)}</h1>");
        Paragraph($"Decision brief | Revision {version}");
        if (events.Any(source => source.Content.Contains("synthetic", StringComparison.OrdinalIgnoreCase)))
            Paragraph("Demonstration scenario. Business inputs are synthetic.");
        Paragraph(summary);
        Heading("The decision");
        Paragraph(string.IsNullOrWhiteSpace(question) ? "No new manager decision is requested." : question);
        if (options.Count > 0)
        {
            html.Append("<ul>");
            foreach (var option in options) html.Append($"<li>{H(option)}</li>");
            html.Append("</ul>");
        }
        if (agenda.Count > 0)
        {
            Heading("Review agenda");
            html.Append("<ol>");
            foreach (var item in agenda) html.Append($"<li>{H(item)}</li>");
            html.Append("</ol>");
        }
        if (changes.Count > 0)
        {
            Heading("Source clarifications");
            foreach (var change in changes)
            {
                Paragraph(change.Quote);
                Cite(change.EvidenceEventId, change.Quote);
            }
        }
        if (decisions.Count > 0)
        {
            Heading("Recorded decisions");
            var superseded = decisions.Where(item => item.Supersedes != null).Select(item => item.Supersedes).ToHashSet();
            foreach (var decision in decisions.OrderBy(item => item.RecordedUtc))
            {
                Paragraph((superseded.Contains(decision.Id) ? "Earlier decision, superseded: " : "") + decision.Statement);
                if (!string.IsNullOrWhiteSpace(decision.Rationale)) Paragraph(decision.Rationale);
                Cite(decision.EvidenceEventId, decision.Statement);
                Cite(decision.EvidenceEventId, decision.Rationale);
            }
        }
        if (tasks.Count > 0)
        {
            Heading("Owned work");
            foreach (var task in tasks.OrderBy(item => item?["eta"]?.GetValue<string>()))
            {
                var status = task?["status"]?.GetValue<string>() ?? "open";
                var name = task?["name"]?.GetValue<string>() ?? "Untitled commitment";
                html.Append($"<p><strong>{H(name)}</strong><br/>{H(Owner(task?["ownerAadObjectId"]?.GetValue<string>() ?? ""))}");
                var due = task?["eta"]?.GetValue<string>();
                var date = DateTimeOffset.TryParse(due, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                    ? parsed.UtcDateTime.ToString("d MMM yyyy, HH:mm 'UTC'", CultureInfo.InvariantCulture) : "Due time not confirmed";
                html.Append($" | {H(date)} | {H(status.Replace('_', ' '))}</p>");
                if (task?["dependencyIds"] is JsonArray dependencies && dependencies.Count > 0)
                    Paragraph("Depends on: " + string.Join("; ", dependencies.Select(id =>
                        tasks.FirstOrDefault(item => item?["id"]?.GetValue<string>() == id?.GetValue<string>())?["name"]?.GetValue<string>()
                            ?? "Unresolved dependency")));
                var completion = task?["completionEvidence"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(completion))
                {
                    var split = completion.IndexOf(": ", StringComparison.Ordinal);
                    var quote = split >= 0 ? completion[(split + 2)..] : completion;
                    Paragraph("Completion confirmed: " + quote);
                    if (split >= 0) Cite(completion[..split], quote);
                }
            }
        }
        if (inputs.Count > 0)
        {
            Heading("Inputs and open assumptions");
            foreach (var input in inputs.OrderBy(item => item.State == "received").ThenBy(item => item.Title))
            {
                html.Append($"<p><strong>{H(input.Title)}</strong> | {H(input.State)} | {H(Owner(input.OwnerId))}</p>");
                Paragraph(input.State == "missing" ? "Awaiting the owner's contribution." : input.Content);
                Cite(input.EvidenceEventId, input.Content);
            }
        }
        if (specialists.Count > 0)
        {
            Heading("Specialist evidence");
            foreach (var receipt in specialists)
            {
                var result = JsonNode.Parse(receipt.Detail);
                Paragraph(result?["agent_name"]?.GetValue<string>() ?? "Authorized specialist");
                var answer = result?["answer"]?.GetValue<string>() ?? string.Empty;
                html.Append(RenderMarkdownEvidence(answer));
            }
        }
        if (evidence.Count > 0)
        {
            Heading("Source record");
            foreach (var (eventId, quotes) in evidence)
            {
                var source = events.SingleOrDefault(item => item.Id == eventId);
                if (source == null) throw new InvalidOperationException("A brief cannot cite an unrecorded source.");
                Paragraph($"{Owner(source.ActorId)} | {source.Kind} | {source.ReceivedUtc.UtcDateTime:d MMM yyyy, HH:mm} UTC");
                if (source.SourceUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https")
                    html.Append($"<p><a href=\"{H(url)}\">Open the source</a></p>");
                foreach (var quote in quotes) html.Append($"<blockquote>{H(quote)}</blockquote>");
            }
        }
        html.Append("</div>");
        return html.ToString();
    }

    private static string RenderMarkdownEvidence(string text)
    {
        var html = new StringBuilder();
        foreach (var paragraph in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = WebUtility.HtmlEncode(paragraph.Trim().TrimStart('#', ' '));
            line = Regex.Replace(line, @"\*\*([^*]+)\*\*", "<strong>$1</strong>");
            line = Regex.Replace(line, @"\[([^\]]+)\]\((https://[^)\s]+)\)", match =>
                Uri.TryCreate(WebUtility.HtmlDecode(match.Groups[2].Value), UriKind.Absolute, out var uri) && uri.Scheme == "https"
                    ? $"<a href=\"{WebUtility.HtmlEncode(uri.AbsoluteUri)}\">{match.Groups[1].Value}</a>" : match.Value);
            html.Append("<p>").Append(line).Append("</p>");
        }
        return html.ToString();
    }
}
