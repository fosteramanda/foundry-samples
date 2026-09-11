<#
.SYNOPSIS
    Preflight gate. Blocks a deploy when an invariant this sample depends on is broken.

.DESCRIPTION
    Every assertion states WHAT IT DEFENDS, not just what it checks. That wording is the
    point: when an assertion fails you need to tell a stale check from a real regression in
    seconds, and you cannot do that from a bare "expected X, got Y".

    This gate EXITS NONZERO on failure. An earlier version of this pattern on the sibling
    autopilot printed its results and let the build continue; two checks went stale and
    nobody noticed. Do not "improve" it by making failures advisory.

    Structural assertions are made against the BUILT ASSEMBLY rather than the source,
    because reflection over the compiled DLL catches wiring that a source grep cannot.
    Assertions about tool definitions are made against source, because those definitions
    are JSON string literals that only exist as data inside method bodies.

.PARAMETER SkipBuild
    Reuse the existing binaries instead of rebuilding first. Faster, but it means the
    assembly you are asserting on may not match the source you are reading.

.EXAMPLE
    ./preflight.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$sampleRoot  = Split-Path $PSScriptRoot -Parent
$projectDir  = Join-Path $sampleRoot 'src/agentic_colleague_agent'
$projectName = 'AgenticColleagueAgent'
$helpersDir  = Join-Path $projectDir 'AgentLogic/ResponsesApi/Helpers'

$script:Failures = 0
$script:Checks   = 0

function Assert-That {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Defends,
        [Parameter(Mandatory)][bool]$Condition,
        [string]$Detail = ''
    )
    $script:Checks++
    if ($Condition) {
        Write-Host ("  PASS  {0}" -f $Name) -ForegroundColor Green
    }
    else {
        $script:Failures++
        Write-Host ("  FAIL  {0}" -f $Name) -ForegroundColor Red
        if ($Detail) { Write-Host ("        {0}" -f $Detail) -ForegroundColor Red }
        Write-Host ("        Defends: {0}" -f $Defends) -ForegroundColor Yellow
    }
}

function Remove-Comments {
    <#
        Assertions below test what the code does, not what the comments say about it.
        Without this, documenting a change ("we no longer send publishAsDigitalWorker")
        trips the very assertion that forbids it, and the honest fix looks like a
        regression.
    #>
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][ValidateSet('ps', 'bicep')][string]$Kind
    )
    if ($Kind -eq 'ps') {
        $Text = [regex]::Replace($Text, '(?s)<#.*?#>', '')
        $Text = [regex]::Replace($Text, '(?m)^\s*#.*$', '')
    }
    else {
        $Text = [regex]::Replace($Text, '(?s)/\*.*?\*/', '')
        $Text = [regex]::Replace($Text, '(?m)^\s*//.*$', '')
    }
    return $Text
}

Write-Host ''
Write-Host 'Preflight gate: agentic-colleague autopilot' -ForegroundColor Cyan
Write-Host ''

# ---------------------------------------------------------------------------
# Build first, so the assembly assertions describe the current source.
# ---------------------------------------------------------------------------
if (-not $SkipBuild) {
    Write-Host 'Building...' -ForegroundColor Cyan
    Push-Location $projectDir
    try {
        $buildOutput = & dotnet build "$projectName.sln" -v quiet --nologo 2>&1
        $buildOk = ($LASTEXITCODE -eq 0)
    }
    finally { Pop-Location }

    Assert-That -Name 'Project builds' -Condition $buildOk `
        -Detail ($buildOutput | Select-Object -Last 12 | Out-String) `
        -Defends 'Everything below asserts on the built assembly. If the build is broken those assertions are meaningless, so this has to fail first and loudest.'

    if (-not $buildOk) {
        Write-Host ''
        Write-Host 'Build failed; skipping the remaining assertions.' -ForegroundColor Red
        exit 1
    }
}

$dllPath = Join-Path $projectDir "bin/Debug/net9.0/$projectName.dll"

Assert-That -Name 'Built assembly is present' -Condition (Test-Path $dllPath) `
    -Detail "Expected: $dllPath" `
    -Defends 'The deploy copies this DLL into the container image. If it is missing the image ships stale or empty.'

if (-not (Test-Path $dllPath)) { Write-Host ''; exit 1 }

# ---------------------------------------------------------------------------
# Assembly identity, by reflection.
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host 'Assembly identity' -ForegroundColor Cyan

$asmName = [System.Reflection.AssemblyName]::GetAssemblyName($dllPath).Name
Assert-That -Name "Assembly is named $projectName" -Condition ($asmName -eq $projectName) `
    -Detail "Actual: $asmName" `
    -Defends 'The Dockerfile ENTRYPOINT names this assembly. If they drift the container starts and exits immediately, which reads as a platform fault rather than a rename mistake.'

