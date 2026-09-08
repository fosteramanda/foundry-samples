$ErrorActionPreference = "Stop"

function Get-RequiredAzdValue {
    param([Parameter(Mandatory)][string]$Name)

    $value = azd env get-value $Name 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($value)) {
        throw "Missing $Name. Run 'azd provision' and 'azd deploy' before this script."
    }

    return $value.Trim()
}

function Read-RequiredValue {
    param([Parameter(Mandatory)][string]$Prompt)

    do {
        $value = (Read-Host $Prompt).Trim()
    } while ([string]::IsNullOrWhiteSpace($value))

    return $value
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

$packageMetadata = @{
    PublishAsAutopilot = $true
    PublishScope = "Tenant"
    AgentDisplayName = Read-RequiredValue -Prompt "Agent display name"
    AppVersion = "1.0.0"
    ShortDescription = Read-RequiredValue -Prompt "Short description"
    FullDescription = Read-RequiredValue -Prompt "Full description"
    DeveloperName = Read-RequiredValue -Prompt "Developer or organization name"
    DeveloperWebsiteUrl = Read-RequiredValue -Prompt "Developer support website URL"
    PrivacyUrl = Read-RequiredValue -Prompt "Privacy statement URL"
    TermsOfUseUrl = Read-RequiredValue -Prompt "Terms of use URL"
    CanRespondWithoutMention = $true
} | ConvertTo-Json

$packageFile = Join-Path -Path (Get-Location) -ChildPath "$agentName-1.0.0.zip"
$headers = @{
    Authorization = "Bearer $($accessToken.Trim())"
    Accept = "application/zip"
}

$escapedAgentName = [Uri]::EscapeDataString($agentName)
Invoke-WebRequest `
    -Method Post `
    -Uri "$projectEndpoint/agents/$escapedAgentName/microsoft365/zip`?api-version=v1" `
    -Headers $headers `
    -ContentType "application/json" `
    -Body $packageMetadata `
    -OutFile $packageFile

Write-Host "Downloaded $packageFile"
