namespace WorkstreamManager.Services;

using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Cached Responses API chain pointer for one conversation.
/// PartitionKey = "{tenantId}:{agentUserId}" (per agent instance), RowKey = hashed conversation id.
/// </summary>
public class ConversationStateEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    /// <summary>The Responses API response id to continue the chain from.</summary>
    public string ResponseId { get; set; } = string.Empty;

    /// <summary>
    /// Fingerprint of the MCP server set attached when this chain was written. A chain captures
    /// its tool inventory at the point it starts, so a change here must start a fresh chain.
    /// </summary>
    public string ToolFingerprint { get; set; } = string.Empty;

    /// <summary>Unhashed conversation id, for diagnosics only. Never used as a key.</summary>
    public string ConversationId { get; set; } = string.Empty;
}

/// <summary>
/// Durable store for per-conversation Responses API chain pointers.
///
/// These were previously written to <c>~/.a365agent/*.responseid</c> inside the container, which
/// made conversation continuity a property of a container rather than of a conversation: it was
/// lost silently whenever the platform recycled or rescheduled the container, and two replicas
/// would each keep their own divergent view. Both of the chain-related failures seen in
/// production traced back to that placement.
///
/// State now lives in the same table account the work-item tracker and allowlist already use,
/// partitioned per agent instance so one instance can never read another's conversations.
///
/// Falls back to the local-file store when no table URI is configured, so local runs and any
/// deployment that has not been given a table keep working.
/// </summary>
public class ConversationStateStore
{
    private readonly TableClient? _tableClient;
    private readonly ILogger<ConversationStateStore> _logger;

    public ConversationStateStore(IConfiguration configuration, ILogger<ConversationStateStore> logger)
    {
        _logger = logger;

        // Reuse the work-items table account: same lifetime, same per-instance RBAC grant, and
        // one less thing to provision. The table name is separately configurable.
        var tableServiceUri = configuration["ConversationStateTableServiceUri"]
            ?? configuration["WorkItemsTableServiceUri"];
        var tableName = configuration["ConversationStateTableName"] ?? "conversationstate";

        if (string.IsNullOrWhiteSpace(tableServiceUri))
        {
            _logger.LogWarning(
                "No table URI configured for conversation state; falling back to container-local files. " +
                "Conversation continuity will not survive a container restart.");
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
            _logger.LogInformation("ConversationStateStore initialized with table {TableName} at {Uri}", tableName, tableServiceUri);
        }
        catch (Exception ex)
        {
            // Never let a storage problem take the agent down - it degrades to the local-file
            // path, which is what the agent did for its entire life before this store existed.
            _logger.LogError(ex, "Failed to initialize ConversationStateStore; falling back to container-local files.");
            _tableClient = null;
        }
    }

    /// <summary>True when durable storage is available; false means the local-file fallback is in use.</summary>
    public bool IsDurable => _tableClient != null;

    /// <summary>
    /// Loads the chain pointer for a conversation. Returns null when absent, when storage is
    /// unavailable, or when the stored fingerprint does not match <paramref name="expectedToolFingerprint"/>
    /// (a non-null value asks for the check; null skips it, for tool-less passes).
    /// </summary>
    public async Task<string?> LoadAsync(string partitionKey, string conversationId, string? expectedToolFingerprint)
    {
        if (_tableClient == null)
        {
            return null;
        }

        try
        {
            var response = await _tableClient.GetEntityIfExistsAsync<ConversationStateEntity>(partitionKey, HashConversationId(conversationId));
            if (!response.HasValue || response.Value is null)
            {
                return null;
            }

            var entity = response.Value;
            if (expectedToolFingerprint != null && entity.ToolFingerprint != expectedToolFingerprint)
            {
                _logger.LogInformation(
                    "Tool set changed for conversation {ConversationId} (stored={StoredFingerprint}, current={CurrentFingerprint}); " +
                    "starting a fresh response chain so the model sees the current tools.",
                    conversationId,
                    string.IsNullOrEmpty(entity.ToolFingerprint) ? "(none)" : entity.ToolFingerprint,
                    expectedToolFingerprint);
                return null;
            }

            return string.IsNullOrEmpty(entity.ResponseId) ? null : entity.ResponseId;
        }
        catch (Exception ex)
        {
            // A read failure must not fail the turn: losing continuity costs the model its memory
            // of the thread, which is strictly better than refusing to answer.
            _logger.LogWarning(ex, "Failed to load conversation state for {ConversationId}; continuing without prior context.", conversationId);
            return null;
        }
    }

    public async Task SaveAsync(string partitionKey, string conversationId, string responseId, string? toolFingerprint)
    {
        if (_tableClient == null || string.IsNullOrEmpty(responseId))
        {
            return;
        }

        try
        {
            var entity = new ConversationStateEntity
            {
                PartitionKey = partitionKey,
                RowKey = HashConversationId(conversationId),
                ResponseId = responseId,
                ToolFingerprint = toolFingerprint ?? string.Empty,
                ConversationId = conversationId,
            };

            await _tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save conversation state for {ConversationId}.", conversationId);
        }
    }

    /// <summary>
    /// Drops the chain pointer, used when the service reports the stored response no longer
    /// exists so the next turn starts clean instead of re-sending a dangling id forever.
    /// </summary>
    public async Task ClearAsync(string partitionKey, string conversationId)
    {
        if (_tableClient == null)
        {
            return;
        }

        try
        {
            await _tableClient.DeleteEntityAsync(partitionKey, HashConversationId(conversationId));
            _logger.LogInformation("Cleared conversation state for {ConversationId}.", conversationId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear conversation state for {ConversationId}.", conversationId);
        }
    }

    /// <summary>
    /// Table row keys cannot contain '/', '\', '#' or '?', and are length-bounded. Teams
    /// conversation ids contain several of those and Word comment ids can be very long, so key
    /// on a hash. Deterministic, so load and save always agree.
    /// </summary>
    private static string HashConversationId(string conversationId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(conversationId))).ToLowerInvariant();
}
