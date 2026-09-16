<#
.SYNOPSIS
    Creates (or updates) the Foundry toolbox this autopilot agent attaches at runtime
    (appsettings: ToolboxName), bundling the Azure DevOps MCP server — and optionally
    WorkIQ Outlook Calendar — behind one MCP proxy endpoint.

.DESCRIPTION
    A toolbox is a Foundry project resource that bundles MCP tools behind one MCP proxy
    endpoint ({project}/toolboxes/{name}/mcp). Each tool references a project CONNECTION
    that declares how the proxy authenticates to the downstream server. For autopilot
    agents the tools here use identity passthrough (authType UserEntraToken): the proxy
    forwards the caller's Entra identity — the agent user — so Azure DevOps sees the
    digital worker itself, not a human or an app-only principal.

    EASIEST PATH — the Foundry portal does all of this in two steps, no script needed:
      1. Build → Tools → "Azure DevOps MCP Server (preview)" → Connect:
           orgName        = <your ADO organization>   (endpoint becomes https://mcp.dev.azure.com/<org>)
           Authentication = OAuth Identity Passthrough
           OAuth Provider = Managed
         This creates a project connection (default name "AzureDevOpsMCPServerpreview",
         category RemoteTool, authType UserEntraToken,
         audience api://2a72489c-aab2-4b65-b93a-a91edccf33b8).
      2. Build → Toolboxes → New toolbox → add the connected tool. The toolbox page then
         shows the MCP endpoint the agent uses.

    This script is the REST automation equivalent of step 2 only: it PUTs the toolbox
    definition referencing the connection created in step 1 (or any existing connection).

    NOTE: the toolboxes data plane is in preview (api-version=v1 + Foundry-Features
    header). If your project is on a different preview ring, adjust -ApiVersion /
    -FeaturesHeader.

.PARAMETER ProjectEndpoint
    Foundry project endpoint, e.g. https://<account>.services.ai.azure.com/api/projects/<project>

.PARAMETER ToolboxName
    Toolbox name. The agent's appsettings ToolboxName must match. Default: workstream-manager-ado

.PARAMETER AdoOrgName
    Azure DevOps organization name. The ADO MCP endpoint is derived from it:
    https://mcp.dev.azure.com/<org>

.PARAMETER AdoConnectionName
    Name of the EXISTING project connection for the ADO MCP server (created by the portal
    Tools UI). Default: AzureDevOpsMCPServerpreview

.PARAMETER CalendarConnectionName
    Optional. Name of an existing WorkIQ Calendar connection (category RemoteTool,
    authType UserEntraToken, audience ea9ffc3e-8a23-4a7d-836d-234d7c7565c1, target
    https://agent365.svc.cloud.microsoft/agents/servers/mcp_CalendarTools). When set,
    the calendar tool is added to the toolbox alongside ADO.

.EXAMPLE
    ./create-toolbox.ps1 `
        -ProjectEndpoint "https://foundryworkstreammangeracct.services.ai.azure.com/api/projects/foundryworkstreammangerproj" `
        -AdoOrgName "notarealco"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectEndpoint,

    [string]$ToolboxName = "workstream-manager-ado",

    [Parameter(Mandatory = $true)]
    [string]$AdoOrgName,

    [string]$AdoConnectionName = "AzureDevOpsMCPServerpreview",

    [string]$CalendarConnectionName = "",

    [string]$ApiVersion = "v1",

    [string]$FeaturesHeader = "Toolboxes=V1Preview"
)

$ErrorActionPreference = "Stop"

# Data-plane token for the Foundry project (your signed-in identity needs a project data-plane
# role, e.g. Azure AI User, on the project).
$token = az account get-access-token --resource "https://ai.azure.com" --query accessToken -o tsv
if (-not $token) {
    throw "Failed to acquire a token for https://ai.azure.com — run 'az login' first."
}

$adoMcpUrl = "https://mcp.dev.azure.com/$AdoOrgName"
$toolboxUri = "$($ProjectEndpoint.TrimEnd('/'))/toolboxes/$([uri]::EscapeDataString($ToolboxName))?api-version=$ApiVersion"

$tools = @(
    @{
        type                  = "mcp"
        server_label          = "azure-devops"
        project_connection_id = $AdoConnectionName
        server_url            = $adoMcpUrl
    }
)
if ($CalendarConnectionName) {
    $tools += @{
        type                  = "mcp"
        server_label          = "workiq-calendar"
        project_connection_id = $CalendarConnectionName
        server_url            = "https://agent365.svc.cloud.microsoft/agents/servers/mcp_CalendarTools"
    }
}

$body = @{
    description = "Toolbox for the Workstream Manager autopilot agent. Tools authenticate via agent-user identity passthrough (UserEntraToken)."
    tools       = $tools
} | ConvertTo-Json -Depth 6

Write-Host "PUT $toolboxUri"
$response = Invoke-RestMethod -Method Put -Uri $toolboxUri -Body $body -ContentType "application/json" -Headers @{
    Authorization      = "Bearer $token"
    "Foundry-Features" = $FeaturesHeader
}

Write-Host "Toolbox '$ToolboxName' created/updated."
$response | ConvertTo-Json -Depth 6 | Write-Host

Write-Host ""
Write-Host "Agent MCP endpoint for this toolbox:"
Write-Host "  $($ProjectEndpoint.TrimEnd('/'))/toolboxes/$ToolboxName/mcp?api-version=$ApiVersion"
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. appsettings: ToolboxName=$ToolboxName, FoundryProjectEndpoint=$ProjectEndpoint"
Write-Host "     (keep McpDiscoverySource=Manifest — the toolbox is attached alongside the WorkIQ tools)."
Write-Host "  2. AFTER the agent instance is hired in Teams (agent identity + agent user exist):"
Write-Host "     grant the agent user access to the ADO organization '$AdoOrgName' and the target"
Write-Host "     project (Organization settings -> Users -> Add user; then add to the project/team)."
