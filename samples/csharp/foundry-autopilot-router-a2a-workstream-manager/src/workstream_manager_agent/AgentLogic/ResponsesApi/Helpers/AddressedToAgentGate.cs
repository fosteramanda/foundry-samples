namespace WorkstreamManager.AgentLogic.ResponsesApi.Helpers;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

/// <summary>
/// Decides whether the agent should send a reply for the current activity, so the agent only
/// speaks when spoken to instead of answering every message in a group chat.
///
/// The criteria, in evaluation order:
///  1. Non-message activities (installation updates, events) are always agent-directed → respond.
///  2. Non-Teams channels have no multi-participant ambiguity → respond.
///  3. Teams 1:1 personal chats only have two participants → every message is for the agent.
///  4. Explicit @-mention of the agent → respond (and remember the mention so the reply can
///     @-mention the sender back). Detected via the structured Mention entity id, or via
///     &lt;at&gt; markup in the text matched against the agent's known names (Recipient.Name,
///     the per-chat display name resolved from Graph, and configured AgentDisplayNameAliases).
///  5. Cheap deterministic pre-filter: when the message @-mentions only other participants,
///     contains no second-person reference ("you", "your", ...), and never names the agent in
///     plain text, there is no plausible reading under which it is addressed to the agent —
///     stay silent without spending an LLM call.
///  6. Remaining ambiguous group-chat/channel messages go to a strict YES/NO LLM judge:
///     respond only when the message is clearly directed at the agent in context — e.g. a
///     second-person follow-up ("can you update that ETA?") right after the agent spoke.
///     Ordinary human-to-human discussion stays silent.
///  7. Fail open: if the judge call itself fails, respond rather than appear broken.
/// The judge can be disabled with EnableLlmAddressedToAgentJudge=false, which reduces the
/// gate to the deterministic checks (the agent then only answers DMs and explicit mentions).
///
/// This gate intentionally runs AFTER AccessControlService so unauthorized users keep getting
/// the canned access-control responses, and only authorized chatter falls through to "should
/// the agent respond?" filtering.
/// </summary>
internal class AddressedToAgentGate
{
    /// <summary>
    /// Name the agent answers to when nothing better is available: inbound agentic deliveries
    /// often carry a null Recipient.Name, so this must match the persona name in
    /// <see cref="WorkstreamManager.AgentLogic.AgentInstructions"/>.
    /// </summary>
    private const string DefaultAgentName = "Chief of Staff Autopilot";

    private readonly ILogger _logger;
    private readonly IConfiguration _configuration;
    private readonly ResponsesApiClient _responsesApiClient;
    private readonly TeamsActivityHelper _teamsHelper;
    private readonly HttpClient _httpClient;
    private readonly string? _graphAccessToken;

    // Per-process cache of "this bot's display name in this chat", keyed by conversationId.
    // The bot's chat-specific display name is set by whoever installed/added the bot and is
    // NOT delivered on inbound activities (Recipient.Name is null for agenticUser deliveries),
    // so we resolve it via Graph chat-members on first need and remember the answer.
    private static readonly ConcurrentDictionary<string, string?> BotDisplayNameCache = new();

