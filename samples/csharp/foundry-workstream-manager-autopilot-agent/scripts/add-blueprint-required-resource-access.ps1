<#
.SYNOPSIS
  Declares requiredResourceAccess entries on an AgentIdentityBlueprint application
  so that admin-consented permissions actually INHERIT to its agent identities.

.DESCRIPTION
  Inheritance to an agent identity requires BOTH of the following
  (https://learn.microsoft.com/entra/agent-id/concept-inheritable-permissions):

    1. The resource app is listed in the blueprint's inheritablePermissions, and
    2. The permission is granted with static consent using requiredResourceAccess
       (or dynamic consent with the permission explicitly requested).

  Declaring inheritable scopes and granting consent on the blueprint service
  principal is NOT sufficient on its own. Per the documented static-consent
  matrix, "not in requiredResourceAccess + inheritable" yields NO inheritance,
  which surfaces at runtime as:

    AADSTS65001: The user or administrator has not consented to use the
    application with ID '<agent identity appId>'

  ...on every audience except the ones that happen to be declared. Because
  inherited permissions are not visible on agent identities in the portal or via
  Microsoft Graph (they are only observable in the runtime token), the failure is
  invisible until a token request fails.

  This script merges entries into requiredResourceAccess, so reruns are safe and
  existing declarations are preserved.

.PARAMETER BlueprintAppId
  AppId (client ID) of the agent identity blueprint application.

.PARAMETER ResourceAppId
  AppId of the resource whose permissions are being declared.

.PARAMETER Scopes
  One or more delegated permission scope names (e.g. "user_impersonation").
  Names that the resource app does not expose are skipped with a warning rather
  than failing provisioning.

.EXAMPLE
  ./add-blueprint-required-resource-access.ps1 `
      -BlueprintAppId "<blueprint-app-id>" `
      -ResourceAppId "18a66f5f-dbdf-4c17-9dd7-1634712a9cbe" `
      -Scopes "user_impersonation"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$BlueprintAppId,

    [Parameter(Mandatory = $true)]
    [string]$ResourceAppId,

    [Parameter(Mandatory = $true)]
    [string[]]$Scopes
)

$ErrorActionPreference = "Stop"

$graphToken = az account get-access-token --resource "https://graph.microsoft.com" --query accessToken -o tsv
if ([string]::IsNullOrWhiteSpace($graphToken)) {
    throw "Failed to acquire a Microsoft Graph token for the signed-in principal."
}
$headers = @{
    "Content-Type"  = "application/json"
    "Accept"        = "application/json"
    "Authorization" = "Bearer $graphToken"
}

# The resource service principal carries the scope definitions. It may not exist
# yet for preview resources, in which case there is nothing to declare.
$resourceSpJson = az ad sp show --id $ResourceAppId -o json 2>$null
if ([string]::IsNullOrWhiteSpace($resourceSpJson)) {
    Write-Warning "No service principal for resource app $ResourceAppId; skipping requiredResourceAccess declaration."
    return
}
$resourceSp = $resourceSpJson | ConvertFrom-Json

$resourceAccess = @()
foreach ($scope in $Scopes) {
    $match = $resourceSp.oauth2PermissionScopes | Where-Object { $_.value -eq $scope }
    if ($null -eq $match) {
        $available = ($resourceSp.oauth2PermissionScopes | ForEach-Object { $_.value }) -join ', '
        Write-Warning "Resource '$($resourceSp.displayName)' does not expose scope '$scope'; skipping. Available: $available"
        continue
    }
    $resourceAccess += @{ id = $match.id; type = "Scope" }
}

if ($resourceAccess.Count -eq 0) {
    Write-Warning "No resolvable scopes for $($resourceSp.displayName); nothing to declare."
    return
}

$appUri = "https://graph.microsoft.com/v1.0/applications(appId='$BlueprintAppId')"
$app = Invoke-RestMethod -Uri $appUri -Method Get -Headers $headers
$existing = @($app.requiredResourceAccess)

# Rebuild as plain hashtables so ConvertTo-Json emits the shape Graph expects.
$merged = @()
$found = $false
foreach ($entry in $existing) {
    $access = @($entry.resourceAccess | ForEach-Object { @{ id = $_.id; type = $_.type } })
    if ($entry.resourceAppId -eq $ResourceAppId) {
        $found = $true
        foreach ($wanted in $resourceAccess) {
            if (-not ($access | Where-Object { $_.id -eq $wanted.id })) {
                $access += $wanted
            }
        }
    }
    $merged += @{ resourceAppId = $entry.resourceAppId; resourceAccess = $access }
}
if (-not $found) {
    $merged += @{ resourceAppId = $ResourceAppId; resourceAccess = $resourceAccess }
}

$body = @{ requiredResourceAccess = $merged } | ConvertTo-Json -Depth 8
Invoke-RestMethod -Uri $appUri -Method Patch -Headers $headers -Body $body | Out-Null

# Read back: a silent PATCH that did not persist is the failure mode this whole
# script exists to prevent, so never report success without confirming.
$after = Invoke-RestMethod -Uri $appUri -Method Get -Headers $headers
$declared = @($after.requiredResourceAccess | Where-Object { $_.resourceAppId -eq $ResourceAppId })
if ($declared.Count -eq 0) {
    throw "requiredResourceAccess for $ResourceAppId did not persist on blueprint $BlueprintAppId."
}

$declaredIds = @($declared[0].resourceAccess | ForEach-Object { $_.id })
$missing = @($resourceAccess | Where-Object { $declaredIds -notcontains $_.id })
if ($missing.Count -gt 0) {
    throw "requiredResourceAccess for $($resourceSp.displayName) is missing $($missing.Count) declared permission(s) after PATCH."
}

Write-Host "Declared requiredResourceAccess: $($resourceSp.displayName) ($ResourceAppId) -> $($declaredIds.Count) permission(s)."
