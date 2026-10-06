using Azure;
using Azure.Data.Tables;
using Azure.Identity;

namespace WorkstreamManager.Services;

/// <summary>
/// One meeting the autopilot has been asked to track, plus whether anyone has actually
/// permitted it to use that meeting's content.
///
/// PartitionKey = the organizer's address, lowercased. Meetings belong to whoever ran them,
/// and partitioning that way keeps one person's meetings out of another's listing.
/// RowKey       = the Teams thread id, sanitised. The thread id is on the calendar event
///                itself, so it can be derived without a Graph lookup that might fail.
///
/// The default state of every field here is "not permitted". That is deliberate: a registry
/// whose default is permissive turns a bug into a disclosure.
/// </summary>
public class TrackedMeetingEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string Subject { get; set; } = string.Empty;
    public string OrganizerAddress { get; set; } = string.Empty;
    public DateTimeOffset? StartUtc { get; set; }
    public DateTimeOffset? EndUtc { get; set; }

    /// <summary>Teams thread id, e.g. 19:meeting_xxx@thread.v2. The stable handle for the meeting.</summary>
    public string ThreadId { get; set; } = string.Empty;

    /// <summary>
    /// Join URL as it appeared on the calendar event. Kept because the documented way to resolve
    /// an onlineMeeting is a filter on JoinWebUrl; thread id is explicitly not filterable.
    /// </summary>
    public string JoinWebUrl { get; set; } = string.Empty;

    /// <summary>
    /// Whether anyone has permitted the agent to use this meeting's content. Defaults false and
    /// must be set by an explicit act. Nothing reads the meeting while this is false.
    /// </summary>
    public bool CaptureApproved { get; set; }

    public string ApprovedBy { get; set; } = string.Empty;
    public DateTimeOffset? ApprovedUtc { get; set; }

    /// <summary>
    /// When participants were told the meeting may be captured. Separate from approval on
    /// purpose: the organizer approving is not the same event as the room being told, and the
    /// second is the one that is owed to people who are not the organizer.
    /// </summary>
    public DateTimeOffset? NoticeSentUtc { get; set; }

    /// <summary>
    /// Records whether this meeting was also approved for retention beyond the immediate recap.
    ///
    /// This stores a decision, it does not implement one. Who owns memory retention is listed as
    /// an open question in CANON.md, so nothing here should be read as settling it. The flag is
    /// recorded so the answer is not lost, and so that whoever settles it later can see what was
    /// approved at the time.
    /// </summary>
    public bool RetentionApproved { get; set; }

    /// <summary>none, pending, ingested or excluded.</summary>
    public string IngestionState { get; set; } = "none";

    public string TranscriptId { get; set; } = string.Empty;
    public DateTimeOffset? IngestedUtc { get; set; }

    public string TrackedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>
    /// The single gate every future ingestion path must consult. Both conditions are required:
    /// approval alone is not enough, because a participant who was never told cannot have
    /// consented by someone else's approval.
    /// </summary>
    public bool IsIngestionEligible => CaptureApproved && NoticeSentUtc.HasValue;
}

/// <summary>
/// Durable record of which meetings the autopilot may use, and which it may not.
///
/// Azure Tables for the same reason PendingDelegationStore uses it: the storage account is
/// already provisioned and already carries a per-instance RBAC grant, so this adds no new
/// infrastructure. Consistency with the existing store also means one failure mode to reason
/// about rather than two.
///
/// When no table is configured the store reports itself unavailable and the tools are not
/// offered at all. That is the safe direction to fail. An agent that cannot record a capture
/// decision must not be able to act as though one was made.
/// </summary>
public class MeetingRegistryStore
{
    private readonly TableClient? _tableClient;
    private readonly ILogger<MeetingRegistryStore> _logger;

