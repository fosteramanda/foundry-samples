<!-- Begin standard disclaimer — do not modify -->
**IMPORTANT!** All samples and other resources made available in this GitHub repository ("samples") are designed to assist in accelerating development of agents, solutions, and agent workflows for various scenarios. Review all provided resources and carefully test output behavior in the context of your use case. AI responses may be inaccurate and AI actions should be monitored with human oversight. Learn more in the transparency note for [Agent Service](https://learn.microsoft.com/en-us/azure/ai-foundry/responsible-ai/agents/transparency-note).

Agents, solutions, or other output you create may be subject to legal and regulatory requirements, may require licenses, or may not be suitable for all industries, scenarios, or use cases. By using any sample, you are acknowledging that any output created using those samples are solely your responsibility, and that you will comply with all applicable laws, regulations, and relevant safety standards, terms of service, and codes of conduct.

Third-party samples contained in this folder are subject to their own designated terms, and they have not been tested or verified by Microsoft or its affiliates.

Microsoft has no responsibility to you or others with respect to any of these samples or any resulting output.
<!-- End standard disclaimer -->

# Voice Live Bridge — Basic .NET

## What this sample demonstrates

This sample gives a hosted .NET text agent a managed voice experience without adding audio code to
the agent. It implements Voice Live Bridge Protocol `1.0` with the typed
`Azure.AI.AgentServer.Invocations.Voice` API and streams text from a Microsoft Foundry model through
the Responses API.

Voice Live owns audio input, speech recognition, turn detection, synthesis, playback interruption,
and proactive-response admission. This hosted agent receives text and control events on
`/invocations_ws`.

## How it works

```text
Caller audio
    → Foundry voice agent
    → Voice Live (STT, VAD, TTS, barge-in)
    → Bridge Protocol 1.0 over invocations_ws
    → this hosted .NET text agent
    → Foundry model Responses API
```

The application explicitly accepts `session.start`, sends `session.ready`, receives final
transcripts as `user.message`, and streams `response.output_text.delta` messages back to Voice
Live. The AgentServer Voice API is a typed relay; the application owns response IDs, tasks,
cancellation, bounded conversation history, and terminal-event correlation.

### Project layout

- `VoiceAgent.cs` is the thin protocol handler: it owns the SDK callbacks, session handshake, and
  sample-command dispatch.
- `VoiceConnectionState.cs` atomically enforces connection phase, input deduplication, proactive
  admission, and active-response capacity.
- `ConversationHistory.cs` contains bounded per-session model history and response-to-history
  correlation for barge-in reconciliation.
- `VoiceResponseCoordinator.cs` reserves and executes responses, emits the ordered wire sequence,
  and owns cancellation, termination, and task cleanup.
- `AzureOpenAIResponsesClient.cs` is the external Foundry Responses API adapter.

This separation keeps protocol callbacks easy to follow while isolating concurrency-sensitive
state and response execution without using partial classes.

Protocol `1.0` supports text input, streaming responses, `response.none`,
no-input turns, barge-in, response timeout, proactive responses, response self-cancellation,
errors, and call termination. It does not support DTMF, image input, app-driven history mutation,
or hosted-agent handoff.

Bridge Protocol `1.0` is the application-level event contract between Voice Live and this agent.
The manifest's `invocations_ws` version `2.0.0` selects the Hosted Agent WebSocket ingress
transport. They are separate versioned layers and must not be treated as interchangeable.

## Prerequisites

1. [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Azure Developer CLI (`azd`) 1.27.1 or later with the Foundry extension.
3. Azure CLI authentication for local model access. The signed-in identity needs at least the
  **Azure AI User** role on the Foundry project.
4. A Foundry project and model deployment, or permission for `azd provision` to create them.

Install and authenticate the tools:

```bash
azd ext install microsoft.foundry
azd auth login
az login
```

## Option 1: Azure Developer CLI

Initialize this sample from the public manifest in a new directory:

```bash
mkdir voice-live-bridge-basic-dotnet
cd voice-live-bridge-basic-dotnet
azd ai agent init -m https://github.com/microsoft-foundry/foundry-samples/blob/main/samples/csharp/hosted-agents/bring-your-own/voice-agent-target-agent/basic/azure.yaml
```

Provision the declared Foundry project and model when needed, run locally, and deploy:

```bash
azd provision
azd ai agent run
azd deploy
```

Neither `azd ai agent invoke` nor Agent Inspector generates Voice Live Bridge events. Use the
protocol-aware smoke client described below to exercise this agent.

### Deploy to an existing Foundry project

The `ai-project.deployments` block in `azure.yaml` is the default for provisioning a new project.
To reuse a project that already has a model deployment, create an `azd` environment in this sample
directory and identify the existing resources explicitly:

```bash
azd env new "<environment-name>"
azd env set AZURE_SUBSCRIPTION_ID "<subscription-id>"
azd env set AZURE_LOCATION "<foundry-project-region>"
azd env set AZURE_AI_PROJECT_ID "<foundry-project-resource-id>"
azd env set FOUNDRY_PROJECT_ENDPOINT "<foundry-project-endpoint>"
azd env set AZURE_AI_MODEL_DEPLOYMENT_NAME "<existing-model-deployment-name>"
azd deploy
```

Copy the project resource ID, endpoint, and region from **Manage** > **Project details** in the
Foundry portal. The model deployment name must exactly match a deployment in that project.
`AZURE_AI_MODEL_DEPLOYMENT_NAME` configures the hosted agent runtime; it does not rename, replace,
or suppress the model declared under `ai-project.deployments`.

Do not run `azd provision` or `azd up` in this existing-project flow. Those commands apply the
fresh-project declaration and may attempt to create another `gpt-5.4-mini` deployment, consuming
additional quota. After `azd deploy`, ensure the new hosted-agent instance identity has
**Cognitive Services OpenAI User** (or an equivalent inherited model-inference role) on the parent
AI Services account before exercising model-backed Bridge turns.

## Option 2: Run locally

This sample uses direct code deployment; no Docker build is required. From the sample root,
restore and build the application:

```bash
dotnet restore src/voice-live-bridge-basic-dotnet/VoiceLiveBridgeBasic.csproj
dotnet build src/voice-live-bridge-basic-dotnet/VoiceLiveBridgeBasic.csproj --no-restore
```

The runtime's `NuGet.config` already configures NuGet.org and the user-specified public Azure SDK
for .NET feed. Package source mapping sends only `Azure.AI.AgentServer.*` to that Azure SDK feed and
all other packages to NuGet.org. No credentials, local packages, private feeds, or extra restore
arguments are required.

Create local configuration and set the required values:

```bash
cd src/voice-live-bridge-basic-dotnet
cp .env.example .env
```

| Variable | Required | Description |
| --- | --- | --- |
| `FOUNDRY_PROJECT_ENDPOINT` | Yes | HTTPS project endpoint ending in `/api/projects/<project>`. Do not append `/openai/v1`; the application does that. |
| `AZURE_AI_MODEL_DEPLOYMENT_NAME` | Yes | Foundry model deployment name. |
| `AZURE_OPENAI_MAX_OUTPUT_TOKENS` | No | Maximum output tokens, from 1 through 4096; defaults to `512`. |
| `AZURE_OPENAI_SYSTEM_PROMPT` | No | Overrides the concise voice-assistant system prompt. |
| `AZURE_OPENAI_API_KEY` | Local only | Optional development credential. Do not put it in the hosted-agent manifest. |
| `AZURE_TOKEN_CREDENTIALS` | No | Set to `AzureCliCredential` to constrain `DefaultAzureCredential` locally. |
| `PORT` | No | Server port; defaults to `8088`. |

For local Entra authentication, load the file and start the server:

```bash
set -a
source .env
set +a
export AZURE_TOKEN_CREDENTIALS=AzureCliCredential
dotnet run --project VoiceLiveBridgeBasic.csproj
```

The server listens on `PORT` or port `8088` and exposes `GET /readiness` and the
`/invocations_ws` WebSocket route. Hosted deployments should use the hosted agent's managed
identity. The model token uses the `https://ai.azure.com/.default` scope, and Responses requests set
`store=false`.

The Foundry Toolkit Agent Inspector does not generate Bridge Protocol WebSocket events. Start the
server manually or with `azd ai agent run`, then use the smoke client.

## Local protocol smoke test

In a second terminal, from the runtime directory, run:

```bash
dotnet run --project smoke/VoiceLiveBridgeBasic.SmokeTest.csproj
```

The default smoke input is `/help`, so this validates the wire flow without calling a model.
Override the target or input when needed:

```bash
dotnet run --project smoke/VoiceLiveBridgeBasic.SmokeTest.csproj -- \
  --uri ws://127.0.0.1:8088/invocations_ws \
  --text /help
```

Expected message sequence:

```text
session.ready
response.created
response.output_text.done
response.done
```

## Give the hosted agent a voice

The sample `azure.yaml` already declares a managed Voice wrapper agent that targets this hosted
agent. `azd deploy` deploys both the hosted target and the Voice wrapper; no manual JSON wrapper
creation is required for the main azd flow.

The caller connects to the Voice wrapper's Voice WebSocket endpoint and never sends Bridge messages
directly to the hosted text agent. Conversation logic, model calls, tools, and response generation
belong to the target hosted agent. Voice configuration and the managed audio experience belong to
the Voice wrapper and Voice Live.

## Sample commands

| User text | Behavior |
| --- | --- |
| Any text or `/stream text` | Stream a model response. |
| `/done text` | Send one completed model output item. |
| `/voice text` | Send completed text with a `+10%` synthesis-rate patch. |
| `/none` | Decline the input with `response.none`. |
| `/proactive text` | Generate only after Voice Live sends `response.accepted`. |
| `/cancel text` | Open a response and request self-cancellation. |
| `/error` | Emit a sanitized response-scoped sample error. |
| `/end` or `/end-now` | End after queued audio drains or immediately. |
| `/help` | Return this command set without calling the model. |

## Tests

Tests use deterministic fake model streams and make no cloud calls:

```bash
dotnet test tests/VoiceHostedAgent.Tests.csproj
```

From the repository root, run the standard per-sample validator:

```bash
bash .github/scripts/validate-sample.sh \
  --language csharp \
  --sample-dir samples/csharp/hosted-agents/bring-your-own/voice-agent-target-agent/basic
```

The repository validator executes the `build`, `validate`, and `test` commands in `sample.yaml` from
the sample root.

## Design notes

- Conversation history is bounded and exists only for one physical agent connection; ending one
  session clears only that session's history correlations.
- Barge-in reconciles assistant history to the text Voice Live reports as heard by the caller.
- Proactive output never starts before `response.accepted`.
- Custom spans contain identifiers, durations, and status, but no prompt or response content.
- The sample has not been validated against a deployed cloud voice agent as part of these
  instructions; perform deployment-specific validation in your own environment.

## Troubleshooting

- If `dotnet restore` cannot find `Azure.AI.AgentServer.Invocations`, verify that the runtime
  `NuGet.config` is present and that the public `azure-sdk-for-net` feed is reachable.
- If the server rejects `session.start` with `protocol_mismatch`, ensure the event declares Bridge
  Protocol `1.0`. This is separate from `invocations_ws` transport version `2.0.0` in `azure.yaml`.
- If startup reports an invalid endpoint, use the Foundry project endpoint exactly in the form
  `https://<account>.services.ai.azure.com/api/projects/<project>` without `/openai/v1` or
  `/responses`.
- If model calls fail locally, verify `FOUNDRY_PROJECT_ENDPOINT`,
  `AZURE_AI_MODEL_DEPLOYMENT_NAME`, `az login`, and the active identity's project access.
- If the smoke client cannot connect, start the server first and verify port `8088`, or pass a
  matching `--uri`.
- Agent Inspector and `azd ai agent invoke` cannot validate this protocol because they do not emit
  Voice Live Bridge event frames.

## Next steps

- [Voice Live Bridge samples](../)
- [Hosted agents overview](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/hosted-agents)
- [Deploy a hosted agent](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/deploy-hosted-agent)
- [AgentServer Invocations SDK for .NET](https://www.nuget.org/packages/Azure.AI.AgentServer.Invocations)
