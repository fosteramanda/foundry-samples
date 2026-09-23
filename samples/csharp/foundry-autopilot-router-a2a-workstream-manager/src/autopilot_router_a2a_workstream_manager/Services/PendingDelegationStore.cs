using Azure;
using Azure.Data.Tables;
using Azure.Identity;

namespace WorkstreamManager.Services;

/// <summary>
/// A delegation that was handed to another agent but had not produced an answer by the time the
/// turn had to end.
///
/// PartitionKey = the agent instance id, so one instance never polls another's work.
/// RowKey       = a generated id, because an agent can have several outstanding at once.
/// </summary>
public class PendingDelegationEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    /// <summary>Work IQ agent id the question went to.</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>Friendly name, captured at delegation time so the follow-up can name the agent.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>A2A task id to poll.</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>Fully-qualified A2A endpoint for this agent, so the poller need not rebuild it.</summary>
    public string A2AUrl { get; set; } = string.Empty;

    /// <summary>
    /// The question as it was sent. Answers arrive out of order and possibly minutes later, so the
    /// follow-up has to remind the user which question it belongs to; without this the message is
    /// an answer with no question attached.
    /// </summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>Id returned by Proactive.StoreConversationAsync — where to deliver the answer.</summary>
    public string ProactiveConversationId { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>Poll count, used to give up rather than poll a stuck task forever.</summary>
    public int Attempts { get; set; }

    // The delegating agent's own identity. Persisted because the follow-up runs outside any turn,
    // where there is no inbound activity to derive it from, and the poll must present the same
    // agent-user credential the original delegation used.
    public string OwnerUserId { get; set; } = string.Empty;
    public string OwnerAgentId { get; set; } = string.Empty;
    public string OwnerAppId { get; set; } = string.Empty;
    public string OwnerTenantId { get; set; } = string.Empty;
}

/// <summary>
/// Durable queue of delegations awaiting an answer.
///
/// Deliberately Azure Tables rather than the SDK's IStorage: the whole point of this feature is
/// that an answer arrives after the turn ends, and a container restart between the delegation and
/// the answer must not silently swallow it. Table storage is already provisioned for this agent
/// and already has a per-instance RBAC grant, so this adds no new infrastructure.
///
/// When no table is configured the store reports itself unavailable and the caller falls back to
/// the synchronous behaviour — degraded, but never silently dropping a question on the floor.
/// </summary>
public class PendingDelegationStore
{
    private readonly TableClient? _tableClient;
    private readonly ILogger<PendingDelegationStore> _logger;

    public PendingDelegationStore(IConfiguration configuration, ILogger<PendingDelegationStore> logger)
    {
        _logger = logger;

        // Fall back through the table URIs this agent actually has. The work-items account is
        // the natural home, but it is frequently unset while the allowlist account is always
        // configured and already carries the per-instance "Storage Table Data Contributor" grant
        // this agent needs. Without this fallback the store silently disables itself and the
        // follow-up promise is quietly broken — the exact failure this feature exists to prevent.
        var tableServiceUri = FirstNonEmpty(
            configuration["PendingDelegationTableServiceUri"],
            configuration["WorkItemsTableServiceUri"],
            configuration["DirectMessageAllowListTableServiceUri"],
            Environment.GetEnvironmentVariable("DirectMessageAllowListTableServiceUri"));
        var tableName = configuration["PendingDelegationTableName"] ?? "pendingdelegations";

        if (string.IsNullOrWhiteSpace(tableServiceUri))
        {
            _logger.LogWarning(
                "No table URI configured for pending delegations; asynchronous follow-up is DISABLED. " +
                "Delegations will answer synchronously or not at all.");
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
            _logger.LogInformation("PendingDelegationStore initialized with table {TableName} at {Uri}", tableName, tableServiceUri);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize PendingDelegationStore; asynchronous follow-up is disabled.");
            _tableClient = null;
        }
    }

    /// <summary>True when durable storage is available. False disables asynchronous follow-up.</summary>
    public bool IsAvailable => _tableClient != null;

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

    public async Task<bool> AddAsync(PendingDelegationEntity entity)
    {
        if (_tableClient == null)
        {
            return false;
        }

        try
        {
            entity.RowKey = string.IsNullOrWhiteSpace(entity.RowKey) ? Guid.NewGuid().ToString("n") : entity.RowKey;
            entity.CreatedUtc = DateTimeOffset.UtcNow;
            await _tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);
            _logger.LogInformation(
                "Pending delegation stored: agent={AgentId} task={TaskId} rowKey={RowKey}",
                entity.AgentId,
                entity.TaskId,
                entity.RowKey);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store pending delegation for agent {AgentId}", entity.AgentId);
            return false;
        }
    }

    public async Task<List<PendingDelegationEntity>> ListAsync(string partitionKey)
    {
        var results = new List<PendingDelegationEntity>();
        if (_tableClient == null)
        {
            return results;
        }

        try
        {
            await foreach (var e in _tableClient.QueryAsync<PendingDelegationEntity>(x => x.PartitionKey == partitionKey))
            {
                results.Add(e);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list pending delegations for partition {PartitionKey}", partitionKey);
        }

        return results;
    }

    public async Task UpdateAsync(PendingDelegationEntity entity)
    {
        if (_tableClient == null)
        {
            return;
        }

        try
        {
            await _tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update pending delegation {RowKey}", entity.RowKey);
        }
    }

    public async Task RemoveAsync(string partitionKey, string rowKey)
    {
        if (_tableClient == null)
        {
            return;
        }

        try
        {
            await _tableClient.DeleteEntityAsync(partitionKey, rowKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove pending delegation {RowKey}", rowKey);
        }
    }
}