    public MeetingRegistryStore(IConfiguration configuration, ILogger<MeetingRegistryStore> logger)
    {
        _logger = logger;

        var tableServiceUri = FirstNonEmpty(
            configuration["MeetingRegistryTableServiceUri"],
            configuration["WorkItemsTableServiceUri"],
            configuration["DirectMessageAllowListTableServiceUri"],
            Environment.GetEnvironmentVariable("DirectMessageAllowListTableServiceUri"));
        var tableName = configuration["MeetingRegistryTableName"] ?? "meetingregistry";

        if (string.IsNullOrWhiteSpace(tableServiceUri))
        {
            _logger.LogWarning(
                "No table URI configured for the meeting registry; meeting tracking is DISABLED. " +
                "No meeting can be marked approved for capture, so none can be ingested.");
            return;
        }

        try
        {
            var instanceClientId = Environment.GetEnvironmentVariable("FOUNDRY_AGENT_DEFAULT_INSTANCE_CLIENT_ID");
            var credential = !string.IsNullOrEmpty(instanceClientId)
                ? new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = instanceClientId })
                : new DefaultAzureCredential();

            var serviceClient = new TableServiceClient(new Uri(tableServiceUri), credential);
            _tableClient = serviceClient.GetTableClient(tableName);
            _tableClient.CreateIfNotExists();
            _logger.LogInformation("MeetingRegistryStore initialized with table {TableName} at {Uri}", tableName, tableServiceUri);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize MeetingRegistryStore; meeting tracking is disabled.");
            _tableClient = null;
        }
    }

    public bool IsAvailable => _tableClient != null;

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

    /// <summary>
    /// Table keys cannot contain / \ # ? or control characters, and a Teams thread id contains
    /// both a colon and an @. Colons are legal but the rest are normalised so the key round-trips.
    /// </summary>
    public static string ToRowKey(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return Guid.NewGuid().ToString("n");
        }

        var cleaned = new string(threadId
            .Select(c => c is '/' or '\\' or '#' or '?' || char.IsControl(c) ? '_' : c)
            .ToArray());
        return cleaned.Length > 900 ? cleaned[..900] : cleaned;
    }

    public static string ToPartitionKey(string organizerAddress) =>
        string.IsNullOrWhiteSpace(organizerAddress)
            ? "unknown"
            : organizerAddress.Trim().ToLowerInvariant();

    public async Task<TrackedMeetingEntity?> GetAsync(string organizerAddress, string threadId)
    {
        if (_tableClient == null)
        {
            return null;
        }

        try
        {
            var response = await _tableClient.GetEntityIfExistsAsync<TrackedMeetingEntity>(
                ToPartitionKey(organizerAddress), ToRowKey(threadId));
            return response.HasValue ? response.Value : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read tracked meeting {ThreadId}", threadId);
            return null;
        }
    }

    public async Task<bool> UpsertAsync(TrackedMeetingEntity entity)
    {
        if (_tableClient == null)
        {
            return false;
        }

        try
        {
            if (entity.CreatedUtc == default)
            {
                entity.CreatedUtc = DateTimeOffset.UtcNow;
            }

            await _tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);
            _logger.LogInformation(
                "Tracked meeting saved: {Subject} thread={ThreadId} captureApproved={Approved} noticeSent={Notice}",
                entity.Subject,
                entity.ThreadId,
                entity.CaptureApproved,
                entity.NoticeSentUtc.HasValue);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save tracked meeting {ThreadId}", entity.ThreadId);
            return false;
        }
    }

    public async Task<List<TrackedMeetingEntity>> ListAsync(string organizerAddress)
    {
        var results = new List<TrackedMeetingEntity>();
        if (_tableClient == null)
        {
            return results;
        }

        var partition = ToPartitionKey(organizerAddress);

        try
        {
            await foreach (var e in _tableClient.QueryAsync<TrackedMeetingEntity>(x => x.PartitionKey == partition))
            {
                results.Add(e);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list tracked meetings for {Partition}", partition);
        }

        return results.OrderByDescending(r => r.StartUtc ?? r.CreatedUtc).ToList();
    }

    public async Task<bool> RemoveAsync(string organizerAddress, string threadId)
    {
        if (_tableClient == null)
        {
            return false;
        }

        try
        {
            await _tableClient.DeleteEntityAsync(ToPartitionKey(organizerAddress), ToRowKey(threadId));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove tracked meeting {ThreadId}", threadId);
            return false;
        }
    }
}
