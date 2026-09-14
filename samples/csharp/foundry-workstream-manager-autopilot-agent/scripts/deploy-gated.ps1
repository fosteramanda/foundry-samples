$ErrorActionPreference = "Stop"
# Pin to the NotARealCo-only CLI config so this can never read or mutate the machine default.
$env:AZURE_CONFIG_DIR = "C:\Users\fosteramanda\.azure-notarealco-session"
$root = "C:\Users\fosteramanda\Code-Samples\foundry-samples-ado\samples\csharp\foundry-workstream-manager-autopilot-agent"
$src  = "$root\src\workstream_manager_agent"

Get-Content "$root\.azure\workstreammanagerado\.env" | ForEach-Object {
    if ($_ -match '^\s*([A-Za-z0-9_]+)\s*=\s*(.*)$') { Set-Item -Path "Env:$($Matches[1])" -Value ($Matches[2].Trim().Trim('"')) }
}
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet9"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"

# ---------------------------------------------------------------------------
# Preflight. These assertions GATE the deploy. Each says what it defends, so a
# future refactor can tell whether the RULE or the REGEX went stale.
# ---------------------------------------------------------------------------
$instr    = Get-Content "$src\AgentLogic\AgentInstructions.cs" -Raw
$service  = Get-Content "$src\AgentLogic\ResponsesApi\ResponsesApiAgentLogicService.cs" -Raw
$client   = Get-Content "$src\AgentLogic\ResponsesApi\Helpers\ResponsesApiClient.cs" -Raw
$program  = Get-Content "$src\Program.cs" -Raw
$factory  = Get-Content "$src\AgentLogic\ResponsesApi\ResponsesApiAgentLogicServiceFactory.cs" -Raw
$settings = Get-Content "$src\appsettings.json" -Raw
$create   = Get-Content "$root\scripts\agent-creation-script.ps1" -Raw
$routine  = Get-Content "$src\AgentLogic\ResponsesApi\Helpers\RoutineToolHandler.cs" -Raw
$registry = Get-Content "$src\AgentLogic\ResponsesApi\Helpers\MeetingRegistryToolHandler.cs" -Raw
$regstore = Get-Content "$src\Services\MeetingRegistryStore.cs" -Raw
$app      = Get-Content "$src\AgentLogic\A365AgentApplication.cs" -Raw
$manifest = Get-Content "$src\ToolingManifest.json" -Raw

