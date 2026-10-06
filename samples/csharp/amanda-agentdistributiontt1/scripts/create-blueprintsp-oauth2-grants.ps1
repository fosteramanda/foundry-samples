$ErrorActionPreference = "Stop"

$blueprintSP = az ad sp show --id $env:AGENT_IDENTITY_BLUEPRINT_ID --query id -o tsv

if ([string]::IsNullOrEmpty($blueprintSP)) {
    throw "Failed to get service principal for blueprint ID $($env:AGENT_IDENTITY_BLUEPRINT_ID)"
}

Write-Host "Creating OAuth2 permission grants for blueprint service principal..."


$apxAppId = "5a807f24-c9de-44ee-a3a7-329e88a00ffc"

$apxSP = az ad sp show --id $apxAppId --query id -o tsv
if ([string]::IsNullOrEmpty($apxSP)) {
    throw "Failed to get service principal for APEX app ID $apxAppId"
}

$prodMCPAppId = "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1"
$prodMCP_SP = az ad sp show --id $prodMCPAppId --query id -o tsv

if ([string]::IsNullOrEmpty($prodMCP_SP)) {
    throw "Failed to get service principal for Prod MCP app ID $prodMCPAppId"
}

# 00000003-0000-0000-c000-000000000000 is graph appId
$graphAppId = "00000003-0000-0000-c000-000000000000"
$graphSP = az ad sp show --id $graphAppId --query id -o tsv
if ([string]::IsNullOrEmpty($graphSP)) {
    throw "Failed to get service principal for Microsoft Graph app ID $graphAppId"
}

$graphToken = az account get-access-token --resource https://graph.microsoft.com/ --query accessToken -o tsv


$mcpOauthGrant = @"
{
  "clientId": "$blueprintSP",
  "consentType": "AllPrincipals",
  "principalId": null,
  "resourceId": "$prodMCP_SP",
  "scope": "McpServers.M365Admin.All McpServers.DASearch.All McpServers.WebSearch.All McpServers.Files.All AgentTools.MOSEvents.All McpServers.Admin365Graph.All McpServers.ERPAnalytics.All McpServers.DataverseCustom.All McpServers.Dataverse.All McpServers.D365Service.All McpServers.D365Sales.All McpServers.Management.All McpServersMetadata.Read.All McpServers.Developer.All McpServers.CopilotMCP.All McpServers.OneDriveSharepoint.All McpServers.Mail.All McpServers.Teams.All McpServers.Me.All McpServers.Calendar.All McpServers.SharepointLists.All McpServers.Knowledge.All McpServers.Excel.All McpServers.Word.All McpServers.PowerPoint.All"
}
"@
# Catch "Permission entry already exists" error and continue
try {
    $response = Invoke-RestMethod -Uri "https://graph.microsoft.com/v1.0/oauth2PermissionGrants" `
        -Method Post `
        -Headers @{
            "Content-Type" = "application/json"
            "Accept"       = "application/json"
            "Authorization" = "Bearer $($graphToken)"
        } `
        -Body $mcpOauthGrant

    Write-Host ""
    Write-Host "MCP oauth grant response:"
    $response | ConvertTo-Json -Depth 5 | Write-Host

} catch {
    $err = $_.ErrorDetails.Message | ConvertFrom-Json
    if ($err.error.code -eq "Request_BadRequest" -and
        $err.error.message -like "*Permission entry already exists*") {

        Write-Host "Permission already exists  ignoring."
    }
    else {
        throw
    }
}


try {
    $apxOauthGrant = @"
    {
        "clientId": "$blueprintSP",
        "consentType": "AllPrincipals",
        "principalId": null,
        "resourceId": "$apxSP",
        "scope": "AgentData.ReadWrite"
    }
"@

    $response = Invoke-RestMethod -Uri "https://graph.microsoft.com/v1.0/oauth2PermissionGrants" `
        -Method Post `
        -Headers @{
            "Content-Type" = "application/json"
            "Accept"       = "application/json"
            "Authorization" = "Bearer $($graphToken)"
        } `
        -Body $apxOauthGrant

    Write-Host ""
    Write-Host "APX oauth grant response:"
    $response | ConvertTo-Json -Depth 5 | Write-Host
}
catch {
    $err = $_.ErrorDetails.Message | ConvertFrom-Json
    if ($err.error.code -eq "Request_BadRequest" -and
        $err.error.message -like "*Permission entry already exists*") {

        Write-Host "Permission already exists  ignoring."
    }
    else {
        throw
    }
}

