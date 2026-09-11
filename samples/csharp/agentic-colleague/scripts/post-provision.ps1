#!/usr/bin/env pwsh
Write-Host "Starting post-provision script..."

# AZURE_LOCATION is a default azd environment variable
Write-Host "Resources were deployed to: location $env:AZURE_LOCATION subscriptionId $env:SUBSCRIPTION_ID agentName $env:AGENT_NAME"

# Write-Host "===============Building and pushing Docker image==============="
& "$PSScriptRoot/build-docker-image-acr.ps1"

# Write-Host "===============Building and pushing Docker image==============="
# & "$PSScriptRoot/build-docker-image-acr-agenticai-sample.ps1"


Write-Host "===============Creating Agent Version==============="
$agentInfo = & "$PSScriptRoot/agent-creation-script.ps1"
$agentGuid = $agentInfo.AgentGuid
$blueprintClientId = $agentInfo.BlueprintClientId
Write-Host "Agent GUID: $agentGuid, Blueprint client id: $blueprintClientId"

# -----------------------------------------------------------------------------
# The blueprint is created by the platform as part of creating the agent version, so
# this is the first point at which its client id exists. Nothing before this line can
# know it. Every blueprint-dependent script below reads AGENT_IDENTITY_BLUEPRINT_ID
# from the environment, so it is set here for this process and persisted into the azd
# environment for later runs.
#
# KNOWN FIRST-PROVISION BEHAVIOUR: the agent version is created before this id is
# known, so on a brand new environment that version carries no
# Connections__ServiceConnection__Settings__ClientId. The value is persisted here, so
# the next provision injects it into both the image build and the agent version. If the
# container cannot authenticate on a first deploy, re-run the provision before
# investigating anything else.
# -----------------------------------------------------------------------------
if (-not [string]::IsNullOrWhiteSpace($blueprintClientId)) {
    $env:AGENT_IDENTITY_BLUEPRINT_ID = $blueprintClientId
    & azd env set AGENT_IDENTITY_BLUEPRINT_ID $blueprintClientId | Out-Null
    Write-Host "Persisted AGENT_IDENTITY_BLUEPRINT_ID to the azd environment."
}
else {
    Write-Warning "Agent creation returned no blueprint client id. The blueprint-owner, OAuth2 grant and publish steps below will not work."
}

# -----------------------------------------------------------------------------
# One-time digital worker setup: adding the current user as blueprint owner,
# creating the blueprint SP OAuth2 grants (+ inheritable scopes), and publishing
# the digital worker only need to happen on the FIRST successful provision. They
# are idempotent, but they make unnecessary API calls on every code-change
# re-provision, so we persist a marker in the azd environment and skip them once
# completed.
#
# Order matters: become blueprint owner -> declare OAuth2 grants + inheritable
# scopes -> publish. Publishing last ensures the agent's scopes are declared
# before an admin approves the published agent (approval consents the scopes).
#
# A code-only change still rebuilds the image and creates a new agent version
# above (those run every time); the published digital worker references the agent
# GUID, not a specific version, so new versions are served without re-publishing.
#
# To force these steps to run again (e.g. after changing publish metadata or
# blueprint scopes): azd env set DIGITAL_WORKER_SETUP_DONE ""
# -----------------------------------------------------------------------------
$digitalWorkerSetupDone = & azd env get-value DIGITAL_WORKER_SETUP_DONE 2>$null
if ($LASTEXITCODE -ne 0 -or $null -eq $digitalWorkerSetupDone) { $digitalWorkerSetupDone = "" }

if ($digitalWorkerSetupDone.Trim() -eq "true") {
    Write-Host "===============Digital worker one-time setup already completed; skipping publish, OAuth2 grants, and blueprint owner==============="
}
else {
    Write-Host "===============Adding current user as blueprint owner==============="
    try {
        & "$PSScriptRoot/add-current-user-as-blueprint-owner.ps1"
    }
    catch {
        Write-Warning "Failed to add current user as blueprint owner: $($_.Exception.Message)"
        Write-Warning "Continuing without blocking post-provision."
    }

    # oAuth2 grants for blueprint SP (also declares inheritable scopes). Must run
    # before publish so the agent's scopes are declared before an admin approves
    # the published agent (approval is where the declared scopes get consented).
    Write-Host "===============OAuth2 grants for blueprint SP==============="
    & "$PSScriptRoot/create-blueprintsp-oauth2-grants.ps1"

    Write-Host "===============Publishing autopilot==============="
    # Publish declares the maximum audience, and the body sends publishScope = "Tenant".
    # Canon item 5 makes that a hard ceiling: a tenant admin can narrow it later but can
    # never widen it. SKIP_PUBLISH exists so an environment can be provisioned and an agent
    # version created without committing to that ceiling. It defaults to OFF, so normal
    # runs publish as before; set it only when the audience decision is still open.
    $skipPublish = & azd env get-value SKIP_PUBLISH 2>$null
    if ($LASTEXITCODE -ne 0 -or $null -eq $skipPublish) { $skipPublish = "" }

    if ($skipPublish.Trim() -eq "true") {
        Write-Host "SKIP_PUBLISH=true, so the autopilot was NOT published and no audience ceiling was declared."
        Write-Host "To publish later: azd env set SKIP_PUBLISH `"`" ; azd env set DIGITAL_WORKER_SETUP_DONE `"`" ; azd provision"
    }
    else {
        & "$PSScriptRoot/publish-digital-worker.ps1" -BlueprintClientId $blueprintClientId
    }

    # Mark one-time setup complete so subsequent re-provisions skip these steps.
    & azd env set DIGITAL_WORKER_SETUP_DONE true | Out-Null
}


Write-Host ""
Write-Host "Post-provision script finished."
