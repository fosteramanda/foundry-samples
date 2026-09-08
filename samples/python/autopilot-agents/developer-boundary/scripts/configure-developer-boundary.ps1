$ErrorActionPreference = "Stop"

function Get-RequiredAzdValue {
    param([Parameter(Mandatory)][string]$Name)

    $value = azd env get-value $Name 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($value)) {
        throw "Missing $Name. Run 'azd provision' and 'azd deploy' before this script."
    }

    return $value.Trim()
}

$projectEndpoint = (Get-RequiredAzdValue -Name "FOUNDRY_PROJECT_ENDPOINT").TrimEnd("/")
$agentName = Get-RequiredAzdValue -Name "AZURE_AI_AGENT_NAME"
$accessToken = az account get-access-token `
    --resource https://ai.azure.com `
    --query accessToken `
    --output tsv

if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($accessToken)) {
    throw "Failed to acquire an access token. Run 'az login' and try again."
}

$headers = @{
    Authorization = "Bearer $($accessToken.Trim())"
    Accept = "application/json"
    "Foundry-Features" = "HostedAgents=V1Preview,AgentEndpoints=V1Preview,DigitalWorker=V1Preview"
}

$expectedBoundaries = @(
    "read.1on1.developers"
    "write.1on1.developers"
    "read.group.developers"
    "write.group.developers"
)

$escapedAgentName = [Uri]::EscapeDataString($agentName)
$agentUri = "$projectEndpoint/agents/$escapedAgentName`?api-version=v1"
$currentAgent = Invoke-RestMethod `
    -Method Get `
    -Uri $agentUri `
    -Headers $headers

$protocolConfiguration = @{}
foreach ($property in $currentAgent.agent_endpoint.protocol_configuration.PSObject.Properties) {
    $protocolConfiguration[$property.Name] = $property.Value
}
$protocolConfiguration["activity"] = @{
    enable_m365_public_endpoint = $true
    autopilot_boundaries = $expectedBoundaries
}

$authorizationSchemes = @($currentAgent.agent_endpoint.authorization_schemes)
if ($authorizationSchemes.Count -eq 0) {
    throw "The agent endpoint has no authorization schemes to preserve."
}

$boundary = @{
    agent_endpoint = @{
        protocol_configuration = $protocolConfiguration
        authorization_schemes = $authorizationSchemes
    }
} | ConvertTo-Json -Depth 10

Invoke-RestMethod `
    -Method Patch `
    -Uri $agentUri `
    -Headers $headers `
    -ContentType "application/merge-patch+json" `
    -Body $boundary | Out-Null

$configuredAgent = Invoke-RestMethod `
    -Method Get `
    -Uri $agentUri `
    -Headers $headers
$activity = $configuredAgent.agent_endpoint.protocol_configuration.activity
if ($activity.enable_m365_public_endpoint -ne $true) {
    throw "Verification failed: the Microsoft 365 public endpoint is not enabled for '$agentName'."
}

$configuredBoundaries = @($activity.autopilot_boundaries)
$missingBoundaries = @($expectedBoundaries | Where-Object { $_ -notin $configuredBoundaries })
$unexpectedBoundaries = @($configuredBoundaries | Where-Object { $_ -notin $expectedBoundaries })
if ($missingBoundaries.Count -gt 0 -or $unexpectedBoundaries.Count -gt 0) {
    throw "Verification failed: the effective Developer Access Boundaries do not match the requested configuration."
}

Write-Host "Verified the Developer Access Boundary for '$agentName':"
Write-Host "  Microsoft 365 public endpoint: enabled"
Write-Host "  Boundaries:"
$configuredBoundaries | Sort-Object | ForEach-Object { Write-Host "    - $_" }