# -----------------------------------------------------------------------------
# Toolbox (Azure DevOps MCP) admin consent on the blueprint SP.
#
# Same oauth2PermissionGrant (AllPrincipals) pattern as the prod MCP / APX grants
# above, for the two audiences the agent-user impersonation flow mints tokens for:
#   - ai.azure.com (Foundry data plane): mints the toolbox bearer.
#   - Azure DevOps MCP server: the proxy exchanges the agent-user token toward ADO.
# The ADO MCP resource app often has NO service principal in the tenant yet, so we
# ensure one exists before granting (idempotent). Instances inherit this consent
# only when the resource app is ALSO declared in the blueprint's
# requiredResourceAccess (done at the end of this script) in addition to
# inheritablePermissions — inheritable alone does not inherit.
# Relevant only when the agent attaches a toolbox (appsettings ToolboxName set).
# -----------------------------------------------------------------------------
$toolboxAudiences = @(
    @{ Label = "Foundry data plane (ai.azure.com)"; AppId = "18a66f5f-dbdf-4c17-9dd7-1634712a9cbe" },
    @{ Label = "Azure DevOps MCP server";           AppId = "2a72489c-aab2-4b65-b93a-a91edccf33b8" }
)

foreach ($audience in $toolboxAudiences) {
    $audienceSp = az ad sp show --id $audience.AppId --query id -o tsv 2>$null
    if ([string]::IsNullOrEmpty($audienceSp)) {
        Write-Host "Service principal for $($audience.Label) ($($audience.AppId)) not found; creating it..."
        $audienceSp = az ad sp create --id $audience.AppId --query id -o tsv
        if ([string]::IsNullOrEmpty($audienceSp)) {
            throw "Failed to create service principal for $($audience.Label) ($($audience.AppId))."
        }
    }

    try {
        $toolboxGrant = @"
{
  "clientId": "$blueprintSP",
  "consentType": "AllPrincipals",
  "principalId": null,
  "resourceId": "$audienceSp",
  "scope": "user_impersonation"
}
"@
        $response = Invoke-RestMethod -Uri "https://graph.microsoft.com/v1.0/oauth2PermissionGrants" `
            -Method Post `
            -Headers @{
                "Content-Type"  = "application/json"
                "Accept"        = "application/json"
                "Authorization" = "Bearer $($graphToken)"
            } `
            -Body $toolboxGrant

        Write-Host ""
        Write-Host "$($audience.Label) oauth grant response:"
        $response | ConvertTo-Json -Depth 5 | Write-Host
    }
    catch {
        $err = $_.ErrorDetails.Message | ConvertFrom-Json
        if ($err.error.code -eq "Request_BadRequest" -and
            $err.error.message -like "*Permission entry already exists*") {
            Write-Host "$($audience.Label) permission already exists  ignoring."
        }
        else {
            throw
        }
    }
}

$graphReactionScopes = @(
    "ChatMessage.Send",
    "ChannelMessage.Send",
    "ChatMember.Read",
    "ChannelMessage.Read.All",
    "User.Read.All"
)
$graphDeprecatedScopes = @(
    "User.Read"
)
$graphReactionScopeString = ($graphReactionScopes -join ' ').Trim()

function Ensure-GraphOauthGrant {
    param(
        [Parameter(Mandatory = $true)][string] $ClientSpObjectId,
        [Parameter(Mandatory = $true)][string] $ClientLabel
    )

    try {
        $graphOauthGrant = @"
        {
            "clientId": "$ClientSpObjectId",
            "consentType": "AllPrincipals",
            "principalId": null,
            "resourceId": "$graphSP",
            "scope": "$graphReactionScopeString"
        }
"@

        $response = Invoke-RestMethod -Uri "https://graph.microsoft.com/v1.0/oauth2PermissionGrants" `
            -Method Post `
            -Headers @{
                "Content-Type" = "application/json"
                "Accept"       = "application/json"
                "Authorization" = "Bearer $($graphToken)"
            } `
            -Body $graphOauthGrant

        Write-Host ""
        Write-Host "Microsoft Graph oauth grant response ($ClientLabel):"
        $response | ConvertTo-Json -Depth 5 | Write-Host
    }
    catch {
        $errJson = $_.ErrorDetails.Message
        $err = $null
        if (-not [string]::IsNullOrWhiteSpace($errJson)) {
            $err = $errJson | ConvertFrom-Json
        }

        # Graph has returned both "Request_BadRequest" and "Request_MultipleObjectsWithSameKeyValue"
        # for this conflict over time. Match on the message text and accept either code.
        if ($err -and
            ($err.error.code -eq "Request_BadRequest" -or $err.error.code -eq "Request_MultipleObjectsWithSameKeyValue") -and
            $err.error.message -like "*Permission entry already exists*") {

            # oauth2PermissionGrants allows only ONE grant per (clientId, resourceId,
            # consentType, principalId) tuple. To add a new scope to an existing grant,
            # patch the existing record with the merged scope set.
            Write-Host "Permission entry already exists for $ClientLabel - checking whether scope set needs updating."

            $filter = "clientId eq '$ClientSpObjectId' and resourceId eq '$graphSP' and consentType eq 'AllPrincipals'"
            $existingResp = Invoke-RestMethod -Uri ("https://graph.microsoft.com/v1.0/oauth2PermissionGrants?`$filter=" + [uri]::EscapeDataString($filter)) `
                -Method Get `
                -Headers @{
                    "Accept"        = "application/json"
                    "Authorization" = "Bearer $($graphToken)"
                }

            $existing = $existingResp.value | Select-Object -First 1
            if (-not $existing) {
                throw "Graph returned 'Permission entry already exists' for $ClientLabel but the lookup found no matching grant. Aborting."
            }

            $existingScopes = @()
            if (-not [string]::IsNullOrWhiteSpace($existing.scope)) {
                $existingScopes = $existing.scope -split '\s+' | Where-Object { $_ }
            }

            $mergedScopes = @($existingScopes + $graphReactionScopes | Select-Object -Unique)
            $mergedScopes = @($mergedScopes | Where-Object { $graphDeprecatedScopes -notcontains $_ })
            $mergedScopeString = ($mergedScopes -join ' ').Trim()
            $existingScopeStringSorted = (($existingScopes | Sort-Object) -join ' ').Trim()
            $mergedScopeStringSorted = (($mergedScopes | Sort-Object) -join ' ').Trim()

            if ($existingScopeStringSorted -eq $mergedScopeStringSorted) {
                Write-Host "Existing scope set for $ClientLabel already contains all desired scopes; nothing to update."
            }
            else {
                Write-Host "Updating existing grant id=$($existing.id) for ${ClientLabel}:"
                Write-Host "  existing scopes: $($existing.scope)"
                Write-Host "  new scopes:      $mergedScopeString"

                $patchBody = @{ scope = $mergedScopeString } | ConvertTo-Json
                Invoke-RestMethod -Uri "https://graph.microsoft.com/v1.0/oauth2PermissionGrants/$($existing.id)" `
                    -Method Patch `
                    -Headers @{
                        "Content-Type"  = "application/json"
                        "Accept"        = "application/json"
                        "Authorization" = "Bearer $($graphToken)"
                    } `
                    -Body $patchBody | Out-Null

                Write-Host "Patched Microsoft Graph oauth grant successfully for $ClientLabel."
            }
        }
        else {
            throw
        }
    }
}

Write-Host "Ensuring Microsoft Graph oauth grant on blueprint service principal..."
Ensure-GraphOauthGrant -ClientSpObjectId $blueprintSP -ClientLabel "blueprint SP"

Write-Host "Ensuring blueprint inheritable Microsoft Graph scopes for reactions..."
& "$PSScriptRoot/add-blueprint-inheritable-scopes.ps1" `
    -BlueprintAppId $env:AGENT_IDENTITY_BLUEPRINT_ID `
    -ResourceAppId $graphAppId `
    -Scopes $graphReactionScopes

# Inheritable alone does not inherit: the permission must ALSO be declared in
# requiredResourceAccess for static consent to flow to agent identities.
Write-Host "Declaring Microsoft Graph in blueprint requiredResourceAccess..."
& "$PSScriptRoot/add-blueprint-required-resource-access.ps1" `
    -BlueprintAppId $env:AGENT_IDENTITY_BLUEPRINT_ID `
    -ResourceAppId $graphAppId `
    -Scopes $graphReactionScopes

# -----------------------------------------------------------------------------
# Toolbox (Azure DevOps MCP) capability scopes.
#
# The toolbox path authorizes via the agent-user impersonation flow, which mints
# tokens for two downstream audiences. Unlike the prod MCP / APX / Graph resources
# above (admin-consented via oauth2PermissionGrants), the Azure DevOps MCP resource
# app typically has NO service principal in the tenant, so it can't be granted that
# way. Instead we declare both audiences as inheritable scopes on the blueprint;
# they are consented when the published digital worker is approved in the admin
# center (same as the inheritable Graph scopes above).
#   - ai.azure.com (Foundry data plane): without it the agent token service can't
#     mint the toolbox bearer at all ("Failed to acquire toolbox bearer token").
#   - Azure DevOps MCP server (api://2a72489c-...): without it the toolbox proxy
#     can't exchange the agent-user token toward the ADO MCP audience.
# Relevant only when the agent attaches a toolbox (appsettings ToolboxName set);
# harmless otherwise. Access to the actual ADO org/project is a separate, post-hire
# grant on the agent user (see readme "Granting access").
# -----------------------------------------------------------------------------
$foundryDataPlaneAppId = "18a66f5f-dbdf-4c17-9dd7-1634712a9cbe"  # https://ai.azure.com
$resolvedFoundryAppId = az ad sp show --id "https://ai.azure.com" --query appId -o tsv 2>$null
if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($resolvedFoundryAppId)) {
    $foundryDataPlaneAppId = $resolvedFoundryAppId.Trim()
}
$adoMcpAppId = "2a72489c-aab2-4b65-b93a-a91edccf33b8"            # Azure DevOps MCP server (preview)

Write-Host "Ensuring blueprint inheritable Foundry data-plane scope (toolbox bearer)..."
& "$PSScriptRoot/add-blueprint-inheritable-scopes.ps1" `
    -BlueprintAppId $env:AGENT_IDENTITY_BLUEPRINT_ID `
    -ResourceAppId $foundryDataPlaneAppId `
    -Scopes "user_impersonation"

Write-Host "Ensuring blueprint inheritable Azure DevOps MCP scope (toolbox identity passthrough)..."
& "$PSScriptRoot/add-blueprint-inheritable-scopes.ps1" `
    -BlueprintAppId $env:AGENT_IDENTITY_BLUEPRINT_ID `
    -ResourceAppId $adoMcpAppId `
    -Scopes "user_impersonation"

# Declare both toolbox audiences in requiredResourceAccess. Without this, the
# inheritable declarations above never take effect and the agent identity fails
# with AADSTS65001 when minting the toolbox bearer — while the APX audience keeps
# working, because it IS declared. That asymmetry is what makes this bug so hard
# to spot: some tools work and some silently disappear.
Write-Host "Declaring Foundry data plane in blueprint requiredResourceAccess..."
& "$PSScriptRoot/add-blueprint-required-resource-access.ps1" `
    -BlueprintAppId $env:AGENT_IDENTITY_BLUEPRINT_ID `
    -ResourceAppId $foundryDataPlaneAppId `
    -Scopes "user_impersonation"

# The ADO MCP resource exposes "Ado.Mcp.Tools" (plus granular per-area scopes such
# as wit.read / wit.write); it does NOT expose "user_impersonation". Declare only
# the scope the toolbox proxy actually needs to reach the MCP server, and add
# granular data scopes here if a deployment needs more than tool invocation.
Write-Host "Declaring Azure DevOps MCP in blueprint requiredResourceAccess..."
& "$PSScriptRoot/add-blueprint-required-resource-access.ps1" `
    -BlueprintAppId $env:AGENT_IDENTITY_BLUEPRINT_ID `
    -ResourceAppId $adoMcpAppId `
    -Scopes "Ado.Mcp.Tools"

Write-Host "Per-agent Microsoft Graph oauth grant is intentionally not applied."
Write-Host "This environment relies on blueprint grant + inheritablePermissions + requiredResourceAccess."
