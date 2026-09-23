namespace WorkstreamManager.AgentLogic.ResponsesApi.Helpers;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Agents.Core.Models;
using WorkstreamManager.Services;

public sealed class TeamsDigestToolHandler
{
    public const string ToolName = "send_teams_digest";
    private readonly PlannerToolHandler _planner;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly IConfiguration _configuration;
    private readonly string? _graphAccessToken;
    private IActivity? _activity;
    private string? _deliveryResult;

    public TeamsDigestToolHandler(
        PlannerToolHandler planner, HttpClient httpClient, ILogger logger,
        IConfiguration configuration, string? graphAccessToken)
    {
        _planner = planner;
        _httpClient = httpClient;
        _logger = logger;
        _configuration = configuration;
        _graphAccessToken = graphAccessToken;
    }

    public bool HasDelivered { get; private set; }

    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(_graphAccessToken)
        && string.Equals(_activity?.ChannelId?.ToString(), "msteams", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(_activity?.Conversation?.ConversationType, "channel", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(_activity?.Conversation?.Id);

    public void SetCurrentActivityContext(IActivity activity)
    {
        _activity = activity;
        _deliveryResult = null;
        HasDelivered = false;
    }

    public List<JsonNode> GetToolDefinitions() => !IsEnabled ? [] :
    [
        JsonNode.Parse("""
        {
            "type": "function",
            "name": "send_teams_digest",
            "description": "Posts one readable Adaptive Card digest into THIS Teams chat. Use for scheduled and on-demand Teams briefings, digests and board summaries instead of SendMessageToChat or a long text reply. It reads current Planner items itself when include_board is true, so do not repeat task lists in sections. It does not change any board items. After success do not send the same digest again. Email delivery remains separate.",
            "parameters": {
                "type": "object",
                "properties": {
                    "title": { "type": "string", "maxLength": 120, "description": "Short digest title." },
                    "sections": {
                        "type": "array",
                        "maxItems": 4,
                        "description": "Up to four concise, evidence-based sections such as Activity, Decisions, or Blockers. Plain text, no HTML, tables or owner mentions. Distinguish missing access from no activity. Empty is allowed for a board-only summary.",
                        "items": {
                            "type": "object",
                            "properties": {
                                "heading": { "type": "string", "maxLength": 60 },
                                "text": { "type": "string", "maxLength": 1200 }
                            },
                            "required": ["heading", "text"],
                            "additionalProperties": false
                        }
                    },
                    "include_board": { "type": "boolean", "description": "Read the live Planner board and include accurate counts, owners and due dates. Default true. Set false for a briefing unrelated to the board." },
                    "board": { "type": "string", "description": "Optional board title; omit for the configured default." },
                    "mention_owners": { "type": "boolean", "description": "True only when the request or standing routine asks to notify owners. Mention each distinct owner once, never once per task. Default false." }
                },
                "required": ["title", "sections"],
                "additionalProperties": false
            }
        }
        """)!,
    ];

    public async Task<string?> TryExecuteAsync(string toolName, string arguments)
    {
        if (toolName != ToolName)
        {
            return null;
        }
        if (!IsEnabled)
        {
            return "The digest card tool needs a Teams chat and the agent's Graph token. Nothing was sent.";
        }
        if (_deliveryResult != null)
        {
            return _deliveryResult;
        }

        JsonObject message;
        RenderedTeamsDigest digest;
        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            };
            var args = JsonSerializer.Deserialize<DigestArguments>(arguments, options)
                ?? throw new ArgumentException("Digest arguments are required.");
            ValidateText(args.Title, "title", 120);
            if (args.Sections == null || args.Sections.Count > 4)
            {
                throw new ArgumentException("sections must be an array with at most four entries.");
            }
            foreach (var section in args.Sections)
            {
                if (section == null)
                {
                    throw new ArgumentException("A digest section cannot be null.");
                }
                ValidateText(section.Heading, "section heading", 60);
                ValidateText(section.Text, "section text", 1200);
            }
            if (!args.IncludeBoard && args.Sections.Count == 0)
            {
                throw new ArgumentException("A digest needs a board or at least one summary section.");
            }
            if (args.Board?.Length > 200)
            {
                throw new ArgumentException("The board title must be at most 200 characters.");
            }

            PlannerDigestSnapshot? board = null;
            if (args.IncludeBoard)
            {
                var (snapshot, error) = await _planner.ReadDigestSnapshotAsync(args.Board);
                if (snapshot == null)
                {
                    _logger.LogWarning("Digest board read failed: {Error}", error);
                    return $"Could not prepare the digest: {error} Nothing was sent. Do not report an empty board.";
                }
                board = snapshot;
            }
            var zone = TimeZoneInfo.FindSystemTimeZoneById(
                _configuration["MeetingDisplayTimeZone"] ?? "Pacific Standard Time");
            digest = TeamsDigestCard.Build(args.Title, args.Sections, board, DateTimeOffset.UtcNow, zone);
            message = TeamsDigestCard.BuildGraphMessage(digest, args.MentionOwners);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            _logger.LogWarning(ex, "Invalid Teams digest arguments or display configuration.");
            return $"Could not prepare the digest: {ex.Message} Nothing was sent.";
        }

        // Once a POST is attempted, its outcome can be uncertain. Do not blindly post it twice.
        _deliveryResult = "Digest delivery could not be confirmed. Check this chat before retrying; do not repost with another tool.";
        var chatId = _activity!.Conversation!.Id;
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://graph.microsoft.com/v1.0/chats/{Uri.EscapeDataString(chatId)}/messages");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _graphAccessToken);
            request.Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Teams digest POST failed: HTTP {Status} {Body}",
                    (int)response.StatusCode, body[..Math.Min(body.Length, 500)]);
                _deliveryResult = $"Teams rejected the digest (HTTP {(int)response.StatusCode}). "
                    + "Report the delivery failure; do not claim it was posted or retry with another send tool.";
                return _deliveryResult;
            }
            var id = JsonNode.Parse(body)?["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id))
            {
                _logger.LogWarning("Teams accepted the digest but returned no message ID; not retrying.");
                return _deliveryResult;
            }

            HasDelivered = true;
            _logger.LogInformation("Teams digest card posted: chat={ChatId} message={MessageId}", chatId, id);
            _deliveryResult = JsonSerializer.Serialize(new
            {
                sent = true,
                message_id = id,
                summary = digest.Summary,
                instruction = "The card is already posted in this chat. Do not also post a text digest or resend it. Send email separately only if requested.",
            });
            return _deliveryResult;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            _logger.LogError(ex, "Teams digest delivery outcome is uncertain; not retrying.");
            return _deliveryResult;
        }
    }

    private static void ValidateText(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            throw new ArgumentException($"{field} must contain 1 to {maxLength} characters.");
        }
    }

    private sealed class DigestArguments
    {
        public string Title { get; init; } = string.Empty;
        public List<DigestSection>? Sections { get; init; }
        public bool IncludeBoard { get; init; } = true;
        public string? Board { get; init; }
        public bool MentionOwners { get; init; }
    }
}
