  $ErrorActionPreference = "Stop"

  function Grant-AzureRole {
      param(
          [Parameter(Mandatory = $true)][string]$Assignee,
          [Parameter(Mandatory = $true)][string]$Role,
          [Parameter(Mandatory = $true)][string]$Scope,
          [Parameter(Mandatory = $true)][string]$Description
      )

      Write-Host "Granting $Description to $Assignee on scope $Scope"
      $roleAssignmentOutput = az role assignment create `
          --assignee $Assignee `
          --role $Role `
          --scope $Scope 2>&1 | Out-String

      if ($LASTEXITCODE -eq 0) {
          Write-Host "$Description role assignment created."
      } elseif ($roleAssignmentOutput -match "RoleAssignmentExists") {
          Write-Host "$Description role assignment already exists, skipping."
      } else {
          throw "Failed to create $Description role assignment: $roleAssignmentOutput"
      }
  }

  $AzureAIProjectEndpoint = $env:AZURE_AI_PROJECT_ENDPOINT
  $AgentName = $env:AGENT_NAME
  $AzureContainerRegistryEndpoint = $env:AZURE_CONTAINER_REGISTRY_ENDPOINT

  $environmentVariables = @{}
  # The blueprint is no longer pre-created in infra, so its client id does not exist at image
  # build time any more. It is injected here instead, from the value the previous provision
  # persisted. Environment variables override appsettings.json, which is how the platform
  # supplies config. See the note in post-provision.ps1 about the first provision.
  if (-not [string]::IsNullOrWhiteSpace($env:AGENT_IDENTITY_BLUEPRINT_ID)) {
      $environmentVariables."Connections__ServiceConnection__Settings__ClientId" = $env:AGENT_IDENTITY_BLUEPRINT_ID
  }
  if (-not [string]::IsNullOrWhiteSpace($env:DIRECT_MESSAGE_ALLOWLIST_TABLE_SERVICE_URI)) {
      $environmentVariables.DirectMessageAllowListTableServiceUri = $env:DIRECT_MESSAGE_ALLOWLIST_TABLE_SERVICE_URI
  }
  if (-not [string]::IsNullOrWhiteSpace($env:DIRECT_MESSAGE_ALLOWLIST_TABLE_NAME)) {
      $environmentVariables.DirectMessageAllowListTableName = $env:DIRECT_MESSAGE_ALLOWLIST_TABLE_NAME
  }
  # Agent-to-agent delegation target. Only lives in the agent version's environment variables
  # (there is no build arg for it), so it must be re-supplied on every new version or the next
  # version silently loses Source of Truth delegation.
  if (-not [string]::IsNullOrWhiteSpace($env:SOURCE_OF_TRUTH_AGENT_ID)) {
      $environmentVariables.SourceOfTruthAgentId = $env:SOURCE_OF_TRUTH_AGENT_ID
  }
  if (-not [string]::IsNullOrWhiteSpace($env:SOURCE_OF_TRUTH_AGENT_NAME)) {
      $environmentVariables.SourceOfTruthAgentName = $env:SOURCE_OF_TRUTH_AGENT_NAME
  }
  # Toolbox version pin. The unpinned toolbox endpoint serves whatever the toolbox's default
  # version is, so publishing a toolbox version can break every running instance. Overriding it
  # here changes the pin without rebuilding the image (appsettings.json holds the baked default).
  if (-not [string]::IsNullOrWhiteSpace($env:TOOLBOX_VERSION)) {
      $environmentVariables.ToolboxVersion = $env:TOOLBOX_VERSION
  }

# Routine tools disable themselves without these two: RoutineToolHandler.IsEnabled requires a
# project endpoint and an agent name, and appsettings ships them empty so the env var is what
# actually supplies them. Without this the scheduling tools vanish with no error at all.
# These are set unconditionally. They previously sat inside the TOOLBOX_VERSION block above,
# which meant that leaving the toolbox pin unset silently disabled the scheduling tools too.
$environmentVariables.FoundryProjectEndpoint = $env:AZURE_AI_PROJECT_ENDPOINT
$environmentVariables.FoundryAgentName = $env:AGENT_NAME

  # Endpoint shape is built once and reused by the PATCH below, so the two cannot drift.
  $agentEndpoint = @{
      protocols = @("activity")
      protocol_configuration = @{
          activity = @{
              enable_m365_public_endpoint = $true
          }
      }
  }

  $agentUrl = "$($AzureAIProjectEndpoint)/agents/$($AgentName)/versions?api-version=2025-11-15-preview"

  $agentCreationBody = @{
      definition = @{
          kind = "hosted"
          image = "$($AzureContainerRegistryEndpoint)/agentic-colleague-agent1:latest"
          cpu = "2"
          memory = "4Gi"
          environment_variables = $environmentVariables
          container_protocol_versions = @(
              @{
                  protocol = "activity_protocol"
                  version  = "v1"
              }
          )
      }
      metadata = @{
        enableVnextExperience = "true"
      }
      description = "Foundry autopilot."
      agent_endpoint = $agentEndpoint
      digital_worker_type = "m365"
  }

  $jsonBody = $agentCreationBody | ConvertTo-Json -Depth 5

  Write-Host "Getting access token for https://ai.azure.com ..."

  $aiAzureToken = az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv


  Write-Host "Token length: $($aiAzureToken.Length)"

  $headers = @{
      "Content-Type"  = "application/json"
      "Accept"        = "application/json"
      "Authorization" = "Bearer $aiAzureToken"
      "Foundry-Features" = "HostedAgents=V1Preview,AgentEndpoints=V1Preview,DigitalWorker=V1Preview"
  }

  Write-Host "Creating agent version at: $agentUrl"
  Write-Host "JSON Body:"
  Write-Host $jsonBody

  $response = Invoke-RestMethod -Uri $agentUrl `
      -Method Post `
      -Headers $headers `
      -Body $jsonBody `
      -ErrorAction Stop

  Write-Host ""
  Write-Host "Response:"
  $response | ConvertTo-Json -Depth 100 | Write-Host

  # Output the agent version
  $agentVersion = $response.version
  $agentGuid = $response.agent_guid
  $agentDefaultInstanceClientId = $response.instance_identity.client_id
  $blueprintClientId = $response.blueprint.client_id
  Write-Host "Agent GUID: $agentGuid"
  Write-Host "Agent Version: $agentVersion"
  Write-Host "Agent default instance client id: $agentDefaultInstanceClientId"
  Write-Host "Blueprint client id: $blueprintClientId"

  # Poll for agent version provisioning status
  $maxRetries = 30
  $delaySeconds = 10
  $provisioningStatus = $response.status
  if (-not $provisioningStatus) { $provisioningStatus = "Unknown" }

  Write-Host "Initial provisioning status: $provisioningStatus"

  $pollUrl = "$($AzureAIProjectEndpoint)/agents/$($AgentName)/versions/$($agentVersion)?api-version=2025-11-15-preview"

  if ($provisioningStatus -ne "active" -and $provisioningStatus -ne "failed") {
      for ($i = 1; $i -lt $maxRetries; $i++) {
          Write-Host "Waiting ${delaySeconds}s before poll $($i + 1)/${maxRetries}..."
          Start-Sleep -Seconds $delaySeconds

          try {
              $pollResponse = Invoke-RestMethod -Uri $pollUrl `
                  -Method Get `
                  -Headers $headers `
                  -ErrorAction Stop

              $provisioningStatus = $pollResponse.status
              if (-not $provisioningStatus) { $provisioningStatus = "Unknown" }

              # Identity client ids are not necessarily populated on the create response;
              # they appear once provisioning completes. Refresh them on every poll or the
              # role assignment below is made against an empty assignee.
              if ($pollResponse.instance_identity.client_id) {
                  $agentDefaultInstanceClientId = $pollResponse.instance_identity.client_id
              }
              if ($pollResponse.blueprint.client_id) {
                  $blueprintClientId = $pollResponse.blueprint.client_id
              }
          } catch {
              Write-Host "Poll failed: $($_.Exception.Message)"
          }

          Write-Host "Provisioning status: $provisioningStatus"

          if ($provisioningStatus -eq "active" -or $provisioningStatus -eq "failed") {
              break
          }
      }
  }

  Write-Host "Agent version provisioned: $provisioningStatus"

  if ($provisioningStatus -ne "active") {
      throw "Agent version provisioning status is '$provisioningStatus', expected 'active'."
  }

  # Grant Cognitive Services User role on the foundry account to the agent's default instance identity.
  # The Foundry runtime creates this identity when the agent version is provisioned and injects its client id
  # into the running container as FOUNDRY_AGENT_DEFAULT_INSTANCE_CLIENT_ID. Our Responses API code uses that
  # identity (per Adi's sync from foundry-ai-teammate), so it must have the role on the Cognitive Services account.
  $subscriptionId = if ($env:SUBSCRIPTION_ID) { $env:SUBSCRIPTION_ID } else { $env:AZURE_SUBSCRIPTION_ID }
  $resourceGroup = if ($env:RESOURCE_GROUP) { $env:RESOURCE_GROUP } else { $env:AZURE_RESOURCE_GROUP }
  $accountName = $env:ACCOUNT_NAME
  $accountScope = "/subscriptions/$subscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.CognitiveServices/accounts/$accountName"
  $cognitiveServicesUserRoleId = "a97b65f3-24c7-4388-baec-2e87135dc908"

  Grant-AzureRole `
      -Assignee $agentDefaultInstanceClientId `
      -Role $cognitiveServicesUserRoleId `
      -Scope $accountScope `
      -Description "Cognitive Services User"

  if (-not [string]::IsNullOrWhiteSpace($env:DIRECT_MESSAGE_ALLOWLIST_STORAGE_ACCOUNT_RESOURCE_ID)) {
      Grant-AzureRole `
          -Assignee $agentDefaultInstanceClientId `
          -Role "Storage Table Data Contributor" `
          -Scope $env:DIRECT_MESSAGE_ALLOWLIST_STORAGE_ACCOUNT_RESOURCE_ID `
          -Description "Agent Storage Table Data Contributor (DM Allowlist)"
  }

  if (-not [string]::IsNullOrWhiteSpace($env:WORK_ITEMS_STORAGE_ACCOUNT_RESOURCE_ID)) {
      Grant-AzureRole `
          -Assignee $agentDefaultInstanceClientId `
          -Role "Storage Table Data Contributor" `
          -Scope $env:WORK_ITEMS_STORAGE_ACCOUNT_RESOURCE_ID `
          -Description "Agent Storage Table Data Contributor (Work Items)"
  }

  # Patch agent endpoint with activity protocol
  #
  # The authorization scheme is configurable because it is NOT a safe constant: this PATCH
  # overwrites whatever is on the endpoint, so hardcoding one value here means any change made
  # out-of-band is silently reverted on the next provision. BotServiceRbac is the default now
  # that there is no Bot Service resource; override with AGENT_ENDPOINT_AUTH_SCHEME.
  $authScheme = $env:AGENT_ENDPOINT_AUTH_SCHEME
  if ([string]::IsNullOrWhiteSpace($authScheme)) { $authScheme = "BotServiceRbac" }

  $patchUrl = "$($AzureAIProjectEndpoint)/agents/$($AgentName)?api-version=2025-11-15-preview"
  # Reuses the same endpoint object the version was created with, so the protocol
  # configuration cannot drift between create and patch.
  $agentEndpoint["authorization_schemes"] = @(
      @{ "type" = $authScheme }
  )
  $patchBody = @{
      agent_endpoint = $agentEndpoint
  } | ConvertTo-Json -Depth 5

  Write-Host "Patching agent endpoint at: $patchUrl (authorization scheme: $authScheme)"
  Write-Host "Patch Body:"
  Write-Host $patchBody

  $patchResponse = Invoke-RestMethod -Uri $patchUrl `
      -Method Patch `
      -Headers $headers `
      -Body $patchBody `
      -ErrorAction Stop
  
  Write-Host ""
  Write-Host "Patch Response:"
  $patchResponse | ConvertTo-Json -Depth 100 | Write-Host

  # Return both ids for downstream scripts. The blueprint client id in particular is only
  # knowable from here now: nothing creates the blueprint before this point.
  return [pscustomobject]@{
      AgentGuid         = $agentGuid
      BlueprintClientId = $blueprintClientId
  }