$asm   = [System.Reflection.Assembly]::LoadFrom($dllPath)
$roots = @($asm.GetTypes() | Where-Object { $_.Namespace } | ForEach-Object { ($_.Namespace -split '\.')[0] } | Sort-Object -Unique)
Assert-That -Name 'Single namespace root AgenticColleague' -Condition (($roots.Count -eq 1) -and ($roots[0] -eq 'AgenticColleague')) `
    -Detail ("Actual roots: " + ($roots -join ', ')) `
    -Defends 'This sample began as a copy of another autopilot. A leftover namespace root is the signature of a half-finished rename, and it means the code still identifies as the agent it was cloned from.'

$siblingTypes = $asm.GetTypes() | Where-Object { $_.FullName -like '*WorkstreamManager*' }
Assert-That -Name 'No sibling types survive in the assembly' -Condition ($siblingTypes.Count -eq 0) `
    -Detail ("Found: " + (($siblingTypes | ForEach-Object { $_.FullName }) -join ', ')) `
    -Defends 'Same rename risk as above, but caught at the type level where a source grep over prose cannot distinguish a real symbol from a comment.'

# ---------------------------------------------------------------------------
# Dockerfile entrypoint must name the assembly that was actually built.
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host 'Container entrypoint' -ForegroundColor Cyan

$dockerfile     = Join-Path $projectDir 'foundry-infra/Dockerfile'
$entrypointLine = (Select-String -Path $dockerfile -Pattern '^\s*ENTRYPOINT' | Select-Object -First 1).Line
$entrypointDll  = if ($entrypointLine -match '"([A-Za-z0-9_.]+\.dll)"') { $Matches[1] } else { '' }

Assert-That -Name 'Dockerfile ENTRYPOINT matches the built assembly' -Condition ($entrypointDll -eq "$projectName.dll") `
    -Detail "ENTRYPOINT names '$entrypointDll', assembly is '$projectName.dll'" `
    -Defends 'A rename that misses the Dockerfile produces a container that exits on start with no application log at all, which is one of the slowest failures here to diagnose.'

# ---------------------------------------------------------------------------
# Tool wiring. Defends the trap that cost the sibling the most time twice over.
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host 'Tool wiring' -ForegroundColor Cyan

$handlerFiles  = Get-ChildItem (Join-Path $helpersDir '*ToolHandler.cs')
$declaredTools = @{}
$paramNames    = New-Object System.Collections.Generic.HashSet[string]