    // Second-person references that suggest a message could be talking TO the agent even
    // while @-mentioning someone else ("@Priya loop in legal — and can you update the ETA?").
    // Used by the judge pre-filter; a match here just means "let the LLM judge decide", so
    // false positives are safe (they only cost the judge call we would have made anyway).
    private static readonly Regex SecondPersonRegex = new(
        @"\b(you|your|yours|yourself|u)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal AddressedToAgentGate(
        ILogger logger,
        IConfiguration configuration,
        ResponsesApiClient responsesApiClient,
        TeamsActivityHelper teamsHelper,
        HttpClient httpClient,
        string? graphAccessToken)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _responsesApiClient = responsesApiClient ?? throw new ArgumentNullException(nameof(responsesApiClient));
        _teamsHelper = teamsHelper ?? throw new ArgumentNullException(nameof(teamsHelper));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _graphAccessToken = graphAccessToken;
    }

    internal async Task<AddressedVerdict> ShouldRespondAsync(
        ITurnContext turnContext,
        string? userMessage,
        string conversationId)
    {
        var activity = turnContext.Activity;
        var channelId = activity.ChannelId;
        var activityType = activity.Type;
        var conversation = activity.Conversation;
        var conversationType = conversation?.ConversationType;
        var isGroup = conversation?.IsGroup;
        var recipient = activity.Recipient;
        var sender = activity.From;

        var mentions = _teamsHelper.ExtractMentions(activity);
        var mentionedIds = string.Join(",", mentions.Select(m => m.MentionedId ?? "(null)"));
        var mentionedNames = string.Join(",", mentions.Select(m => m.MentionedName ?? "(null)"));

        // Teams-style <at>NAME</at> markup parsed from the raw text. Most reliable signal -
        // some agentic Teams deliveries strip Mentioned/Text from the strongly-typed mention
        // entities, leaving the entity collection effectively empty even when the message
        // clearly contains @-mentions in its body.
        var atTagNames = TeamsActivityHelper.ExtractAtTagNames(userMessage);
        var atTagNamesJoined = string.Join(",", atTagNames);

        var (recipientAgenticUserId, recipientAgenticAppId, recipientBotId, recipientRole) =
            _teamsHelper.ExtractRecipientAgenticIdentifiers(recipient);

        _logger.LogInformation(
            "ShouldRespond: evaluating. activityId={ActivityId} channelId={ChannelId} activityType={ActivityType} " +
            "conversationId={ConversationId} conversationType={ConversationType} isGroup={IsGroup} " +
            "botRecipientId={RecipientId} botRecipientAadObjectId={RecipientAadObjectId} botRecipientName={RecipientName} " +
            "botRecipientRole={RecipientRole} botAgenticUserId={AgenticUserId} botAgenticAppId={AgenticAppId} botId={BotId} " +
            "senderId={SenderId} senderAadObjectId={SenderAadObjectId} senderName={SenderName} " +
            "mentionCount={MentionCount} mentionedIds=[{MentionedIds}] mentionedNames=[{MentionedNames}] " +
            "atTagCount={AtTagCount} atTagNames=[{AtTagNames}] textLength={TextLength}",
            activity.Id,
            channelId,
            activityType,
            conversationId,
            conversationType,
            isGroup,
            recipient?.Id,
            recipient?.AadObjectId,
            recipient?.Name,
            recipientRole,
            recipientAgenticUserId,
            recipientAgenticAppId,
            recipientBotId,
            sender?.Id,
            sender?.AadObjectId,
            sender?.Name,
            mentions.Count,
            mentionedIds,
            mentionedNames,
            atTagNames.Count,
            atTagNamesJoined,
            (userMessage ?? string.Empty).Length);

        // System / non-message activities (installation updates, etc.) are agent-directed.
        if (activityType != ActivityTypes.Message)
        {
            _logger.LogInformation(
                "ShouldRespond: YES (short-circuit: non-message activityType '{ActivityType}')",
                activityType);
            return AddressedVerdict.Respond();
        }

        // Non-Teams channels deliver only agent-directed traffic; the multi-participant
        // ambiguity this gate exists for is specific to Teams group chats and channels.
        if (!string.Equals(channelId, "msteams", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "ShouldRespond: YES (short-circuit: non-Teams channel '{ChannelId}')",
                channelId);
            return AddressedVerdict.Respond();
        }

        // Explicit @-mention of this agent (structured mention entity from Teams identifies
        // the recipient by id). When present this is a definitive signal - no LLM needed.
        if (recipient != null && !string.IsNullOrEmpty(recipient.Id))
        {
            var candidateIds = new[]
            {
                recipient.Id,
                recipient.AadObjectId,
                recipientAgenticUserId,
                recipientAgenticAppId,
                recipientBotId,
            }.Where(s => !string.IsNullOrEmpty(s)).ToArray();

            var matchedMention = mentions.FirstOrDefault(m =>
                !string.IsNullOrEmpty(m.MentionedId) &&
                candidateIds.Any(c => string.Equals(m.MentionedId, c, StringComparison.OrdinalIgnoreCase)));
            if (matchedMention != null)
            {
                _logger.LogInformation(
                    "ShouldRespond: YES (short-circuit: agent @-mentioned by id). botRecipientId={RecipientId} matchedMentionId={MatchedId} mentionText='{MentionText}'",
                    recipient.Id,
                    matchedMention.MentionedId,
                    matchedMention.Text);
                return AddressedVerdict.RespondWithExplicitMention();
            }

            if (mentions.Count > 0)
            {
                _logger.LogInformation(
                    "ShouldRespond: structured mentions present but none matched the agent by id. " +
                    "candidateBotIds=[{CandidateIds}] mentionedIds=[{MentionedIds}] mentionedNames=[{MentionedNames}]. " +
                    "Continuing with other heuristics.",
                    string.Join(",", candidateIds),
                    mentionedIds,
                    mentionedNames);
            }
        }

        var configuredAliases = GetConfiguredAgentAliases();
        var knownMentionNames = new List<string?>
        {
            recipient?.Name,
            DefaultAgentName,
        }
            .Concat(configuredAliases)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var matchedAtTagName = atTagNames.FirstOrDefault(tag =>
            knownMentionNames.Any(name => MentionNamesMatch(tag, name!)));
        if (matchedAtTagName is not null)
        {
            _logger.LogInformation(
                "ShouldRespond: YES (short-circuit: parsed @-mention matched known agent name). matchedTag={MatchedTag} knownMentionNames=[{KnownNames}] configuredAliases=[{Aliases}]",
                matchedAtTagName,
                string.Join(",", knownMentionNames),
                string.Join(",", configuredAliases));
            return AddressedVerdict.RespondWithExplicitMention();
        }

        // 1:1 personal chats only have two participants (user + agent), so every message
        // is necessarily directed at the agent.
        var isPersonalChat = string.Equals(conversationType, "personal", StringComparison.OrdinalIgnoreCase)
            || isGroup == false;
        if (channelId == "msteams" && isPersonalChat)
        {
            _logger.LogInformation(
                "ShouldRespond: YES (short-circuit: Teams personal chat). conversationType={ConversationType} isGroup={IsGroup}",
                conversationType,
                isGroup);
            return AddressedVerdict.Respond();
        }

        // NOTE: intentionally NOT short-circuiting NO on "Teams group chat + any <at> markup".
        // Empirically Teams delivers group-chat messages to an agenticUser bot even when the
        // user @-mentioned a different participant, so <at> tags alone aren't reliable as a
        // "addressed to me" signal. The LLM judge below is given the full list of @-tagged
        // names plus the agent's known names so it can decide name-by-name.

        var enableLlmAddressedToAgentJudge = _configuration.GetValue("EnableLlmAddressedToAgentJudge", true);
        _logger.LogInformation(
            "ShouldRespond: no short-circuit matched - evaluating ambiguous addressing. channelId={ChannelId} conversationType={ConversationType} isGroup={IsGroup} llmJudgeEnabled={LlmJudgeEnabled}",
            channelId,
            conversationType,
            isGroup,
            enableLlmAddressedToAgentJudge);

        return await IsAddressedToAgentAsync(
            turnContext,
            userMessage ?? string.Empty,
            conversationId,
            enableLlmAddressedToAgentJudge);
    }

    /// <summary>
    /// Runs deterministic addressed-to-agent checks for ambiguous group-chat messages and,
    /// when enabled, falls back to an LLM YES/NO classifier for pronoun-heavy follow-ups.
    /// </summary>
    private async Task<AddressedVerdict> IsAddressedToAgentAsync(
        ITurnContext turnContext,
        string userMessage,
        string conversationId,
        bool enableLlmAddressedToAgentJudge)
    {
        var agentName = turnContext.Activity.Recipient?.Name;
        if (string.IsNullOrWhiteSpace(agentName))
        {
            agentName = DefaultAgentName;
        }

        var senderName = turnContext.Activity.From?.Name ?? "the sender";
        var trimmedMessage = string.IsNullOrWhiteSpace(userMessage) ? "(no text)" : userMessage.Trim();

        var resolvedDisplayName = await TryResolveBotDisplayNameAsync(turnContext, conversationId);
        var aliases = GetConfiguredAgentAliases();
        var allAgentNames = new List<string> { agentName }
            .Concat(string.IsNullOrWhiteSpace(resolvedDisplayName)
                ? Array.Empty<string>()
                : new[] { resolvedDisplayName! })
            .Concat(aliases)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var atTagNames = TeamsActivityHelper.ExtractAtTagNames(userMessage);

        _logger.LogInformation(
            "Judge: evaluating addressed-to-agent context. agentName={AgentName} resolvedDisplayName={ResolvedDisplayName} " +
            "aliases=[{Aliases}] allAgentNames=[{AllNames}] atTagNames=[{AtTagNames}] senderName={SenderName} " +
            "conversationId={ConversationId} llmJudgeEnabled={LlmJudgeEnabled} messageLength={MessageLength}",
            agentName,
            resolvedDisplayName,
            string.Join(",", aliases),
            string.Join(",", allAgentNames),
            string.Join(",", atTagNames),
            senderName,
            conversationId,
            enableLlmAddressedToAgentJudge,
            trimmedMessage.Length);

        if (string.IsNullOrWhiteSpace(resolvedDisplayName) && aliases.Count == 0)
        {
            _logger.LogWarning(
                "Judge: bot display name could not be resolved from Graph and no AgentDisplayNameAliases " +
                "are configured. The judge will only see the canonical agent name (\"{AgentName}\"), which " +
                "may not match the name users actually @-mention the bot with in this chat. To fix, ensure " +
                "the bot has Graph permission to read chat members, or set AgentDisplayNameAliases " +
                "(comma-separated) as a fallback.",
                agentName);
        }

        var allAgentNamesJoined = string.Join(", ", allAgentNames.Select(n => $"\"{n}\""));
        var atTagsJoined = atTagNames.Count == 0
            ? "(none)"
            : string.Join(", ", atTagNames.Select(n => $"\"{n}\""));

        var matchedMentionName = atTagNames.FirstOrDefault(tag =>
            allAgentNames.Any(name => MentionNamesMatch(tag, name)));
        if (matchedMentionName is not null)
        {
            _logger.LogInformation(
                "Judge: YES (short-circuit: parsed @-mention matched known agent name). matchedTag={MatchedTag} allAgentNames=[{AllNames}] conversationId={ConversationId}",
                matchedMentionName,
                string.Join(",", allAgentNames),
                conversationId);
            return AddressedVerdict.RespondWithExplicitMention();
        }

        // Cheap deterministic pre-filter before spending an LLM call: when the message
        // @-mentions only other participants (any mention of the agent would have
        // short-circuited above), makes no second-person reference, and never names the
        // agent in plain text, there is no plausible reading under which it is addressed
        // to the agent. Skip without consulting the judge. Anything that trips one of the
        // checks just falls through to the judge, so this only ever saves calls — it never
        // answers YES on its own.
        var structuredMentionCount = _teamsHelper.ExtractMentions(turnContext.Activity).Count;
        if (atTagNames.Count > 0 || structuredMentionCount > 0)
        {
            // Strip <at> markup so mentioned display names don't count as message text.
            var textWithoutMentionTags = TeamsActivityHelper.AtTagRegex.Replace(userMessage, " ");
            var hasSecondPersonReference = SecondPersonRegex.IsMatch(textWithoutMentionTags);
            var namesAgentInText = allAgentNames.Any(name =>
                textWithoutMentionTags.Contains(name, StringComparison.OrdinalIgnoreCase));

            if (!hasSecondPersonReference && !namesAgentInText)
            {
                _logger.LogInformation(
                    "Judge: NO (pre-filter: message @-mentions only other participants, has no second-person reference, " +
                    "and does not name the agent — skipping LLM judge). atTagNames=[{AtTagNames}] structuredMentionCount={StructuredMentionCount} conversationId={ConversationId}",
                    string.Join(",", atTagNames),
                    structuredMentionCount,
                    conversationId);
                return AddressedVerdict.Skip();
            }
        }

        if (!enableLlmAddressedToAgentJudge)
        {
            _logger.LogInformation(
                "Judge: LLM addressed-to-agent classifier disabled via config; deterministic checks did not match. Returning NO. conversationId={ConversationId}",
                conversationId);
            return AddressedVerdict.Skip();
        }

        var primaryModelDeployment = _configuration["ModelDeployment"]?.Trim();
        var judgeModelDeployment = _configuration["AddressedToAgentJudgeModelDeployment"]?.Trim();
        var judgeModelOverride = string.IsNullOrWhiteSpace(judgeModelDeployment) ? null : judgeModelDeployment;
        var canReuseResponseChain = judgeModelOverride == null
            || string.Equals(judgeModelOverride, primaryModelDeployment, StringComparison.OrdinalIgnoreCase);

        if (!canReuseResponseChain)
        {
            _logger.LogInformation(
                "Judge: using model override '{JudgeModel}' and skipping previous_response_id from primary model '{PrimaryModel}' to avoid cross-model chain mismatches.",
                judgeModelOverride,
                primaryModelDeployment);
        }

        var judgeInstructions =
            "You are a strict binary classifier. Your only job is to decide whether the most " +
            "recent user message in the ongoing conversation is addressed to the agent. The " +
            $"agent is known by ANY of these names/aliases: {allAgentNamesJoined}. Respond with " +
            "exactly one token: YES or NO. No punctuation, no explanation, no other text.";

        var judgeInput =
            $"Agent names/aliases (any of these refers to the agent): {allAgentNamesJoined}\n" +
            $"Sender of the latest message (a human participant): {senderName}\n" +
            $"@-mention tag names parsed from the message: [{atTagsJoined}]\n" +
            "\n" +
            "Decide whether the LATEST USER MESSAGE below is addressed to the agent.\n" +
            "Apply these rules in order:\n" +
            "  1. If the message contains @-mention tags but none of those tags refer to the\n" +
            "     agent names/aliases listed above, answer NO.\n" +
            "  2. If there are no @-mention tags, answer YES only when the latest message is\n" +
            "     most likely directed at the agent given prior context (for example, a direct\n" +
            "     second-person follow-up to something the agent just said).\n" +
            "  3. Otherwise answer NO.\n" +
            "\n" +
            "LATEST USER MESSAGE:\n" +
            trimmedMessage + "\n" +
            "\n" +
            "Answer with exactly YES or NO.";

        try
        {
            var verdict = await _responsesApiClient.InvokeAsync(
                input: judgeInput,
                conversationId: conversationId,
                instructionsOverride: judgeInstructions,
                includeMcpTools: false,
                persistResponseId: false,
                modelDeploymentOverride: judgeModelOverride,
                usePreviousResponseId: canReuseResponseChain);

            var normalized = (verdict ?? string.Empty).Trim().TrimEnd('.', '!', '?', ',').ToUpperInvariant();
            var isYes = normalized.StartsWith("YES");
            var isNo = normalized.StartsWith("NO");

            _logger.LogInformation(
                "Judge: verdict received. rawVerdict='{Verdict}' normalized='{Normalized}' parsedYes={IsYes} parsedNo={IsNo} " +
                "decision={Decision} agentName={AgentName} allAgentNames=[{AllNames}] atTagNames=[{AtTagNames}] conversationId={ConversationId}",
                verdict,
                normalized,
                isYes,
                isNo,
                isYes ? "RESPOND" : "SKIP",
                agentName,
                string.Join(",", allAgentNames),
                string.Join(",", atTagNames),
                conversationId);

            // Judge-decided YES = user did NOT explicitly @-mention the agent (we'd have
            // short-circuited above). Reply via blockquote but skip the @-mention.
            return isYes ? AddressedVerdict.Respond() : AddressedVerdict.Skip();
        }
        catch (Exception ex)
        {
            // If the judge itself fails, fall back to responding so the agent doesn't appear
            // unresponsive due to a transient classifier failure.
            _logger.LogWarning(
                ex,
                "Judge: classifier call failed; defaulting to RESPOND=true. conversationId={ConversationId}",
                conversationId);
            return AddressedVerdict.Respond();
        }
    }

    /// <summary>
    /// Resolves the bot's per-chat display name (the name end-users actually @-mention it with)
    /// by querying Microsoft Graph chat members. The chat-specific display name is set by whoever
    /// added the bot to the chat - it can differ from instance to instance - and is NOT delivered
    /// on inbound activities for agenticUser bots. We match each member's userId against the bot's
    /// recipient identifiers and return the matching member's displayName. Cached per-conversationId
    /// for process lifetime. Returns null if we don't have a Graph token, the channel isn't Teams,
    /// or no member matches.
    /// </summary>
    private async Task<string?> TryResolveBotDisplayNameAsync(ITurnContext turnContext, string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(_graphAccessToken))
        {
            _logger.LogDebug("Skipping Graph display-name lookup: no Graph access token available.");
            return null;
        }

        var channelId = turnContext.Activity.ChannelId?.ToString();
        if (!string.Equals(channelId, "msteams", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (BotDisplayNameCache.TryGetValue(conversationId, out var cached))
        {
            _logger.LogInformation(
                "Bot display name resolution: cache hit. conversationId={ConversationId} displayName={DisplayName}",
                conversationId,
                cached);
            return cached;
        }

        var recipient = turnContext.Activity.Recipient;
        var candidateIds = _teamsHelper.GetBotCandidateIds(recipient);

        if (candidateIds.Count == 0)
        {
            _logger.LogWarning(
                "Bot display name resolution: no recipient identifiers to match against. conversationId={ConversationId}",
                conversationId);
            BotDisplayNameCache[conversationId] = null;
            return null;
        }

        var url = $"https://graph.microsoft.com/v1.0/chats/{Uri.EscapeDataString(conversationId)}/members";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _graphAccessToken);
            using var resp = await _httpClient.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Bot display name resolution: Graph chat-members lookup failed. conversationId={ConversationId} status={Status} body={Body}",
                    conversationId,
                    (int)resp.StatusCode,
                    body);
                BotDisplayNameCache[conversationId] = null;
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning(
                    "Bot display name resolution: Graph response missing 'value' array. conversationId={ConversationId} body={Body}",
                    conversationId,
                    body);
                BotDisplayNameCache[conversationId] = null;
                return null;
            }

            string? botDisplay = null;
            string? matchedOn = null;
            var memberCount = 0;
            foreach (var m in arr.EnumerateArray())
            {
                memberCount++;
                var memberDisplay = m.TryGetProperty("displayName", out var dnProp) && dnProp.ValueKind == JsonValueKind.String
                    ? dnProp.GetString()
                    : null;
                var memberUserId = m.TryGetProperty("userId", out var uidProp) && uidProp.ValueKind == JsonValueKind.String
                    ? uidProp.GetString()
                    : null;
                var memberId = m.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                    ? idProp.GetString()
                    : null;

                if (!string.IsNullOrEmpty(memberUserId) && candidateIds.Contains(memberUserId))
                {
                    botDisplay = memberDisplay;
                    matchedOn = $"userId={memberUserId}";
                    break;
                }
                if (!string.IsNullOrEmpty(memberId) && candidateIds.Contains(memberId))
                {
                    botDisplay = memberDisplay;
                    matchedOn = $"id={memberId}";
                    break;
                }
            }

            _logger.LogInformation(
                "Bot display name resolution: Graph chat-members lookup complete. conversationId={ConversationId} memberCount={MemberCount} candidateIds=[{Candidates}] resolved={Resolved} matchedOn={MatchedOn}",
                conversationId,
                memberCount,
                string.Join(",", candidateIds),
                botDisplay,
                matchedOn);

            BotDisplayNameCache[conversationId] = botDisplay;
            return botDisplay;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Bot display name resolution: exception calling Graph chat-members. conversationId={ConversationId}",
                conversationId);
            // Do NOT cache the failure permanently - a transient Graph error shouldn't doom
            // this chat for the lifetime of the process. Caller will retry on the next message.
            return null;
        }
    }

    /// <summary>
    /// Reads the configured Teams display-name aliases the agent should respond to. Operators
    /// set these via the "AgentDisplayNameAliases" config setting (comma- or semicolon-separated)
    /// because the bot's display name in a Teams group chat is set by whoever added the bot and
    /// is NOT delivered on the inbound activity (Recipient.Name is null for agenticUser deliveries).
    /// Without aliases the LLM judge can only match the configured agent name from instructions.
    /// </summary>
    private List<string> GetConfiguredAgentAliases()
    {
        var raw = _configuration["AgentDisplayNameAliases"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new List<string>();
        }
        return raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool MentionNamesMatch(string mentionName, string agentName)
    {
        var normalizedMention = NormalizeMentionName(mentionName);
        var normalizedAgentName = NormalizeMentionName(agentName);
        return normalizedMention.Length > 0 &&
               string.Equals(normalizedMention, normalizedAgentName, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeMentionName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        var chars = value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray();
        return new string(chars);
    }
}

/// <summary>
/// Verdict from <see cref="AddressedToAgentGate.ShouldRespondAsync"/>. Combines the should-reply
/// decision with whether the user explicitly @-mentioned the agent (vs. the agent inferring it
/// was being addressed via the LLM judge or by virtue of being the only other participant in a
/// 1:1 chat). Downstream code uses <see cref="WasExplicitlyMentioned"/> to decide whether to
/// @-mention the sender back in the response.
/// </summary>
internal readonly record struct AddressedVerdict(bool ShouldRespond, bool WasExplicitlyMentioned)
{
    /// <summary>
    /// Respond to the message but the user did not explicitly @-mention the agent (e.g. 1:1 DM,
    /// installation update, or the LLM judge inferred the message was addressed to the agent).
    /// The agent's reply should NOT prepend an @-mention of the sender.
    /// </summary>
    public static AddressedVerdict Respond() => new(ShouldRespond: true, WasExplicitlyMentioned: false);

    /// <summary>
    /// Respond to the message AND the user explicitly @-mentioned the agent (structured Mention
    /// entity by id, or a parsed &lt;at&gt; tag matching the agent's name/alias/Graph display
    /// name). The agent's reply should @-mention the sender back to match the conversational
    /// register.
    /// </summary>
    public static AddressedVerdict RespondWithExplicitMention() => new(ShouldRespond: true, WasExplicitlyMentioned: true);

    /// <summary>
    /// Do not respond. Used when the LLM judge decides the message is not addressed to the agent
    /// (e.g. side conversation between humans in a group chat).
    /// </summary>
    public static AddressedVerdict Skip() => new(ShouldRespond: false, WasExplicitlyMentioned: false);
}
