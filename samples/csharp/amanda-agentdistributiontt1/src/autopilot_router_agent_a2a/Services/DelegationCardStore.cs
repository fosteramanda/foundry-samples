using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Data.Tables;

namespace WorkstreamManager.Services;

internal static class DelegationCardStates
{
    internal const string Working = "working";
    internal const string Pending = "pending";
    internal const string Response = "response";
    internal const string Review = "review";
    internal const string Saving = "saving";
    internal const string Verifying = "verifying";
    internal const string Saved = "saved";
    internal const string Failed = "failed";
    internal const string Unconfirmed = "unconfirmed";
    internal const string Stale = "stale";
    internal const string Declined = "declined";
}

internal sealed class DelegationCardEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "";
    public string RowKey { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string ConversationId { get; set; } = "";
    public string ConversationJson { get; set; } = "";
    public string ActivityId { get; set; } = "";
    public string RequesterId { get; set; } = "";
    public string ManagerId { get; set; } = "";
    public string OwnerAgentId { get; set; } = "";
    public string OwnerName { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string ParentScope { get; set; } = "";
    public string ContextScope { get; set; } = "";
    public string ContextId { get; set; } = "";
    public string ContextRevision { get; set; } = "";
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
    public string State { get; set; } = DelegationCardStates.Working;
    public string Detail { get; set; } = "";
    public string PreviewJson { get; set; } = "";
    public string ReviewedPreviewJson { get; set; } = "";
    public bool ApprovalIssued { get; set; }
    public string PlanUrl { get; set; } = "";
    public string PlanId { get; set; } = "";
    public string TasksJson { get; set; } = "[]";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

internal sealed class DelegationScopeEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "";
    public string RowKey { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string Revision { get; set; } = "";
    public string LeaseId { get; set; } = "";
    public DateTimeOffset LeaseUntilUtc { get; set; }
}

internal sealed class DelegationCardRouteEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "";
    public string RowKey { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string OwnerAgentId { get; set; } = "";
    public string RequesterId { get; set; } = "";
    public string ManagerId { get; set; } = "";
    public string OwnerName { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string ConversationJson { get; set; } = "";
}

internal sealed record DelegationScopeLease(string Partition, string Scope, string Revision);

internal interface IDelegationCardStore
{
    bool IsAvailable { get; }
    Task AddAsync(DelegationCardEntity card);
    Task<DelegationCardEntity?> GetAsync(string partition, string id);
    Task<bool> ReplaceAsync(DelegationCardEntity card);
    Task<DelegationScopeLease> AcquireScopeAsync(string partition, string scope, string? expectedRevision = null);
    Task ReleaseScopeAsync(DelegationScopeLease lease);
    Task<string?> GetRevisionAsync(string partition, string scope);
    Task SaveRouteAsync(DelegationCardRouteEntity route);
    Task<DelegationCardRouteEntity?> GetRouteAsync(string partition, string requesterId, string ownerAgentId);
}

internal sealed class DelegationCardStore(TableClient? table, TimeProvider? clock = null) : IDelegationCardStore
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public bool IsAvailable => table != null;
    private TableClient RequiredTable => table ?? throw new InvalidOperationException("Durable delegation-card storage is unavailable.");
    private static string ScopeRow(string scope) =>
        "card-scope-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))).ToLowerInvariant();

    public async Task AddAsync(DelegationCardEntity card)
    {
        var response = await RequiredTable.AddEntityAsync(card);
        if (response.Headers.ETag is { } etag) card.ETag = etag;
    }

    public async Task<DelegationCardEntity?> GetAsync(string partition, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new ArgumentException("Invalid delegation card reference.");
        var value = await RequiredTable.GetEntityIfExistsAsync<DelegationCardEntity>(partition, "card-" + id);
        return value.HasValue ? value.Value : null;
    }

    public async Task<bool> ReplaceAsync(DelegationCardEntity card)
    {
        card.UpdatedUtc = _clock.GetUtcNow();
        try
        {
            var response = await RequiredTable.UpdateEntityAsync(card, card.ETag, TableUpdateMode.Replace);
            if (response.Headers.ETag is { } etag) card.ETag = etag;
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            return false;
        }
    }

    public async Task<DelegationScopeLease> AcquireScopeAsync(string partition, string scope, string? expectedRevision = null)
    {
        var row = ScopeRow(scope);
        var response = await RequiredTable.GetEntityIfExistsAsync<DelegationScopeEntity>(partition, row);
        var current = response.HasValue ? response.Value : null;
        if (current is { LeaseId.Length: > 0 } && current.LeaseUntilUtc > _clock.GetUtcNow())
            throw new InvalidOperationException("Another request to this specialist is still running. No additional request was sent.");
        if (expectedRevision != null && current?.Revision != expectedRevision)
            throw new InvalidOperationException("This preview is stale. Ask for a fresh preview before approving.");
        var revision = Guid.NewGuid().ToString("N");
        var next = new DelegationScopeEntity
        {
            PartitionKey = partition, RowKey = row, Revision = revision, LeaseId = revision,
            LeaseUntilUtc = _clock.GetUtcNow().AddMinutes(10)
        };
        try
        {
            if (current == null)
                await RequiredTable.AddEntityAsync(next);
            else
                await RequiredTable.UpdateEntityAsync(next, current.ETag, TableUpdateMode.Replace);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            throw new InvalidOperationException("The specialist conversation changed while this request was being prepared. No additional request was sent.", ex);
        }
        return new DelegationScopeLease(partition, scope, revision);
    }

    public async Task ReleaseScopeAsync(DelegationScopeLease lease)
    {
        var response = await RequiredTable.GetEntityIfExistsAsync<DelegationScopeEntity>(lease.Partition, ScopeRow(lease.Scope));
        var current = response.HasValue ? response.Value : null;
        if (current == null || current.LeaseId != lease.Revision)
            throw new InvalidOperationException("The delegation lease changed before completion. Do not repeat an uncertain write.");
        current.LeaseId = "";
        current.LeaseUntilUtc = _clock.GetUtcNow();
        await RequiredTable.UpdateEntityAsync(current, current.ETag, TableUpdateMode.Replace);
    }

    public async Task<string?> GetRevisionAsync(string partition, string scope)
    {
        var response = await RequiredTable.GetEntityIfExistsAsync<DelegationScopeEntity>(partition, ScopeRow(scope));
        return response.HasValue ? response.Value?.Revision : null;
    }

    private static string RouteRow(string requesterId, string ownerAgentId) =>
        "card-route-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ownerAgentId + ":" + requesterId))).ToLowerInvariant();

    public async Task SaveRouteAsync(DelegationCardRouteEntity route)
    {
        route.RowKey = RouteRow(route.RequesterId, route.OwnerAgentId);
        await RequiredTable.UpsertEntityAsync(route, TableUpdateMode.Replace);
    }

    public async Task<DelegationCardRouteEntity?> GetRouteAsync(string partition, string requesterId, string ownerAgentId)
    {
        var result = await RequiredTable.GetEntityIfExistsAsync<DelegationCardRouteEntity>(
            partition, RouteRow(requesterId, ownerAgentId));
        return result.HasValue ? result.Value : null;
    }
}
