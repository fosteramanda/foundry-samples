namespace WorkstreamManager.Models;

using System.Text.Json.Serialization;

public class ToolingManifest
{
    [JsonPropertyName("mcpServers")]
    public List<McpServerConfig> McpServers { get; set; } = [];
}

public class McpServerConfig
{
    [JsonPropertyName("mcpServerName")]
    public string McpServerName { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;

    [JsonPropertyName("audience")]
    public string Audience { get; set; } = string.Empty;

    [JsonPropertyName("publisher")]
    public string Publisher { get; set; } = string.Empty;

    /// <summary>
    /// Optional extra HTTP headers to send to this MCP server alongside the Authorization
    /// bearer. Used by the Foundry toolbox MCP proxy, which requires a feature-flag header
    /// (e.g. "Foundry-Features: Toolboxes=V1Preview") while toolboxes are in preview.
    /// </summary>
    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }
}