foreach ($file in $handlerFiles) {
    $text  = Get-Content $file.FullName -Raw
    $names = [regex]::Matches($text, '"name":\s*"([a-z][a-z0-9_]*)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    $declaredTools[$file.Name] = $names

    # Tool parameter names, so the prompt may mention them without being accused of
    # naming a tool that does not exist.
    foreach ($m in [regex]::Matches($text, '"([a-z][a-z0-9_]*)"\s*:\s*\{\s*"type"')) {
        [void]$paramNames.Add($m.Groups[1].Value)
    }

    # Every declared tool must also appear in that handler's dispatch, otherwise the
    # model is offered something no code will answer.
    $execIndex = $text.IndexOf('TryExecuteAsync')
    $execBody  = if ($execIndex -ge 0) { $text.Substring($execIndex) } else { '' }
    $undispatched = @($names | Where-Object { $execBody -notmatch [regex]::Escape("`"$_`"") })

    Assert-That -Name "$($file.Name): every declared tool is dispatched" -Condition ($undispatched.Count -eq 0) `
        -Detail ("Declared but not dispatched: " + ($undispatched -join ', ')) `
        -Defends 'A tool that is advertised to the model but has no dispatch arm returns "no handler recognised the name". The model then retries or invents an answer, and the turn looks like a model fault rather than missing wiring.'
}

$allDeclared = @($declaredTools.Values | ForEach-Object { $_ }) | Sort-Object -Unique

Assert-That -Name 'At least one local tool is declared' -Condition ($allDeclared.Count -gt 0) `
    -Detail "Declared tools found: $($allDeclared.Count)" `
    -Defends 'If the extraction regex stops matching, every tool assertion below silently passes against an empty set. This check is what stops this gate from going quietly stale.'

# The aggregator has to offer and dispatch every handler, or a whole handler goes dark.
$serviceFile = Join-Path $projectDir 'AgentLogic/ResponsesApi/ResponsesApiAgentLogicService.cs'
$serviceText = Get-Content $serviceFile -Raw
foreach ($handler in @('_workItemTools', '_routineTools', '_meetingRegistryTools')) {
    $offered    = $serviceText -match "$handler\.GetToolDefinitions\(\)"
    $dispatched = $serviceText -match "$handler\.TryExecuteAsync"
    Assert-That -Name "$handler is both offered and dispatched" -Condition ($offered -and $dispatched) `
        -Detail "offered=$offered dispatched=$dispatched" `
        -Defends 'A handler offered but not dispatched advertises tools nothing will run; dispatched but not offered is dead code the model never learns about. Either way the failure appears at runtime, in front of a user.'
}

# ---------------------------------------------------------------------------
# The prompt must not describe a tool the agent does not have.
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host 'Prompt honesty' -ForegroundColor Cyan

$instructionsFile = Join-Path $projectDir 'AgentLogic/AgentInstructions.cs'
$instructions     = Get-Content $instructionsFile -Raw

# Tools reached through an MCP server appear with a transport prefix (workiq___x, mcp_X).
# They come from the toolbox at runtime, so this gate cannot confirm they exist; it only
# records that the prompt referred to them deliberately rather than by accident.
$mcpSuffixes = New-Object System.Collections.Generic.HashSet[string]
foreach ($m in [regex]::Matches($instructions, '(?:workiq___|mcp_[A-Za-z0-9]+_{0,3})([a-z][a-z0-9_]*)')) {
    [void]$mcpSuffixes.Add($m.Groups[1].Value)
}

$toolShaped = [regex]::Matches($instructions, '\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+\b') | ForEach-Object { $_.Value } | Sort-Object -Unique
$phantom    = @($toolShaped | Where-Object {
    ($allDeclared -notcontains $_) -and (-not $paramNames.Contains($_)) -and (-not $mcpSuffixes.Contains($_))
})

Assert-That -Name 'Prompt names no tool the agent does not have' -Condition ($phantom.Count -eq 0) `
    -Detail ("Named in the prompt but not declared, not a tool parameter, and not an MCP tool: " + ($phantom -join ', ')) `
    -Defends 'This is the most expensive mistake on the sibling autopilot and it happened twice: a prompt section was ported without its handler, and the agent went hunting for a tool that did not exist. It costs nothing to catch here and a debugging session to catch at runtime.'

# ---------------------------------------------------------------------------
# Toolbox configuration.
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host 'Toolbox configuration' -ForegroundColor Cyan

$appSettingsPath = Join-Path $projectDir 'appsettings.json'
$appSettingsRaw  = Get-Content $appSettingsPath -Raw
# Strip // comments so this parses as JSON; the file is commented heavily on purpose.
$appSettingsJson = ($appSettingsRaw -replace '(?m)^\s*//.*$', '') | ConvertFrom-Json

$toolboxName    = $appSettingsJson.ToolboxName
$toolboxVersion = $appSettingsJson.ToolboxVersion

if (-not [string]::IsNullOrWhiteSpace($toolboxName)) {
    Assert-That -Name 'ToolboxVersion is pinned' -Condition (-not [string]::IsNullOrWhiteSpace($toolboxVersion)) `
        -Detail "ToolboxName='$toolboxName' ToolboxVersion='$toolboxVersion'" `
        -Defends 'An unpinned toolbox follows the toolbox default version, so somebody else publishing a new version silently changes what every running instance loads. A published version containing an unresolvable connection breaks every turn, including turns needing none of those tools.'

    $createToolbox    = Get-Content (Join-Path $PSScriptRoot 'create-toolbox.ps1') -Raw
    $scriptToolboxDef = if ($createToolbox -match '\$ToolboxName\s*=\s*"([^"]+)"') { $Matches[1] } else { '' }

    Assert-That -Name 'appsettings ToolboxName matches create-toolbox.ps1 default' -Condition ($scriptToolboxDef -eq $toolboxName) `
        -Detail "appsettings='$toolboxName' create-toolbox.ps1='$scriptToolboxDef'" `
        -Defends 'If these drift, the script creates one toolbox and the agent asks for another. The agent then starts cleanly with no tools attached and reports no error, which reads as a model problem.'
}

# ---------------------------------------------------------------------------
# No references to the sibling autopilot's live environment.
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host 'Environment isolation' -ForegroundColor Cyan

$siblingRefs = @(
    Get-ChildItem $sampleRoot -Recurse -File -Include '*.ps1', '*.json', '*.bicep', '*.cs', '*.md', '*.yaml' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.FullName -ne $PSCommandPath } |
        Select-String -Pattern 'foundryworkstreammang' -ErrorAction SilentlyContinue
)

Assert-That -Name 'No reference to the sibling autopilot Foundry account' -Condition ($siblingRefs.Count -eq 0) `
    -Detail (($siblingRefs | ForEach-Object { "$($_.Filename):$($_.LineNumber)" }) -join ', ') `
    -Defends 'The create-toolbox example in this sample originally hardcoded the sibling autopilot live Foundry account and project. Running it as written would have created a toolbox in a different agent environment.'

# ---------------------------------------------------------------------------
# Provisioning model. These lock in the shape agreed on 2026-09-11: no bot service,
# no blueprint created in infra, and the new publish schema.
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host 'Provisioning model' -ForegroundColor Cyan

$infraDir   = Join-Path $sampleRoot 'infra'
$mainBicep  = Remove-Comments -Kind bicep -Text (Get-Content (Join-Path $infraDir 'main.bicep') -Raw)
$creationPs = Remove-Comments -Kind ps -Text (Get-Content (Join-Path $PSScriptRoot 'agent-creation-script.ps1') -Raw)
$publishPs  = Remove-Comments -Kind ps -Text (Get-Content (Join-Path $PSScriptRoot 'publish-digital-worker.ps1') -Raw)

Assert-That -Name 'No bot service module in infra' -Condition (($mainBicep -notmatch 'botservice') -and (-not (Test-Path (Join-Path $infraDir 'modules/botservice.bicep')))) `
    -Defends 'The bot service was deliberately cut. The agent endpoint is authorized with the BotServiceRbac scheme instead. Re-adding one means the endpoint is being secured two different ways at once.'

Assert-That -Name 'No blueprint created in infra' -Condition (($mainBicep -notmatch 'maib-creation-script|maibName') -and (-not (Test-Path (Join-Path $infraDir 'modules/maib-creation-script.bicep')))) `
    -Defends 'The blueprint is created by the platform during agent creation, which is the only place its client id is returned. Creating one in infra produces a second, unused blueprint and an id that does not match the running agent.'

Assert-That -Name 'main.bicep does not output AGENT_IDENTITY_BLUEPRINT_ID' -Condition ($mainBicep -notmatch 'output AGENT_IDENTITY_BLUEPRINT_ID') `
    -Defends 'Infra cannot know the blueprint id under this model. An output that claims to supply it would resolve empty and be baked into the image as an empty string, which the null-coalescing fallback in config will not rescue.'

Assert-That -Name 'main.bicep outputs RESOURCE_GROUP' -Condition ($mainBicep -match 'output RESOURCE_GROUP') `
    -Defends 'agent-creation-script.ps1 builds the role assignment scope from RESOURCE_GROUP. Without it the scope string is malformed and the Cognitive Services User grant silently targets nothing.'

Assert-That -Name 'Agent creation sets digital_worker_type m365' -Condition ($creationPs -match 'digital_worker_type\s*=\s*"m365"') `
    -Defends 'The m365 type is what makes the platform handle the Microsoft 365 setup path. Without it the agent is created as a different kind of thing.'

Assert-That -Name 'Agent creation does not reference a pre-created blueprint' -Condition ($creationPs -notmatch 'blueprint_reference') `
    -Defends 'blueprint_reference pointed at a blueprint built in infra. Nothing creates that now, so the reference would name a blueprint that does not exist.'

Assert-That -Name 'Agent endpoint auth scheme defaults to BotServiceRbac' -Condition ($creationPs -match 'authScheme\s*=\s*"BotServiceRbac"') `
    -Defends 'This PATCH overwrites the endpoint authorization outright. With the bot service gone, defaulting to the old BotServiceTenant scheme would leave the endpoint expecting a resource that was never created.'

Assert-That -Name 'Publish uses the new autopilot schema' -Condition (($publishPs -match 'publishAsAutopilot') -and ($publishPs -notmatch 'publishAsDigitalWorker')) `
    -Defends 'The publish contract changed and the two calls are not interchangeable. The old flag goes with a different endpoint and body shape, so mixing them fails in a way that reads as a permissions problem.'

Assert-That -Name 'Publish targets the project endpoint, not agent-asset' -Condition (($publishPs -match 'microsoft365/publish\?api-version=2025-11-15-preview') -and ($publishPs -notmatch 'agent-asset')) `
    -Defends 'The old publish path was a different service entirely (azureml.ms/agent-asset). Calling it now returns errors that look like an auth failure rather than a wrong endpoint.'

Assert-That -Name 'Publish still sends a real bearer token' -Condition ($publishPs -match 'Bearer \$\(?\$?aiAzureToken') `
    -Defends 'Editors and log scrubbers render this header as asterisks. Retyping it from what is displayed writes a literal mask into the script, and every publish then fails 401 for a reason nothing in the diff explains.'

# ---------------------------------------------------------------------------
Write-Host ''
if ($script:Failures -gt 0) {
    Write-Host ("PREFLIGHT FAILED: {0} of {1} assertions failed. Deploy is blocked." -f $script:Failures, $script:Checks) -ForegroundColor Red
    Write-Host ''
    exit 1
}

Write-Host ("PREFLIGHT PASSED: {0} assertions." -f $script:Checks) -ForegroundColor Green
Write-Host ''
exit 0
