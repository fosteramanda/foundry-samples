using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Data.Tables;

namespace WorkstreamManager.Services;

/// <summary>
/// A request to be told in a 1:1 chat when a specific sender's email reaches the agent's own
/// mailbox. Stored in the conversation-state table under the agent instance's partition, with
/// a row-key prefix that no other row in that table uses.
/// </summary>
internal sealed class EmailWatchEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "";
    public string RowKey { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string ConversationId { get; set; } = "";
    public string RequesterId { get; set; } = "";
    public string SenderAddress { get; set; } = "";
    /// <summary>Every lowercased address that counts as this sender, separated by ';'.</summary>
    public string SenderAddresses { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string Note { get; set; } = "";
    public bool Repeat { get; set; }
    public string LastMessageKey { get; set; } = "";
    public int NotifiedCount { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }

    internal IEnumerable<string> Addresses =>
        SenderAddresses.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal bool Matches(string? address) =>
        !string.IsNullOrWhiteSpace(address)
        && Addresses.Contains(address.Trim(), StringComparer.OrdinalIgnoreCase);

    internal EmailWatchEntity Copy() => (EmailWatchEntity)MemberwiseClone();
}

internal interface IEmailWatchStore
{
    bool IsAvailable { get; }
    Task SaveAsync(EmailWatchEntity watch);
    Task<IReadOnlyList<EmailWatchEntity>> ListAsync(string partition);
    /// <summary>Deletes the watch only if it has not changed since it was read.</summary>
    Task<bool> RemoveAsync(EmailWatchEntity watch);
    /// <summary>Replaces the watch only if it has not changed since it was read.</summary>
    Task<bool> ReplaceAsync(EmailWatchEntity watch);
}

internal sealed class EmailWatchStore(TableClient? table) : IEmailWatchStore
{
    internal const string RowPrefix = "email-watch-";
    // '.' sorts immediately after '-', so this range covers exactly the prefixed rows.
    private const string RowRangeEnd = "email-watch.";

    public bool IsAvailable => table != null;
    private TableClient RequiredTable => table ?? throw new InvalidOperationException("Durable email-watch storage is unavailable.");

    internal static string RowKeyFor(string conversationId, string senderAddress) =>
        RowPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            conversationId + "\n" + senderAddress.Trim().ToLowerInvariant()))).ToLowerInvariant();

    public async Task SaveAsync(EmailWatchEntity watch)
    {
        var response = await RequiredTable.UpsertEntityAsync(watch, TableUpdateMode.Replace);
        if (response.Headers.ETag is { } etag) watch.ETag = etag;
    }

    public async Task<IReadOnlyList<EmailWatchEntity>> ListAsync(string partition)
    {
        var filter = TableClient.CreateQueryFilter(
            $"PartitionKey eq {partition} and RowKey ge {RowPrefix} and RowKey lt {RowRangeEnd}");
        var watches = new List<EmailWatchEntity>();
        await foreach (var watch in RequiredTable.QueryAsync<EmailWatchEntity>(filter))
            watches.Add(watch);
        return watches;
    }

    public async Task<bool> RemoveAsync(EmailWatchEntity watch)
    {
        try
        {
            await RequiredTable.DeleteEntityAsync(watch.PartitionKey, watch.RowKey, watch.ETag);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return false;
        }
    }

    public async Task<bool> ReplaceAsync(EmailWatchEntity watch)
    {
        try
        {
            var response = await RequiredTable.UpdateEntityAsync(watch, watch.ETag, TableUpdateMode.Replace);
            if (response.Headers.ETag is { } etag) watch.ETag = etag;
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return false;
        }
    }
}