$checks = @(
    # identity of THIS agent, which the port could have overwritten
    @{ Name = "identity: own persona";    Ok = ($instr -match 'You are a Workstream Manager autopilot') -and (-not ($instr -match 'Chief of Staff')); Why = "the port copied instructions from a sibling agent; this one must keep its own identity" }
    @{ Name = "identity: ADO preserved";  Ok = ($instr -match 'source of truth for engineering work'); Why = "ADO is why this agent exists; the ADO guidance moved into BuildAdoSection and must survive the port" }
    @{ Name = "toolbox still bound";      Ok = ($settings -match '"ToolboxName":\s*"workstream-manager-ado"'); Why = "the source agent has ToolboxName empty; copying that would unbind the ADO tools" }
    @{ Name = "toolbox pinned";           Ok = ($settings -match '"ToolboxVersion":\s*"7"'); Why = "unpinned follows the toolbox default, and version 5 fails tools/list with BadRequest on every turn" }

    # routines
    @{ Name = "routines: handler";        Ok = ($routine -match '"name": "create_routine"'); Why = "scheduling tool must exist" }
    @{ Name = "routines: wired";          Ok = ($service -match '_routineTools\.GetToolDefinitions\(\)'); Why = "handler present but the model cannot call it" }
    @{ Name = "routines: activity ctx";   Ok = ($service -match '_routineTools\.SetCurrentActivityContext'); Why = "a routine with no conversation reference fires and has nowhere to deliver" }
    @{ Name = "routines: env wired";      Ok = ($create -match 'FoundryProjectEndpoint = \$env:AZURE_AI_PROJECT_ENDPOINT') -and ($create -match 'FoundryAgentName = \$env:AGENT_NAME'); Why = "measured: without both env vars RoutineToolHandler.IsEnabled is false and the tools vanish with no error" }
    @{ Name = "routines: no bad appId";   Ok = (-not ($routine -match 'agenticAppId\s*=[^;]*FOUNDRY_AGENT_DEFAULT_INSTANCE_CLIENT_ID')); Why = "the instance identity cannot authenticate a scheduled run; that fallback produced routines that fire forever and never work" }
    @{ Name = "routines: prompt cond";    Ok = ($instr -match 'BuildRoutinesSection\(routinesEnabled\)'); Why = "never advertise scheduling the agent cannot do" }
    @{ Name = "routines: home tenant";    Ok = ($app -match 'ResolveHomeTenantId') -and ($app -match 'Connections:ServiceConnection:Settings:AuthorityEndpoint'); Why = "measured: a scheduled turn carries no tenant on the recipient AND none on the conversation when the routine payload predates the stored tenantId. Without a home-tenant last resort the agent-user token goes to /00000000-.../oauth2/v2.0/token and Entra returns AADSTS900021 on every single fire, silently" }
    @{ Name = "routines: mail attached";  Ok = ($manifest -match '"mcpServerName":\s*"mcp_MailTools"') -and ($manifest -match '"scope":\s*"McpServers\.Mail\.All"'); Why = "email is the ONLY working delivery for a scheduled run - chat replies are rejected 401 on the way back. Without the mail server the routine has no way to reach the user at all, and the prompt would be advertising a delivery the agent cannot perform" }
    @{ Name = "routines: email default";  Ok = ($routine -match 'IsNullOrWhiteSpace\(delivery\) \? "email"'); Why = "a chat-delivery routine fires, does the work, and delivers nothing while the platform still records the run as Finished. Defaulting to chat is a silent-failure generator" }
    @{ Name = "routines: send on sched";  Ok = ($service -match 'isScheduledRun') -and ($service -match 'MUST call your mail'); Why = "the interactive Teams framing tells the model its answer is delivered automatically and forbids looking for a send tool. On a scheduled run that is false and fatal: measured, a routine told to email composed a full answer and made ZERO tool calls because of that sentence. Scheduled turns need the opposite instruction" }

    # meetings
    @{ Name = "meeting: invite is consent"; Ok = ($regstore -match 'CaptureApproved \{ get; set; \} = true;') -and ($instr -match 'Consent is the invitation') -and ($instr -match 'Do NOT ask "do you approve"'); Why = "Amanda: there is no approval. By the time a meeting is on the agents calendar with a transcript the organizer has already invited it AND started transcription with the room notified. Asking again blocks the user and protects nothing. Exclusion stays available" }
    @{ Name = "meeting: exclusion works"; Ok = ($registry -match 'explicitly excluded from capture'); Why = "the opt-out must still exist and must say so plainly rather than quietly omitting the meeting" }
    @{ Name = "meeting: read is gated";   Ok = ($registry -match 'if \(!entity\.IsIngestionEligible\)'); Why = "eligibility must be checked before any transcript call, with no override" }
    @{ Name = "meeting: own calendar";    Ok = ($registry -match 'ResolveAgentMailboxAsync') -and (-not ($registry -match 'ResolveManagerMailboxAsync')); Why = "the agent is invited like a person and recaps ITS OWN meetings" }
    @{ Name = "meeting: url encoded";     Ok = ($registry -match 'Uri\.EscapeDataString\(entity\.JoinWebUrl\)'); Why = "the raw ? in a join URL truncates the OData filter: unterminated string literal at position 128" }
    @{ Name = "meeting: tz on reads";     Ok = ($registry -match 'preferTimeZone: DisplayTimeZone'); Why = "without the Prefer header Graph returns UTC and a 2:30pm meeting is reported as 9:30pm" }
    @{ Name = "meeting: tenant switch";   Ok = ($registry -match 'GraphAccessToTranscriptsDisabled'); Why = "a tenant-level Teams switch is not an app permission; sending someone to Entra wastes their afternoon" }
    @{ Name = "meeting: no-transcript";   Ok = ($registry -match 'Nobody turned transcription on'); Why = "no transcript and no permission are different failures and must not read the same" }
    @{ Name = "meeting: wired";           Ok = ($service -match '_meetingRegistryTools\.GetToolDefinitions\(\)'); Why = "handler present but the model cannot call it" }
    @{ Name = "meeting: store in DI";     Ok = ($program -match 'AddSingleton<MeetingRegistryStore>'); Why = "without registration the handler is never constructed and the tools silently vanish" }
    @{ Name = "meeting: factory passes";  Ok = ($factory -match 'meetingRegistry'); Why = "registered in DI but never handed to the service is the same as absent" }
    @{ Name = "meeting: prompt cond";     Ok = ($instr -match 'BuildMeetingRegistrySection\(meetingRegistryEnabled\)'); Why = "never advertise meeting tools when no table is configured to record a decision" }

    @{ Name = "routines: must check";     Ok = ($instr -match 'call list_routines FIRST'); Why = "measured: asked what standing work was scheduled, the model answered No standing work is scheduled without ever calling the tool. It was right by luck." }

    @{ Name = "chat: system payloads";  Ok = ($service -match 'IsTeamsSystemPayload') -and ($service -match 'Video.2/CallRecording'); Why = "measured: Teams posts recording-status XML and call metadata JSON into the meeting chat as ordinary messages. A blank-text-only guard caught none of it and the agent replied I cant respond in front of the meeting, twice" }
    @{ Name = "meeting: untitled ok";   Ok = ($registry -match 'DescribeSubject\(string\? subject'); Why = "measured: a Meet now meeting has a BLANK calendar subject, and skipping those made the agent say no invites on my calendar while looking straight at one" }
    @{ Name = "meeting: single fallback";Ok = ($registry -match 'matches.Count == 0 && online.Count == 1'); Why = "an untitled meeting matches no subject the user can type, so exactly-one must resolve by position rather than name" }

    @{ Name = "routines: tenant fallback"; Ok = ($app -match 'conversation\.TenantId, out var parsedConversationTenantId'); Why = "measured: a scheduled run's recipient carries no tenantId, so without falling back to the conversation the token request goes to the all-zero tenant, returns 400, and the routine's post fails 401 on every fire" }

    @{ Name = "meeting: /me endpoints";  Ok = ($registry -match '"me/onlineMeetings') -and (-not ($registry -match 'users/\{Uri\.EscapeDataString\(mailbox\)\}')); Why = "measured: Graph rejects the user-scoped onlineMeetings form on a delegated token with 'only /me is supported', even when the id resolved is the callers own" }
    @{ Name = "chat: no delivery talk";  Ok = ($service -match 'do not look for a tool to send'); Why = "measured: framing the turn as respond to chat id X made the model hunt for a send tool and prefix its answer with I couldnt post directly to that chat" }

    # no phantom capabilities
    @{ Name = "no phantom A2A tools";   Ok = (-not ($instr -match 'list_workiq_agents|ask_workiq_agent')); Why = "measured: the port brought a delegation section describing A2A tools this agent does not have, and a guard forbidding workiq___ask with an agentId, which is this agent's ONLY delegation path. The agent then tried to find a send tool and failed." }
    @{ Name = "real delegation kept";   Ok = ($instr -match 'Work IQ `ask` tool'); Why = "removing the phantom section must not take the legitimate source-of-truth delegation with it" }
    @{ Name = "no phantom mailbox";       Ok = (-not ($instr -match 'send_email_as_manager')); Why = "the mailbox handler was deliberately not ported; the prompt must not describe tools that do not exist" }
    @{ Name = "prompt flags from client"; Ok = ($client -match 'RoutinesEnabled') -and ($client -match 'MeetingRegistryEnabled'); Why = "prompt sections must derive from real handler state, not config guesses" }
)

