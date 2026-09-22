<#
.SYNOPSIS
  Build, version, repin and verify a deploy of the autopilot router agent.

.DESCRIPTION
  One command for what was previously three hand-run steps plus a manual check.

  DO NOT copy the sibling wrapper at %TEMP%\deploy_wsm.ps1 for this agent. That one targets
  foundry-workstream-manager-autopilot-agent, where the account, project and agent are all the
  SAME string ("workstreammanagerado"). Here they are three different values:

      account  autopilotrouteracct
      project  autopilotrouterproj
      agent    autopilotrouter

  A find-and-replace on that script therefore looks correct and silently builds a wrong URL, so
  this reads all three from the azd .env instead of hardcoding any of them.

  The final check is the one that matters. A traffic repin does NOT restart a running container:
  it keeps serving the OLD image until it idles out (roughly 11-15 minutes). Testing eagerly keeps
  it warm, which keeps the fix from loading. STATE.md records three versions that were "verified"
  by checking the traffic pin and went live nowhere. The pin is not evidence the code is running;
  a container start AFTER the version was created is.

.PARAMETER SkipBuild
  Reuse the image already in ACR and only create/repin a version.

.PARAMETER WhatIfRollback
  Print the rollback command for the currently-serving version and exit without changing anything.
#>
[CmdletBinding()]
param(
    [switch] $SkipBuild,
    [switch] $WhatIfRollback,
    [switch] $KeepSessions
)

$ErrorActionPreference = 'Stop'

# Pin to the NotARealCo-only CLI config so this can never read or mutate the machine default.
$env:AZURE_CONFIG_DIR = 'C:\Users\fosteramanda\.azure-notarealco-session'

$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root '.azure\autopilotrouter\.env'

if (-not (Test-Path $envFile)) {
    throw "azd environment not found at $envFile"
}

Get-Content $envFile | ForEach-Object {
    if ($_ -match '^\s*([A-Za-z0-9_]+)\s*=\s*(.*)$') {
        Set-Item -Path "Env:$($Matches[1])" -Value ($Matches[2].Trim().Trim('"'))
    }
}

$account = $env:ACCOUNT_NAME
$project = $env:PROJECT_NAME
$agent = $env:AGENT_NAME

foreach ($pair in @(@{n = 'ACCOUNT_NAME'; v = $account }, @{n = 'PROJECT_NAME'; v = $project }, @{n = 'AGENT_NAME'; v = $agent })) {
    if ([string]::IsNullOrWhiteSpace($pair.v)) { throw "$($pair.n) is not set in $envFile" }
}

$agentUri = "https://$account.services.ai.azure.com/api/projects/$project/agents/$agent`?api-version=2025-11-15-preview"

function Get-AiToken {
    az account get-access-token --resource 'https://ai.azure.com' --query accessToken -o tsv
}

# Record what is serving BEFORE touching anything: this is the rollback target.
$token = Get-AiToken
$current = Invoke-RestMethod -Method Get -Uri $agentUri -Headers @{ Authorization = "Bearer $token" }
$currentVersion = ($current.agent_endpoint.version_selector.version_selection_rules |
    Where-Object { $_.traffic_percentage -eq 100 } | Select-Object -First 1).agent_version

Write-Host "Currently serving: v$currentVersion"

if ($WhatIfRollback) {
    Write-Host "`nTo roll back to the current version later, re-run with the body below:"
    Write-Host "  PATCH $agentUri"
    Write-Host "  {`"agent_endpoint`":{`"version_selector`":{`"version_selection_rules`":[{`"type`":`"FixedRatio`",`"agent_version`":`"$currentVersion`",`"traffic_percentage`":100}]}}}"
    return
}

if (-not $SkipBuild) {
    Write-Host "`n=== ACR build ==="
    & (Join-Path $PSScriptRoot 'build-docker-image-acr.ps1') 2>&1 | Select-Object -Last 4
    if ($LASTEXITCODE -ne 0) { throw "ACR build failed." }
}

Write-Host "`n=== new agent version ==="
$createOutput = & (Join-Path $PSScriptRoot 'agent-creation-script.ps1') *>&1
$createOutput | Select-String -Pattern 'Agent Version:|provisioned' | ForEach-Object { "  $($_.Line.Trim())" }

$match = $createOutput | Select-String -Pattern 'Agent Version:\s*(\d+)' | Select-Object -Last 1
if (-not $match) { throw "Could not parse the new agent version; traffic NOT repinned." }
$newVersion = $match.Matches.Groups[1].Value

# agent-creation-script.ps1 creates the version but leaves traffic on the PREVIOUS one, so without
# this the new image is live nowhere. Repinning is the difference between built and deployed.
Write-Host "`n=== repin traffic to v$newVersion ==="
$token = Get-AiToken
$body = @{
    agent_endpoint = @{
        version_selector = @{
            version_selection_rules = @(
                @{ type = 'FixedRatio'; agent_version = $newVersion; traffic_percentage = 100 }
            )
        }
    }
} | ConvertTo-Json -Depth 10

