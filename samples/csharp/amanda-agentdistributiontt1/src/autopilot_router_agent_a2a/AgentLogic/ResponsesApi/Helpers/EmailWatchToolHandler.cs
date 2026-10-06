namespace WorkstreamManager.AgentLogic.ResponsesApi.Helpers;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Agents.Core.Models;
using WorkstreamManager.Models;
using WorkstreamManager.Services;

/// <summary>
/// Lets the manager say "tell me when Sustineo emails back" in a 1:1 chat. A watch is saved
/// durably; when the agent's own mailbox receives email from that sender, the email-notification
/// path calls <see cref="NotifyAsync"/>, which posts a short notice into the chat that set the watch.
///
/// The watch only notifies. It never replies to the sender or acts on the email's content, so it
/// works for senders the agent would otherwise ignore (anyone who is not the manager or an approved
/// teammate). It can only see mail that reaches the agent's own mailbox, never the manager's inbox.
/// </summary>
internal sealed class EmailWatchToolHandler
{
    private const int MaxNoteLength = 300;
    private const int MaxPreviewLength = 240;

    private readonly IEmailWatchStore? _store;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly string? _graphAccessToken;
    private readonly string _partition;
    private readonly bool _configured;
    private readonly Func<string, Task<StandingJobMember?>> _resolvePerson;
    private readonly Func<string, string, Task<string>> _postToChat;

    private IActivity? _activity;
    private Func<Task<StandingJobCaller?>>? _resolveCaller;
    private string? _ownAddress;

    internal EmailWatchToolHandler(
        IEmailWatchStore? store,
        AgentMetadata agent,
        IConfiguration configuration,
        ILogger logger,
        HttpClient httpClient,
        string? graphAccessToken,
        Func<string, Task<StandingJobMember?>> resolvePerson,
        Func<string, string, Task<string>> postToChat)
    {
        _store = store;
        _logger = logger;
        _httpClient = httpClient;
        _graphAccessToken = graphAccessToken;
        _resolvePerson = resolvePerson;
        _postToChat = postToChat;
        _partition = $"{agent.TenantId}:{agent.UserId}";
        _configured = configuration.GetValue("EnableEmailWatches", true)
            && agent.TenantId != Guid.Empty && agent.UserId != Guid.Empty;
    }

    /// <summary>
    /// True only when a watch can be saved, the email can be read and the notice can be posted.
    /// Otherwise the tools are not offered, so the agent never promises a notice it cannot send.
    /// </summary>
    internal bool IsEnabled => _configured && _store?.IsAvailable == true && !string.IsNullOrWhiteSpace(_graphAccessToken);

    /// <summary>Captures the chat turn. The caller is resolved only if a watch tool is used.</summary>
    internal void SetTurn(IActivity activity, Func<Task<StandingJobCaller?>> resolveCaller)
    {
        _activity = activity;
        _resolveCaller = resolveCaller;
    }

    internal List<JsonNode> GetToolDefinitions()
    {
        if (!IsEnabled) return [];
        return
        [
            JsonNode.Parse("""
            {
                "type": "function",
                "name": "watch_email_from",
                "description": "Saves a watch so you post a notice in this chat when a specific person's email reaches YOUR mailbox. Use this when the manager says 'tell me when X emails', 'let me know when X replies', 'notify me when X gets back to me'. Do not create a routine to poll for email. Only the manager can set a watch, and only in a 1:1 chat. You only see email sent or copied to you, never the manager's inbox.",
                "parameters": {
                    "type": "object",
                    "properties": {
                        "sender": { "type": "string", "description": "The person to watch for: an email address, or their name as the manager said it (looked up in the directory)." },
                        "note": { "type": "string", "description": "Optional: what the manager is waiting for, in a few words, e.g. 'reply about the 9pm meeting'. Shown in the notice." },
                        "repeat": { "type": "boolean", "description": "false (default): notify on the next email, then stop. true: notify on every email until the watch is stopped. Only true when the manager says every time, whenever, or similar." }
                    },
                    "required": ["sender"],
                    "additionalProperties": false
                }
            }
            """)!,
            JsonNode.Parse("""
            {
                "type": "function",
                "name": "list_email_watches",
                "description": "Lists the email watches set up in this chat. Use it whenever asked what email you are watching for; never answer from memory.",
                "parameters": { "type": "object", "properties": {}, "additionalProperties": false }
            }
            """)!,
            JsonNode.Parse("""
            {
                "type": "function",
                "name": "stop_email_watch",
                "description": "Stops watching for a sender's email in this chat. Use it when the manager says to stop, cancel or forget a watch.",
                "parameters": {
                    "type": "object",
                    "properties": {
                        "sender": { "type": "string", "description": "The watched person's email address or name." }
                    },
                    "required": ["sender"],
                    "additionalProperties": false
                }
            }
            """)!,
        ];
    }