Write-Host "=== preflight ==="
$failed = @()
foreach ($c in $checks) {
    "  [{0}] {1}" -f $(if ($c.Ok) { "PASS" } else { "FAIL" }), $c.Name
    if (-not $c.Ok) { $failed += $c }
}
if ($failed.Count -gt 0) {
    Write-Host ""
    foreach ($f in $failed) { Write-Host "  FAILED: $($f.Name) - $($f.Why)" -ForegroundColor Red }
    throw "Preflight failed ($($failed.Count) of $($checks.Count)). Nothing was built or deployed."
}
Write-Host "  all $($checks.Count) checks passed"

Write-Host "`n=== ACR build ==="
& "$root\scripts\build-docker-image-acr.ps1" 2>&1 | Select-Object -Last 3

Write-Host "`n=== new agent version ==="
$createOutput = & "$root\scripts\agent-creation-script.ps1" *>&1
$createOutput | Select-String -Pattern 'Agent Version:|provisioned:|Agent GUID:' | ForEach-Object { "  $($_.Line.Trim())" }

# agent-creation-script.ps1 creates the version but leaves traffic on the PREVIOUS one, so
# without this the new image is live nowhere. Repinning is the difference between built and
# deployed.
$m = $createOutput | Select-String -Pattern 'Agent Version:\s*(\d+)' | Select-Object -Last 1
if (-not $m) { throw "Could not parse the new agent version; traffic NOT repinned." }
$newVersion = $m.Matches.Groups[1].Value

