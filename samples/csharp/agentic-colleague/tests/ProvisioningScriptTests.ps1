$ErrorActionPreference = 'Stop'
$sampleRoot = Split-Path $PSScriptRoot -Parent
$buildScript = Join-Path $sampleRoot 'scripts\build-docker-image-acr.ps1'
$creationScript = Join-Path $sampleRoot 'scripts\agent-creation-script.ps1'

foreach ($path in @($buildScript, $creationScript, (Join-Path $sampleRoot 'scripts\preflight.ps1'))) {
    $tokens = $null
    $errors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    if ($errors.Count -ne 0) { throw "PowerShell syntax errors in ${path}: $errors" }
}

# Exercise the real entrypoint, but make a failing preflight and any later operation deterministic.
function pwsh { $global:LASTEXITCODE = 17 }
function az { throw 'Unexpected cloud call after a failed preflight.' }
function dotnet { throw 'Unexpected publish after a failed preflight.' }
function Remove-Item { throw 'Unexpected cleanup after a failed preflight.' }
function Set-Location { throw 'Unexpected directory change after a failed preflight.' }
try {
    & $buildScript
    throw 'The image entrypoint did not reject a failed preflight.'
}
catch {
    if ($_.Exception.Message -ne 'Preflight failed; no image build was started.') { throw }
}

$tokens = $null
$errors = $null
$creationAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $creationScript, [ref]$tokens, [ref]$errors)
$settingBlock = $creationAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.IfStatementAst] -and
        $node.Extent.Text.Contains('$env:APPLICATIONINSIGHTS_CONNECTION_STRING')
}, $true)
if ($null -eq $settingBlock) { throw 'No runtime App Insights configuration block found.' }
if ($null -ne $settingBlock.Find({
    param($node)
    $node -is [System.Management.Automation.Language.CommandAst]
}, $true)) { throw 'The settings fixture must not execute external commands.' }

$oldConnectionString = $env:APPLICATIONINSIGHTS_CONNECTION_STRING
try {
    foreach ($configured in @($true, $false)) {
        $env:APPLICATIONINSIGHTS_CONNECTION_STRING = if ($configured) { 'test-instrumentation-setting' } else { '' }
        $environmentVariables = @{}
        & ([scriptblock]::Create($settingBlock.Extent.Text))
        if ($environmentVariables.ContainsKey('ApplicationInsights__ConnectionString') -ne $configured) {
            throw 'Runtime monitoring configuration does not follow the provisioned setting.'
        }
        if ($environmentVariables.ContainsKey('APPLICATIONINSIGHTS_CONNECTION_STRING')) {
            throw 'The hosted API reserves APPLICATIONINSIGHTS_CONNECTION_STRING for platform use.'
        }
        if ($configured -and $environmentVariables.ApplicationInsights__ConnectionString -ne 'test-instrumentation-setting') {
            throw 'Runtime monitoring configuration was not propagated exactly.'
        }
    }
}
finally {
    $env:APPLICATIONINSIGHTS_CONNECTION_STRING = $oldConnectionString
}

Write-Host 'PASS: script syntax, fail-closed image entrypoint, and optional runtime monitoring configuration.'
exit 0
