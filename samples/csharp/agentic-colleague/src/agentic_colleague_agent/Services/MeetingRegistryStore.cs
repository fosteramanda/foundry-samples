using Azure;
using Azure.Data.Tables;
using Azure.Identity;

namespace AgenticColleague.Services;

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
    public string ThreadId { get; set; } = string.Empty;
    public string JoinWebUrl { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public int PolicyVersion { get; set; }
    public bool Excluded { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public string TrackedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }

    // Keep legacy decisions readable. Do not invent consent records when removing the old UI gate.
    public bool CaptureApproved { get; set; }
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTimeOffset? ApprovedUtc { get; set; }
    public DateTimeOffset? NoticeSentUtc { get; set; }
    public bool RetentionApproved { get; set; }
    public string IngestionState { get; set; } = "none";
    public string TranscriptId { get; set; } = string.Empty;
    public DateTimeOffset? IngestedUtc { get; set; }
}

public interface IMeetingRegistryStore
{
    bool IsAvailable { get; }
    Task<TrackedMeetingEntity?> GetAsync(string mailbox, string key);
    Task<bool> UpsertAsync(TrackedMeetingEntity entity);
    Task<List<TrackedMeetingEntity>> ListAsync(string mailbox);
}

public class MeetingRegistryStore : IMeetingRegistryStore
{
    private readonly TableClient? _tableClient;
    private readonly ILogger<MeetingRegistryStore> _logger;

    public MeetingRegistryStore(IConfiguration configuration, ILogger<MeetingRegistryStore> logger)
    {
        _logger = logger;
        var tableServiceUri = new[]
        {
            configuration["MeetingRegistryTableServiceUri"],
            configuration["WorkItemsTableServiceUri"],
            configuration["DirectMessageAllowListTableServiceUri"],
            Environment.GetEnvironmentVariable("DirectMessageAllowListTableServiceUri")
        }.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(tableServiceUri))
        {
            logger.LogWarning("Meeting registry is unavailable: no table URI configured. Meeting tools are disabled.");
            return;
        }

        var tableName = configuration["MeetingRegistryTableName"];
        if (string.IsNullOrWhiteSpace(tableName)) { tableName = "meetingregistry"; }
        var instanceClientId = Environment.GetEnvironmentVariable("FOUNDRY_AGENT_DEFAULT_INSTANCE_CLIENT_ID");
        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = string.IsNullOrWhiteSpace(instanceClientId) ? null : instanceClientId
        });
        _tableClient = new TableServiceClient(new Uri(tableServiceUri), credential).GetTableClient(tableName);
        // Do not turn a storage/authentication failure into an empty registry and lose exclusions.
        _tableClient.CreateIfNotExists();
        logger.LogInformation("MeetingRegistryStore initialized with table {TableName}", tableName);
    }

    public bool IsAvailable => _tableClient != null;

    public static string ToPartitionKey(string mailbox)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mailbox);
        return mailbox.Trim().ToLowerInvariant();
    }

    public static string ToRowKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var cleaned = new string(key.Select(c => c is '/' or '\\' or '#' or '?' || char.IsControl(c) ? '_' : c).ToArray());
        return cleaned.Length > 900 ? cleaned[..900] : cleaned;
    }

    public async Task<TrackedMeetingEntity?> GetAsync(string mailbox, string key)
    {
        var response = await RequireTable().GetEntityIfExistsAsync<TrackedMeetingEntity>(
            ToPartitionKey(mailbox), ToRowKey(key));
        return response.HasValue ? response.Value : null;
    }

    public async Task<bool> UpsertAsync(TrackedMeetingEntity entity)
    {
        if (entity.CreatedUtc == default) { entity.CreatedUtc = DateTimeOffset.UtcNow; }
        if (entity.ETag == default)
        {
            await RequireTable().AddEntityAsync(entity);
        }
        else
        {
            await RequireTable().UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Merge);
        }
        _logger.LogInformation("Meeting preference saved. EventId={EventId} Excluded={Excluded}",
            entity.EventId, entity.Excluded);
        return true;
    }

    public async Task<List<TrackedMeetingEntity>> ListAsync(string mailbox)
    {
        var partition = ToPartitionKey(mailbox);
        var result = new List<TrackedMeetingEntity>();
        await foreach (var entity in RequireTable().QueryAsync<TrackedMeetingEntity>(e => e.PartitionKey == partition))
        {
            result.Add(entity);
        }
        return result;
    }

    private TableClient RequireTable() => _tableClient
        ?? throw new InvalidOperationException("Meeting registry is unavailable; exclusions cannot be checked.");
}