Write-Host "`n=== repin traffic to v$newVersion ==="
$tok  = az account get-access-token --resource "https://ai.azure.com" --query accessToken -o tsv
$uri  = "https://workstreammanagerado.services.ai.azure.com/api/projects/workstreammanagerado/agents/workstreammanagerado?api-version=2025-11-15-preview"
$body = '{"agent_endpoint":{"version_selector":{"version_selection_rules":[{"type":"FixedRatio","agent_version":"VERSIONTOKEN","traffic_percentage":100}]}}}'.Replace("VERSIONTOKEN", $newVersion)
$resp = Invoke-RestMethod -Method Patch -Uri $uri -Headers @{Authorization = "Bearer $tok"} -ContentType "application/json" -Body $body

$rules = $resp.agent_endpoint.version_selector.version_selection_rules
$rules | ForEach-Object { "  traffic -> v$($_.agent_version)  $($_.traffic_percentage)%" }
if (-not ($rules | Where-Object { $_.agent_version -eq $newVersion -and $_.traffic_percentage -eq 100 })) {
    throw "Repin did not take effect: v$newVersion is not serving 100% of traffic."
}
Write-Host "`nDeployed and serving: v$newVersion"

# A traffic repin does NOT restart a running container. If one is already warm it keeps
# serving the OLD image until it idles out, which is 11-15 minutes of inactivity. Testing
# continuously KEEPS IT WARM, so the more eagerly a fix is tested the longer it takes to
# arrive. This cost several rounds of diagnosing symptoms from code already replaced.
Write-Host "`n=== is a stale container still serving? ==="
$aiRid = $env:APPLICATIONINSIGHTS_RESOURCE_ID
if ($aiRid) {
    $q = "traces | where timestamp > ago(30m) | where message has 'Application starting' | summarize last=max(timestamp)"
    $raw = az monitor app-insights query --ids $aiRid --analytics-query $q -o json 2>&1 | Out-String
    $lastStart = $null
    if ($raw -notmatch '^ERROR') { try { $lastStart = ($raw | ConvertFrom-Json).tables[0].rows[0][0] } catch { } }
    $q2 = "traces | where timestamp > ago(15m) | summarize n=count()"
    $raw2 = az monitor app-insights query --ids $aiRid --analytics-query $q2 -o json 2>&1 | Out-String
    $recent = 0
    if ($raw2 -notmatch '^ERROR') { try { $recent = [int](($raw2 | ConvertFrom-Json).tables[0].rows[0][0]) } catch { } }

    if ($recent -gt 0) {
        Write-Host "  WARNING: the container has been active in the last 15 minutes." -ForegroundColor Yellow
        Write-Host "  It is still running the PREVIOUS image and will not pick up v$newVersion" -ForegroundColor Yellow
        Write-Host "  until it has been idle for roughly 11-15 minutes." -ForegroundColor Yellow
        Write-Host "  Do not test immediately: wait for the idle timeout, THEN send the first message." -ForegroundColor Yellow
        Write-Host "" -ForegroundColor Yellow
        Write-Host "  MORE THAN ONE REPLICA CAN BE ALIVE, ON DIFFERENT VERSIONS." -ForegroundColor Yellow
        Write-Host "  Measured: replicas started 13:32 and 13:52 straddled a version, and the same" -ForegroundColor Yellow
        Write-Host "  question got a working answer on one and a 400 on the other twenty minutes apart." -ForegroundColor Yellow
        Write-Host "  One good reply does NOT mean the fix is live everywhere. Check that every" -ForegroundColor Yellow
        Write-Host "  'Application starting' in the window is AFTER this version was created." -ForegroundColor Yellow
        Write-Host "  Verify with: traces | where message has 'Application starting'" -ForegroundColor Yellow
    } else {
        Write-Host "  Container is cold (no activity in 15 min). The next message loads v$newVersion." -ForegroundColor Green
    }
    if ($lastStart) { Write-Host "  last container start: $lastStart" }
} else {
    Write-Host "  (APPLICATIONINSIGHTS_RESOURCE_ID not set; cannot check)"
}