    internal async Task<string?> TryExecuteAsync(string toolName, string arguments)
    {
        if (!IsEnabled || toolName is not ("watch_email_from" or "list_email_watches" or "stop_email_watch"))
            return null;
        JsonNode? args;
        try
        {
            args = JsonNode.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Email watch tool {Tool} called with unparseable arguments.", toolName);
            return $"Could not parse arguments for {toolName}. Nothing was changed.";
        }
        try
        {
            return toolName switch
            {
                "watch_email_from" => await WatchAsync(Text(args, "sender"), Text(args, "note"), Flag(args, "repeat")),
                "list_email_watches" => await ListAsync(),
                _ => await StopAsync(Text(args, "sender")),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Email watch tool {Tool} failed.", toolName);
            return $"The {toolName} request failed ({ex.GetType().Name}). Nothing was confirmed; tell the manager it did not work.";
        }
    }

    private string? PersonalConversationId =>
        _activity is { } activity
        && string.Equals(activity.ChannelId?.ToString(), "msteams", StringComparison.OrdinalIgnoreCase)
        && string.Equals(activity.Conversation?.ConversationType, "personal", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(activity.Conversation?.Id)
            ? activity.Conversation!.Id
            : null;

    private async Task<string?> RefuseUnlessManagerAsync()
    {
        if (PersonalConversationId == null)
            return "Email watches can only be managed in a 1:1 chat with you, because the notice quotes part of the email. Nothing was changed.";
        var caller = _resolveCaller == null ? null : await _resolveCaller();
        if (caller == null || !string.Equals(caller.Id, caller.ManagerId, StringComparison.OrdinalIgnoreCase))
            return "Only your manager can set or stop email watches. Nothing was changed.";
        return null;
    }

    private async Task<string> WatchAsync(string sender, string note, bool repeat)
    {
        if (await RefuseUnlessManagerAsync() is { } refusal) return refusal;
        sender = sender.Trim();
        if (sender.Length == 0)
            return "Ask whose email to watch for: a name or an email address. No watch was set up.";

        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string primary;
        string? name = null;
        if (sender.Contains('@'))
        {
            if (!MailAddress.TryCreate(sender, out var parsed))
                return $"'{sender}' is not a valid email address. No watch was set up.";
            primary = parsed.Address.ToLowerInvariant();
            addresses.Add(primary);
            if (await TryResolvePersonAsync(primary) is { } person)
            {
                addresses.Add(person.Email);
                name = person.DisplayName;
            }
        }
        else
        {
            var person = await TryResolvePersonAsync(sender);
            if (person == null)
                return $"Could not find exactly one person named '{sender}' in the directory. Ask for their email address. No watch was set up.";
            primary = person.Email.ToLowerInvariant();
            addresses.Add(primary);
            name = person.DisplayName;
        }

        var conversationId = PersonalConversationId!;
        var caller = await _resolveCaller!();
        var watch = new EmailWatchEntity
        {
            PartitionKey = _partition,
            RowKey = EmailWatchStore.RowKeyFor(conversationId, primary),
            ConversationId = conversationId,
            RequesterId = caller!.Id,
            SenderAddress = primary,
            SenderAddresses = string.Join(';', addresses.Select(address => address.ToLowerInvariant())),
            SenderName = name ?? "",
            Note = note.Length > MaxNoteLength ? note[..MaxNoteLength] : note,
            Repeat = repeat,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        await _store!.SaveAsync(watch);
        _logger.LogInformation("Email watch saved: sender={Sender} repeat={Repeat} conversation={ConversationId}",
            primary, repeat, conversationId);

        var own = await TryGetOwnAddressAsync();
        var label = string.IsNullOrWhiteSpace(name) ? primary : $"{name} ({primary})";
        var when = repeat
            ? "each time one arrives, until the manager stops the watch"
            : "when the next one arrives, and then stop watching";
        return $"Watch saved for email from {label}. A notice will be posted in this chat {when}. "
             + $"Only email that reaches your own mailbox{(own == null ? "" : $" ({own})")} counts: email sent to you or copied to you. "
             + "You cannot see the manager's inbox, so say that in your confirmation.";
    }

    private async Task<string> ListAsync()
    {
        var conversationId = PersonalConversationId;
        if (conversationId == null)
            return "Email watches exist only in 1:1 chats. There are none in this conversation.";
        var watches = (await _store!.ListAsync(_partition))
            .Where(watch => watch.ConversationId == conversationId)
            .OrderBy(watch => watch.CreatedUtc)
            .ToList();
        if (watches.Count == 0) return "No email watches are set up in this chat.";
        var lines = watches.Select(watch =>
        {
            var label = string.IsNullOrWhiteSpace(watch.SenderName) ? watch.SenderAddress : $"{watch.SenderName} ({watch.SenderAddress})";
            var mode = watch.Repeat ? $"every email, {watch.NotifiedCount} notice(s) so far" : "next email only";
            var note = string.IsNullOrWhiteSpace(watch.Note) ? "" : $"; waiting for: {watch.Note}";
            return $"- {label}: {mode}{note}";
        });
        return "Email watches in this chat:\n" + string.Join('\n', lines);
    }

    private async Task<string> StopAsync(string sender)
    {
        if (await RefuseUnlessManagerAsync() is { } refusal) return refusal;
        sender = sender.Trim();
        var conversationId = PersonalConversationId!;
        var matches = (await _store!.ListAsync(_partition))
            .Where(watch => watch.ConversationId == conversationId && MatchesSender(watch, sender))
            .ToList();
        if (matches.Count == 0) return $"No email watch in this chat matches '{sender}'. Nothing was changed.";
        var stopped = 0;
        foreach (var watch in matches)
            if (await _store.RemoveAsync(watch)) stopped++;
        _logger.LogInformation("Email watch stopped: sender={Sender} count={Count}", sender, stopped);
        return stopped == matches.Count
            ? $"Stopped {stopped} email watch(es) for '{sender}'."
            : $"Stopped {stopped} of {matches.Count} matching watch(es); the rest changed while stopping. Check with list_email_watches.";
    }

    private static bool MatchesSender(EmailWatchEntity watch, string sender) =>
        sender.Contains('@')
            ? watch.Matches(sender)
            : sender.Length > 0
              && (string.Equals(watch.SenderName, sender, StringComparison.OrdinalIgnoreCase)
                  || watch.SenderName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                      .Contains(sender, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Called for every email that reaches the agent's mailbox, before any sender approval.
    /// Posts a notice for each watch on this sender and returns how many were posted.
    /// A one-time watch is claimed by deleting it before posting, so a duplicate notification for
    /// the same email cannot post twice; if posting fails the watch is put back.
    /// </summary>
    internal async Task<int> NotifyAsync(string? messageId, string? fallbackFrom, string? fallbackSubject, string? fallbackText)
    {
        if (!IsEnabled) return 0;
        var watches = await _store!.ListAsync(_partition);
        if (watches.Count == 0) return 0;

        var message = string.IsNullOrWhiteSpace(messageId) ? null : await TryReadMessageAsync(messageId);
        var address = message?.Address
            ?? (fallbackFrom is { } candidate && MailAddress.TryCreate(candidate, out var parsed) ? parsed.Address : null);
        if (address == null)
        {
            _logger.LogWarning("Email watch check skipped: the sender address could not be read.");
            return 0;
        }
        var matches = watches.Where(watch => watch.Matches(address)).ToList();
        if (matches.Count == 0) return 0;

        var subject = message?.Subject ?? fallbackSubject ?? "";
        var preview = Shorten(message?.Preview ?? fallbackText ?? "");
        var key = message?.Id ?? messageId ?? Hash($"{address}\n{subject}\n{preview}");
        var posted = 0;
        foreach (var watch in matches)
        {
            if (watch.LastMessageKey == key) continue;
            var original = watch.Copy();
            bool claimed;
            if (watch.Repeat)
            {
                watch.LastMessageKey = key;
                watch.NotifiedCount++;
                claimed = await _store.ReplaceAsync(watch);
            }
            else
            {
                claimed = await _store.RemoveAsync(watch);
            }
            if (!claimed)
            {
                _logger.LogInformation("Email watch for {Sender} was already handled or changed; no notice posted.", watch.SenderAddress);
                continue;
            }
            try
            {
                var html = BuildNotice(watch, message?.Name, address, subject, preview);
                var receipt = await _postToChat(watch.ConversationId, html);
                posted++;
                _logger.LogInformation("Email watch notice posted: sender={Sender} conversation={ConversationId} message={MessageId} repeat={Repeat}",
                    address, watch.ConversationId, receipt, watch.Repeat);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Email watch notice for {Sender} could not be posted; restoring the watch.", address);
                await TryRestoreAsync(original, watch);
            }
        }
        return posted;
    }

    private async Task TryRestoreAsync(EmailWatchEntity original, EmailWatchEntity claimed)
    {
        try
        {
            if (original.Repeat)
            {
                claimed.LastMessageKey = original.LastMessageKey;
                claimed.NotifiedCount = original.NotifiedCount;
                if (!await _store!.ReplaceAsync(claimed))
                    _logger.LogWarning("Email watch for {Sender} changed before it could be restored.", original.SenderAddress);
            }
            else
            {
                original.ETag = default;
                await _store!.SaveAsync(original);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Email watch for {Sender} could not be restored after a failed notice.", original.SenderAddress);
        }
    }

    internal static string BuildNotice(EmailWatchEntity watch, string? fromName, string address, string subject, string preview)
    {
        string E(string value) => WebUtility.HtmlEncode(value);
        var name = !string.IsNullOrWhiteSpace(fromName) ? fromName
            : !string.IsNullOrWhiteSpace(watch.SenderName) ? watch.SenderName : address;
        var html = new StringBuilder();
        html.Append($"<p>Email from <strong>{E(name)}</strong> ({E(address)}) just reached my mailbox.</p>");
        html.Append($"<p>Subject: {E(string.IsNullOrWhiteSpace(subject) ? "(no subject)" : subject)}</p>");
        if (!string.IsNullOrWhiteSpace(preview)) html.Append($"<p><em>{E(preview)}</em></p>");
        if (!string.IsNullOrWhiteSpace(watch.Note)) html.Append($"<p>You asked me to watch for: {E(watch.Note)}</p>");
        html.Append(watch.Repeat
            ? "<p>I will keep watching for their email.</p>"
            : "<p>That was the email you were waiting for, so I have stopped watching.</p>");
        return html.ToString();
    }

    private static string Shorten(string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= MaxPreviewLength ? collapsed : collapsed[..MaxPreviewLength].TrimEnd() + "...";
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Text(JsonNode? args, string name) =>
        args?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static bool Flag(JsonNode? args, string name) =>
        args?[name] is JsonValue value
        && (value.TryGetValue<bool>(out var flag) ? flag
            : value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed) && parsed);

    private async Task<StandingJobMember?> TryResolvePersonAsync(string identifier)
    {
        try
        {
            return await _resolvePerson(identifier);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Email watch directory lookup failed.");
            return null;
        }
    }

    private sealed record WatchedMessage(string? Id, string? Address, string? Name, string? Subject, string? Preview);

    private async Task<WatchedMessage?> TryReadMessageAsync(string messageId)
    {
        var message = await GraphGetAsync(
            $"me/messages/{Uri.EscapeDataString(messageId)}?$select=id,from,subject,bodyPreview");
        if (message == null) return null;
        var from = message["from"]?["emailAddress"];
        return new WatchedMessage(
            message["id"]?.GetValue<string>(),
            from?["address"]?.GetValue<string>(),
            from?["name"]?.GetValue<string>(),
            message["subject"]?.GetValue<string>(),
            message["bodyPreview"]?.GetValue<string>());
    }

    private async Task<string?> TryGetOwnAddressAsync()
    {
        if (_ownAddress != null) return _ownAddress;
        var me = await GraphGetAsync("me?$select=mail,userPrincipalName");
        _ownAddress = me?["mail"]?.GetValue<string>() ?? me?["userPrincipalName"]?.GetValue<string>();
        return _ownAddress;
    }

    private async Task<JsonNode?> GraphGetAsync(string relativeUrl)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/" + relativeUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _graphAccessToken);
            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Email watch Graph read failed: HTTP {Status}.", (int)response.StatusCode);
                return null;
            }
            return JsonNode.Parse(await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "Email watch Graph read failed.");
            return null;
        }
    }
}
