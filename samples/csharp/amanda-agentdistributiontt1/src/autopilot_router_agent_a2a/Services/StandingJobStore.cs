using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;

namespace WorkstreamManager.Services;

internal sealed record StandingJobMember(string Id, string Email, string? DisplayName = null);
internal sealed record StandingJobCaller(string Id, string ManagerId, string? Email)
{
    public bool IsManager => Guid.TryParse(Id, out var actor) && actor != Guid.Empty
        && Guid.TryParse(ManagerId, out var manager) && actor == manager;
}

internal sealed class StandingJob
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Mandate { get; set; } = string.Empty;
    public string ManagerId { get; set; } = string.Empty;
    public List<StandingJobMember> Members { get; set; } = [];
    public List<string> Recipients { get; set; } = [];
    public List<string> Bindings { get; set; } = [];
    public List<string> SpecialistAgentIds { get; set; } = [];
    public string ConversationId { get; set; } = string.Empty;
    public string DecisionDelivery { get; set; } = "chat";
    public string RoutineName { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;
    public string TimeZone { get; set; } = "UTC";
    public bool Paused { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public DateTimeOffset? ReviewUtc { get; set; }
    public DateTimeOffset? NextCheckUtc { get; set; }
    public string? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset? LastRunUtc { get; set; }
    public string LastRunStatus { get; set; } = "not_run";
    public string LastRunDetail { get; set; } = string.Empty;

    public bool Allows(string actorId) =>
        string.Equals(ManagerId, actorId, StringComparison.OrdinalIgnoreCase)
        || Members.Any(member => string.Equals(member.Id, actorId, StringComparison.OrdinalIgnoreCase));
}

internal sealed record StandingJobEvent(
    string Id, string Kind, string ActorId, string Binding, string Content,
    DateTimeOffset ReceivedUtc, bool IsHuman, string? SourceUrl = null,
    string? DocumentItemId = null, string? DriveId = null, string? CommentId = null);

internal sealed record StandingBriefDraft(
    int Version, string FactsFingerprint, string Content, DateTimeOffset CreatedUtc);

internal sealed record StandingBriefChange(string EvidenceEventId, string Quote);

internal sealed record StandingJobInput(
    string Key, string Title, string OwnerId, string State, string Content,
    string EvidenceEventId, DateTimeOffset UpdatedUtc);

internal sealed record StandingJobDecision(
    string Id, string Statement, string Rationale, string EvidenceEventId,
    string RecordedBy, string? Supersedes, DateTimeOffset RecordedUtc);

internal sealed class StandingJobReceipt
{
    public string Key { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string PayloadHash { get; set; } = string.Empty;
    public string State { get; set; } = "pending";
    public string Detail { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
}

public sealed class StandingJobRow : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string Data { get; set; } = string.Empty;
}

public interface IStandingJobStore
{
    bool IsAvailable { get; }
    Task<StandingJobRow?> ReadAsync(string partition, string key, CancellationToken token = default);
    Task<IReadOnlyList<StandingJobRow>> ListAsync(string partition, string prefix, CancellationToken token = default);
    Task<bool> TryAddAsync(StandingJobRow row, CancellationToken token = default);
    Task<bool> TryReplaceAsync(StandingJobRow row, ETag expected, CancellationToken token = default);
}

public sealed class StandingJobStore : IStandingJobStore
{
    private readonly TableClient? _table;
    private readonly ILogger<StandingJobStore> _logger;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public StandingJobStore(IConfiguration configuration, ILogger<StandingJobStore> logger)
    {
        _logger = logger;
        if (!configuration.GetValue("EnableStandingJobs", true))
        {
            _logger.LogInformation("Standing jobs are disabled by configuration.");
            return;
        }
        var uri = new[]
        {
            configuration["StandingJobsTableServiceUri"],
            configuration["WorkItemsTableServiceUri"],
            configuration["DirectMessageAllowListTableServiceUri"]
        }.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (uri == null)
        {
            _logger.LogWarning("Standing jobs are unavailable: no durable table service is configured.");
            return;
        }
        try
        {
            var instance = Environment.GetEnvironmentVariable("FOUNDRY_AGENT_DEFAULT_INSTANCE_CLIENT_ID");
            TokenCredential credential = string.IsNullOrWhiteSpace(instance)
                ? new DefaultAzureCredential()
                : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(instance));
            _table = new TableClient(new Uri(uri),
                configuration["StandingJobsTableName"] ?? "standingjobs", credential);
            _table.CreateIfNotExists();
            _logger.LogInformation("Standing job storage initialized.");
        }
        catch (Exception ex) when (ex is RequestFailedException or AuthenticationFailedException or ArgumentException)
        {
            _logger.LogError(ex, "Standing job storage could not initialize; job tools are unavailable.");
            _table = null;
        }
    }

    public bool IsAvailable => _table != null;

    public async Task<StandingJobRow?> ReadAsync(string partition, string key, CancellationToken token = default)
    {
        var result = await RequiredTable().GetEntityIfExistsAsync<StandingJobRow>(partition, key, cancellationToken: token);
        return result.HasValue ? result.Value : null;
    }

    public async Task<IReadOnlyList<StandingJobRow>> ListAsync(string partition, string prefix, CancellationToken token = default)
    {
        var end = prefix + "~";
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {partition} and RowKey ge {prefix} and RowKey lt {end}");
        var rows = new List<StandingJobRow>();
        await foreach (var row in RequiredTable().QueryAsync<StandingJobRow>(filter, cancellationToken: token))
        {
            rows.Add(row);
        }
        return rows;
    }

    public async Task<bool> TryAddAsync(StandingJobRow row, CancellationToken token = default)
    {
        Validate(row);
        try
        {
            await RequiredTable().AddEntityAsync(row, token);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            return false;
        }
    }

    public async Task<bool> TryReplaceAsync(StandingJobRow row, ETag expected, CancellationToken token = default)
    {
        Validate(row);
        try
        {
            await RequiredTable().UpdateEntityAsync(row, expected, TableUpdateMode.Replace, token);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            return false;
        }
    }

    private TableClient RequiredTable() => _table
        ?? throw new InvalidOperationException("Standing-job durable storage is unavailable.");

    internal static string Partition(Guid tenantId, Guid agentUserId)
    {
        if (tenantId == Guid.Empty || agentUserId == Guid.Empty)
            throw new ArgumentException("Standing jobs require a tenant and an agent user account.");
        return $"{tenantId:D}:{agentUserId:D}";
    }

    internal static string JobKey(string id) => $"job-{Guid.Parse(id):N}";
    internal static string Prefix(string id, string kind) => $"record-{Guid.Parse(id):N}-{kind}-";
    internal static string Key(string id, string kind, string key) => Prefix(id, kind) + Hash(key);
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static StandingJobRow Row<T>(string partition, string key, T value) =>
        new() { PartitionKey = partition, RowKey = key, Data = JsonSerializer.Serialize(value, Json) };

    internal static T Value<T>(StandingJobRow row) =>
        JsonSerializer.Deserialize<T>(row.Data, Json)
        ?? throw new InvalidOperationException("A standing-job record is invalid.");

    private static void Validate(StandingJobRow row)
    {
        if (string.IsNullOrWhiteSpace(row.PartitionKey) || string.IsNullOrWhiteSpace(row.RowKey))
            throw new ArgumentException("Standing-job record keys are required.");
        if (Encoding.Unicode.GetByteCount(row.Data) > 60000)
            throw new ArgumentException("This record exceeds the durable record size limit; split the input into smaller records.");
    }
}