$response = Invoke-RestMethod -Method Patch -Uri $agentUri -Headers @{ Authorization = "Bearer $token" } `
    -ContentType 'application/json' -Body $body

$rules = $response.agent_endpoint.version_selector.version_selection_rules
$rules | ForEach-Object { "  traffic -> v$($_.agent_version)  $($_.traffic_percentage)%" }

if (-not ($rules | Where-Object { $_.agent_version -eq $newVersion -and $_.traffic_percentage -eq 100 })) {
    throw "Repin did not take effect: v$newVersion is not serving 100% of traffic."
}

# ---------------------------------------------------------------------------
# Stop live sessions, or the repin changes nothing you can observe.
#
# A SESSION IS PINNED TO THE AGENT VERSION IT WAS CREATED ON. Repinning traffic only affects NEW
# sessions; an existing conversation keeps running the old version indefinitely. Measured: with
# traffic on v12, the Foundry portal's Session view still showed Active sessions on v11 and the
# Trace view for v12 was empty, which reads exactly like the deploy having failed.
#
# This is a stronger guarantee than waiting out the container idle timeout, because continuing to
# chat in an existing thread keeps that session alive and on the old version no matter how long
# you wait.
# ---------------------------------------------------------------------------
if (-not $KeepSessions) {
    Write-Host "`n=== stopping live sessions so the next turn lands on v$newVersion ==="

    # The azd agent extension resolves the project from this, not from the azd env.
    if ([string]::IsNullOrWhiteSpace($env:FOUNDRY_PROJECT_ENDPOINT)) {
        $env:FOUNDRY_PROJECT_ENDPOINT = "https://$account.services.ai.azure.com/api/projects/$project"
    }

    $stopScript = 'C:\Users\fosteramanda\Code-Samples\foundry-samples\samples\python\autopilot-agents\scripts\stop-agent-sessions.ps1'

    if (Test-Path $stopScript) {
        & $stopScript -AgentName $agent 2>&1 |
            Where-Object { $_ -notmatch '^Skipping session' } |
            ForEach-Object { "  $_" }
    }
    else {
        Write-Warning "stop-agent-sessions.ps1 not found at $stopScript."
        Write-Warning "Existing sessions stay on their original version, so start a NEW conversation to reach v$newVersion."
    }
}

# ---------------------------------------------------------------------------
# Is a stale container still serving the old image?
#
# Queried over REST rather than `az monitor app-insights query`, which needs the log-analytics
# CLI extension. That extension was installed elevated under this config dir and broke every az
# EXTENSION command with "Access is denied" until it was removed; REST has no such dependency.
# ---------------------------------------------------------------------------
Write-Host "`n=== is a stale container still serving? ==="

$appInsightsRid = $env:APPLICATIONINSIGHTS_RESOURCE_ID
if ([string]::IsNullOrWhiteSpace($appInsightsRid)) {
    Write-Host "  (APPLICATIONINSIGHTS_RESOURCE_ID not set; cannot check)"
    Write-Host "`nDeployed and serving: v$newVersion"
    return
}

$mgmtToken = az account get-access-token --resource 'https://management.azure.com' --query accessToken -o tsv
$component = Invoke-RestMethod -Uri "https://management.azure.com$appInsightsRid`?api-version=2020-02-02" `
    -Headers @{ Authorization = "Bearer $mgmtToken" }
$appId = $component.properties.AppId

$aiToken = az account get-access-token --resource 'https://api.applicationinsights.io' --query accessToken -o tsv

function Invoke-AiQuery([string] $Query) {
    $uri = "https://api.applicationinsights.io/v1/apps/$appId/query?query=$([uri]::EscapeDataString($Query))"
    (Invoke-RestMethod -Uri $uri -Headers @{ Authorization = "Bearer $aiToken" }).tables[0].rows
}

$recent = [int](Invoke-AiQuery 'traces | where timestamp > ago(15m) | count')[0][0]
$lastStart = Invoke-AiQuery "traces | where timestamp > ago(3h) | where message has 'Application starting' | top 1 by timestamp desc | project timestamp"

if ($lastStart) { Write-Host "  last container start: $($lastStart[0][0])" }

if ($recent -gt 0) {
    Write-Warning "The container has been active in the last 15 minutes."
    Write-Warning "It is still running the PREVIOUS image and will not pick up v$newVersion"
    Write-Warning "until it has been idle for roughly 11-15 minutes."
    Write-Warning "Do not test immediately: wait for the idle timeout, THEN send the first message."
    Write-Warning "More than one replica can be alive on different versions, so one good reply does"
    Write-Warning "not mean the fix is live everywhere. Check every 'Application starting' in the"
    Write-Warning "window is AFTER this version was created."
}
else {
    Write-Host "  Container is cold (no traces in 15 min). The next message loads v$newVersion." -ForegroundColor Green
}

Write-Host "`nDeployed and serving: v$newVersion   (rollback target: v$currentVersion)"
