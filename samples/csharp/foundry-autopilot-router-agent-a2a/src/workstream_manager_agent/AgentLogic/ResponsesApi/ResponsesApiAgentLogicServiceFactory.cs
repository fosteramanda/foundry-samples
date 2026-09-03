namespace WorkstreamManager.AgentLogic.ResponsesApi;

using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using WorkstreamManager.Models;
using WorkstreamManager.Services;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App.UserAuth;

/// <summary>
/// Factory for creating ResponsesApiAgentLogicService instances.
/// Discovers MCP servers either from the Agent365 API or from a local ToolingManifest.json,
/// controlled by the "McpDiscoverySource" config setting ("API" or "Manifest").
/// </summary>
public sealed class ResponsesApiAgentLogicServiceFactory(
    IConfiguration configuration,
    ILogger<ResponsesApiAgentLogicServiceFactory> logger,
    AgentTokenHelper tokenHelper,
    ConversationStateStore conversationState,
    PendingDelegationStore pendingDelegations)
{
    private static readonly HttpClient HttpClient = new();

    public async Task<IAgentLogicService> CreateAsync(AgentMetadata agent, ITurnContext turnContext, UserAuthorization userAuthorization)
        => await CreateForAgentAsync(agent);

    public async Task<IAgentLogicService> CreateForAgentAsync(AgentMetadata agent)
    {
        // Acquire token for MCP servers. Use a fresh AgentTokenCredential instance per scope:
        // AgentTokenCredential.cachedToken does NOT key on the requested scope, so reusing a
        // single instance across different audiences returns the first-acquired token for every
        // subsequent call — Graph would then reject our MCP-audience token with "Invalid audience".
        //
        // The scope depends on the discovery mode:
        //  - Manifest/API: the WorkIQ (agent365) MCP servers expect an agent-user token for the
        //    APX audience, sent directly to each server.
        //  - Toolbox: the bearer goes to the Foundry toolbox MCP proxy instead. The proxy
        //    authenticates the caller and, for UserEntraToken connections, passes the caller's
        //    identity through to the downstream tool — so this must be an *agent user* token
        //    (the autopilot identity), not an app-only token. The scope is configurable via
        //    ToolboxAccessTokenScope while toolbox auth is in preview; it defaults to the
        //    Foundry data-plane audience.
        //  - Manifest/API + ToolboxName set (hybrid): the discovered servers keep the APX
        //    bearer, and the toolbox proxy is ATTACHED as one more MCP entry carrying its own
        //    Authorization header (see below). This is the default posture for this sample —
        //    the agent keeps its WorkIQ tools (Word, Mail, OneDrive, …) and gains the toolbox
        //    tools (e.g. Azure DevOps MCP) on top.
        var discoverySource = configuration["McpDiscoverySource"] ?? "API";
        var isToolboxMode = discoverySource.Equals("Toolbox", StringComparison.OrdinalIgnoreCase);
        var configuredToolboxScope = configuration["ToolboxAccessTokenScope"];
        var mcpScope = isToolboxMode
            ? (string.IsNullOrWhiteSpace(configuredToolboxScope) ? "https://ai.azure.com/.default" : configuredToolboxScope.Trim())
            : "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1/.default";

        var mcpRequestContext = new TokenRequestContext([mcpScope]);
        var mcpTokenCredential = new AgentTokenCredential(tokenHelper, agent);
        var accessToken = await mcpTokenCredential.GetTokenAsync(mcpRequestContext, CancellationToken.None);

        logger.LogInformation(
            "Acquired token for Responses API MCP tools (mode={DiscoverySource}, scope={Scope}). Expires at: {Expiration}",
            discoverySource,
            mcpScope,
            accessToken.ExpiresOn);

        // Acquire a Microsoft Graph token alongside the MCP token so the agent logic can call
        // Graph (e.g. setReaction acknowledgments, resolving user identifiers for access control
        // and work item assignment, and chat-member lookups for the addressed-to-agent gate).
        // We tolerate failure here — Graph lookups are an enhancement; the agent still works without them.
        string? graphAccessToken = null;
        try
        {
            var graphRequestContext = new TokenRequestContext(["https://graph.microsoft.com/.default"]);
            var graphTokenCredential = new AgentTokenCredential(tokenHelper, agent);
            var graphToken = await graphTokenCredential.GetTokenAsync(graphRequestContext, CancellationToken.None);
            graphAccessToken = graphToken.Token;
            logger.LogInformation("Acquired Graph token for chat-members lookup. Expires at: {Expiration}", graphToken.ExpiresOn);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to acquire Graph token; Graph-dependent features will be disabled.");
        }

        var mcpServers = await GetMcpServersAsync(agent.AgentId, accessToken.Token);

        // Hybrid mode: when a toolbox is configured but McpDiscoverySource is Manifest/API,
        // attach the toolbox MCP proxy ALONGSIDE the discovered servers instead of replacing
        // them. The manifest/API servers keep the APX-audience bearer acquired above; the
        // toolbox entry carries its own Authorization header (per-server headers override the
        // default bearer in ResponsesApiClient), minted for the Foundry data-plane audience —
        // again as the agent user, so UserEntraToken connections inside the toolbox pass the
        // digital worker's own identity through to e.g. Azure DevOps.
        if (!isToolboxMode && !string.IsNullOrWhiteSpace(configuration["ToolboxName"]))
        {
            var toolboxEntry = LoadFromToolbox().FirstOrDefault();
            if (toolboxEntry != null)
            {
                try
                {
                    var toolboxScope = string.IsNullOrWhiteSpace(configuredToolboxScope)
                        ? "https://ai.azure.com/.default"
                        : configuredToolboxScope.Trim();
                    // Fresh credential instance — AgentTokenCredential caches one token per
                    // instance without keying on scope (see comment at the top of this method).
                    var toolboxCredential = new AgentTokenCredential(tokenHelper, agent);
                    var toolboxToken = await toolboxCredential.GetTokenAsync(
                        new TokenRequestContext([toolboxScope]), CancellationToken.None);
                    toolboxEntry.Headers ??= new Dictionary<string, string>();
                    toolboxEntry.Headers["Authorization"] = $"Bearer {toolboxToken.Token}";
                    mcpServers.Add(toolboxEntry);
                    logger.LogInformation(
                        "Attached Foundry toolbox '{ToolboxName}' alongside {ServerCount} {Source} MCP server(s) (toolboxScope={Scope}).",
                        toolboxEntry.McpServerName,
                        mcpServers.Count - 1,
                        discoverySource,
                        toolboxScope);
                }
                catch (Exception ex)
                {
                    // Toolbox tools are an enhancement on top of the core WorkIQ tools — never
                    // let a toolbox auth failure take the whole agent down.
                    logger.LogWarning(ex, "Failed to acquire toolbox bearer token; continuing without toolbox tools.");
                }
            }
        }

        IAgentLogicService service = new ResponsesApiAgentLogicService(
            agent,
            configuration,
            logger,
            accessToken.Token,
            mcpServers,
            graphAccessToken,
            conversationState,
            tokenHelper,
            pendingDelegations);

        return service;
    }

    private async Task<List<McpServerConfig>> GetMcpServersAsync(Guid agentInstanceId, string accessToken)
    {
        var source = configuration["McpDiscoverySource"] ?? "API";

        if (source.Equals("Manifest", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Loading MCP servers from ToolingManifest.json");
            return LoadFromManifest();
        }

        if (source.Equals("Toolbox", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Using Foundry toolbox MCP proxy as the tool source");
            return LoadFromToolbox();
        }

        logger.LogInformation("Discovering MCP servers from API for agent {AgentId}", agentInstanceId);
        return await DiscoverFromApiAsync(agentInstanceId, accessToken);
    }

    /// <summary>
    /// Builds a single MCP server entry pointing at a Foundry toolbox's MCP proxy endpoint.
    /// The toolbox (a Foundry project resource) bundles one or more MCP tools — each backed by
    /// a project connection that declares its auth — behind one endpoint:
    ///   {projectEndpoint}/toolboxes/{name}/mcp?api-version=v1
    /// The proxy resolves each tool's connection credential server-side. For connections with
    /// authType UserEntraToken (identity passthrough, e.g. WorkIQ Calendar or the Azure DevOps
    /// MCP server), the proxy forwards the caller's identity — for an autopilot agent that is
    /// the agent user, so downstream tools act as the digital worker itself.
    /// </summary>
    private List<McpServerConfig> LoadFromToolbox()
    {
        var projectEndpoint = configuration["FoundryProjectEndpoint"];
        if (string.IsNullOrWhiteSpace(projectEndpoint))
        {
            projectEndpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT");
        }
        var toolboxName = configuration["ToolboxName"];

        if (string.IsNullOrWhiteSpace(projectEndpoint) || string.IsNullOrWhiteSpace(toolboxName))
        {
            logger.LogError(
                "Toolbox discovery requires FoundryProjectEndpoint (or the FOUNDRY_PROJECT_ENDPOINT " +
                "environment variable) and ToolboxName. projectEndpointSet={ProjectEndpointSet} toolboxNameSet={ToolboxNameSet}",
                !string.IsNullOrWhiteSpace(projectEndpoint),
                !string.IsNullOrWhiteSpace(toolboxName));
            return [];
        }

        // Optional version pin: /toolboxes/{name}/versions/{v}/mcp targets a specific toolbox
        // version; without it the proxy serves the latest version.
        var toolboxVersion = configuration["ToolboxVersion"];
        var toolboxPath = string.IsNullOrWhiteSpace(toolboxVersion)
            ? $"toolboxes/{Uri.EscapeDataString(toolboxName)}"
            : $"toolboxes/{Uri.EscapeDataString(toolboxName)}/versions/{Uri.EscapeDataString(toolboxVersion.Trim())}";
        var toolboxMcpUrl = $"{projectEndpoint.TrimEnd('/')}/{toolboxPath}/mcp?api-version=v1";

        // Preview feature-flag header required by the toolbox MCP proxy. Hosted containers may
        // inject FOUNDRY_AGENT_TOOLBOX_FEATURES; config overrides, then the documented default.
        var features = configuration["ToolboxFeaturesHeader"];
        if (string.IsNullOrWhiteSpace(features))
        {
            features = Environment.GetEnvironmentVariable("FOUNDRY_AGENT_TOOLBOX_FEATURES");
        }
        if (string.IsNullOrWhiteSpace(features))
        {
            features = "Toolboxes=V1Preview";
        }

        logger.LogInformation(
            "Toolbox MCP endpoint resolved: {ToolboxUrl} (featuresHeader={Features})",
            toolboxMcpUrl,
            features);

        return
        [
            new McpServerConfig
            {
                McpServerName = toolboxName,
                Url = toolboxMcpUrl,
                Headers = new Dictionary<string, string>
                {
                    ["Foundry-Features"] = features,
                },
            },
        ];
    }

    private List<McpServerConfig> LoadFromManifest()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "ToolingManifest.json");
        if (!File.Exists(manifestPath))
        {
            logger.LogWarning("ToolingManifest.json not found at {Path}", manifestPath);
            return [];
        }

        var json = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<ToolingManifest>(json);
        var servers = manifest?.McpServers ?? [];
        logger.LogInformation("Loaded {Count} MCP servers from ToolingManifest.json", servers.Count);
        return servers;
    }

    private async Task<List<McpServerConfig>> DiscoverFromApiAsync(Guid agentInstanceId, string accessToken)
    {
        var url = $"https://agent365.svc.cloud.microsoft/agents/v2/{agentInstanceId}/mcpServers";
        logger.LogInformation("Discovering MCP servers from {Url}", url);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await HttpClient.SendAsync(request);
        var responseContent = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Failed to discover MCP servers. Status: {StatusCode}, Response: {Response}", response.StatusCode, responseContent);
            return [];
        }

        var servers = JsonSerializer.Deserialize<List<McpServerConfig>>(responseContent) ?? [];
        logger.LogInformation("Discovered {Count} MCP servers for agent {AgentId}", servers.Count, agentInstanceId);

        foreach (var server in servers)
        {
            logger.LogInformation("  MCP Server: {Name} ({Url})", server.McpServerName, server.Url);
        }

        return servers;
    }
}

