---
description: Scenario and design intent for a Microsoft Foundry Bring-Your-Own-Model (BYOM) sample that governs multi-vendor model access — Microsoft Foundry, Google Gemini, and Groq — through a single Azure API Management AI Gateway, with Terraform. Positive path, governance enforcement, and unified telemetry are demonstrated across two Azure subscriptions.
page_type: sample
products:
- azure
- azure-resource-manager
- azure-ai-foundry
- azure-api-management
urlFragment: foundry-byom-multi-vendor-terraform
languages:
- hcl
---

# Microsoft Foundry: Bring Your Own Model — Multi-Vendor Governance (Terraform)

> **Status:** Scenario and design draft. Terraform modules will land in a follow-up
> update. This document exists to align stakeholders on the story, the boundaries,
> and the acceptance criteria before code is written.

## Architecture

```text
      ┌───────────────────────────────────────────────────────────────────────────┐
      │                External Model Providers (outside Azure)                   │
      │                                                                           │
      │       Google Gemini                          Groq  (Llama models)         │
      │       Google AI Studio                       Groq inference API           │
      │       auth: API key                          auth: API key                │
      └────────────────────┬────────────────────────────────┬─────────────────────┘
                           │                                │
                           │  Outbound HTTPS from APIM only.                       
                           │  Provider credentials retrieved from Key Vault.      
                           │                                │
   ╔═══════════════════════▼════════════════════════════════▼══════════════════════╗
   ║                                                                               ║
   ║   Sub A — Platform Subscription                     Owner: Platform Admin     ║
   ║   ═══════════════════════════════════════════════════════════════════════     ║
   ║                                                                               ║
   ║   ┌────────────────────┐    ┌────────────────────────┐   ┌─────────────────┐  ║
   ║   │ Microsoft Foundry  │    │ Azure API Management   │   │ Azure Key Vault │  ║
   ║   │ (Model host)       │◄───│ (AI Gateway)           │──►│  gemini-api-key │  ║
   ║   │   gpt-4o           │ MI │                        │   │  groq-api-key   │  ║
   ║   └────────────────────┘    │  • 3 vendor backends   │   └─────────────────┘  ║
   ║      Microsoft vendor       │  • Validate project MI │                        ║
   ║      in the catalog         │  • Allowlist catalog   │   ┌─────────────────┐  ║
   ║                             │  • Per-project quota   │──►│ Log Analytics   │  ║
   ║                             │  • Emit telemetry      │   │ (unified sink)  │  ║
   ║                             └───────────┬────────────┘   └─────────────────┘  ║
   ║                                         │                                     ║
   ╚═════════════════════════════════════════╪═════════════════════════════════════╝
                                             │
                    ══════ Trust boundary — subscription split ══════
                    Only the APIM gateway URL crosses. Developers hold
                    no provider credential and reach no model directly.
                                             │
   ╔═════════════════════════════════════════▼═════════════════════════════════════╗
   ║                                                                               ║
   ║   Sub B — Development Subscription                    Users: Agent Developers ║
   ║   ═══════════════════════════════════════════════════════════════════════     ║
   ║                                                                               ║
   ║   ┌───────────────────────────────────────────────────────────────────────┐   ║
   ║   │ Microsoft Foundry account (Project host — no model deployments)       │   ║
   ║   │                                                                       │   ║
   ║   │  ┌────────────────────────────┐    ┌────────────────────────────┐     │   ║
   ║   │  │ Project Alpha              │    │ Project Beta               │     │   ║
   ║   │  │  System-assigned MI        │    │  System-assigned MI        │     │   ║
   ║   │  │  ApiManagement connection ─┼────┼─► shared APIM in Sub A     │     │   ║
   ║   │  │                            │    │                            │     │   ║
   ║   │  │  Approved catalog:         │    │  Approved catalog:         │     │   ║
   ║   │  │   • gateway/gpt-4o         │    │   • gateway/gpt-4o         │     │   ║
   ║   │  │   • gateway/gemini-flash   │    │   • gateway/gemini-flash   │     │   ║
   ║   │  │   • gateway/llama-3-70b    │    │   • gateway/llama-3-70b    │     │   ║
   ║   │  └────────────────────────────┘    └────────────────────────────┘     │   ║
   ║   │                                                                       │   ║
   ║   │  Developer RBAC                                                       │   ║
   ║   │    Granted     : Foundry User on assigned project                     │   ║
   ║   │    Not granted : Cognitive Services Contributor anywhere              │   ║
   ║   │                  (developers cannot deploy or modify models)          │   ║
   ║   └───────────────────────────────────────────────────────────────────────┘   ║
   ║                                                                               ║
   ╚═══════════════════════════════════════════════════════════════════════════════╝
```

**Reading the diagram.** Three model vendors participate in Contoso's approved
catalog: Microsoft (an OpenAI model hosted on Microsoft Foundry inside the
Platform subscription), Google (Gemini via Google AI Studio), and Groq (Llama
models via the Groq inference API). Microsoft's model runs in-cloud and
in-tenant; the other two are reached over public HTTPS. Regardless of vendor,
every request originates from a Foundry project managed identity in the
Development subscription, is validated and governed by the shared Azure API
Management gateway in the Platform subscription, and returns telemetry into a
single Log Analytics workspace.

**Runtime call flow.** A developer in Sub B builds a prompt agent that
references one of the approved aliases, for example `gateway/gemini-flash`. At
invocation time:

1. The project's system-assigned managed identity mints an Entra token.
2. Azure API Management validates the token — correct tenant, correct project
   MI application ID, expected audience.
3. APIM checks the requested model against the approved catalog. Unknown
   aliases are rejected before any upstream traffic is generated.
4. APIM enforces the per-project token quota. Exceeded quotas return HTTP 429
   with quota-remaining headers, and no backend is charged.
5. APIM selects the correct vendor backend. Microsoft Foundry backends are
   called with APIM's own managed identity; third-party backends are called
   with a key retrieved from Azure Key Vault.
6. The response returns to the project. APIM emits a telemetry record —
   project, model, vendor, prompt tokens, completion tokens — into the shared
   Log Analytics workspace.

Nothing in this path requires the developer to know where any model runs, hold
any provider credential, or cross the subscription boundary except through the
gateway.

## Overview

Modern enterprises rarely standardize on a single model provider. It is common to
find OpenAI models on Microsoft Foundry alongside Google Gemini, Anthropic Claude,
open-weight Llama models hosted on inference platforms, and self-hosted models
behind a private gateway. Each provider has its own credentials, API shape, quota
model, and observability surface.

This sample demonstrates a repeatable pattern for **using Microsoft Foundry as the
agent development platform while governing model access across multiple providers
through a single Azure API Management (APIM) AI Gateway**. Developers build prompt
agents against an approved model catalog. Administrators own model deployments,
provider credentials, token-consumption policy, and telemetry.

The intent is not to endorse or rank any specific model provider. The included
non-Microsoft providers were selected because they offer zero-friction free tiers
suitable for a live demo, and because their API shapes exercise both the
OpenAI-compatible path and a genuinely different upstream shape — both of which
Azure API Management can normalize into the connection contract Foundry expects.

## The Contoso story

**Contoso** is a mid-sized enterprise. Their AI portfolio is intentionally
multi-vendor: OpenAI models on Microsoft Foundry, Google Gemini via Google AI
Studio, and Llama models served by Groq. The platform team owns which models are
available, how much they can be used, and how usage is measured. Application
teams build agents inside Foundry projects.

- **Priya**, the Contoso Platform Administrator, keeps every model deployment and
  every provider credential inside a locked-down **platform subscription**. She
  exposes the approved catalog through a single Azure API Management gateway,
  applies per-project token-consumption policies, and owns telemetry.
- **Dev**, an Agent Developer, works in a separate **development subscription**.
  He opens his assigned Foundry project, sees three approved model aliases, and
  builds prompt agents that call them. He cannot deploy models, cannot read any
  provider secret, and does not need to know where each model physically runs.

## Approved model catalog

| Alias in Foundry | Vendor | Physical location | Auth from APIM to backend |
|---|---|---|---|
| `gateway/gpt-4o` | Microsoft | Foundry account in Sub A | Managed identity |
| `gateway/gemini-flash` | Google | Google AI Studio | API key (in Key Vault) |
| `gateway/llama-3-70b` | Groq | Groq inference platform | API key (in Key Vault) |

The catalog is defined at the platform (subscription) level. Every governed
Foundry project sees the same three aliases; token quotas and observability are
tracked **per project**, not per catalog entry.

## What this sample will demonstrate

Success is measured on three axes:

- **Positive path.** A developer working in Project Alpha creates a prompt agent
  that references `gateway/gpt-4o`, `gateway/gemini-flash`, and
  `gateway/llama-3-70b` in turn. All three calls succeed through the same
  Responses API code path, exercising a Microsoft-hosted model, a native Google
  API shape, and an OpenAI-compatible third-party endpoint.
- **Governance path.** Attempts to violate policy are rejected at runtime:
  - A developer running `az cognitiveservices ... deployment create` in Sub B
    fails with `AuthorizationFailed`. Model deployment is not a developer-scoped
    action anywhere in Contoso.
  - A prompt agent referencing a model not in the approved catalog receives a
    gateway error and no upstream traffic is generated.
  - A project that exceeds its token quota receives an HTTP 429 response with
    quota-remaining headers; no backend charge is incurred.
- **Observability.** A single Kusto query in the Sub A Log Analytics workspace
  returns per-project, per-model token consumption across all three vendors,
  proving unified telemetry regardless of upstream provider.

## Governance boundaries

| Component | Responsibility |
|---|---|
| Platform Administrator (Priya) | Owns Sub A. Deploys models, onboards providers, defines the approved catalog, creates governed projects in Sub B, configures APIM policies, owns telemetry. |
| Agent Developer (Dev) | Owns nothing. Consumes approved models inside assigned projects. |
| Microsoft Foundry account (Sub A) | Hosts Microsoft-managed model deployments only. Not visible to developers. |
| Microsoft Foundry account (Sub B) | Hosts governed projects. Has no model deployments — developer self-service is prevented by design, not by RBAC alone. |
| Azure API Management | Enforces authorization, catalog allowlist, token quotas, routing, and emits telemetry. The only ingress to any model backend from Sub B. |
| Azure Key Vault | Holds third-party provider credentials. Not visible to developers or projects. |
| Log Analytics workspace (Sub A) | Unified telemetry sink for every model call regardless of provider. |

## Design decisions

The following decisions are locked for this sample. Alternatives are noted for
future extensions.

- **Two Azure subscriptions.** Platform (Sub A) and Development (Sub B). This is
  the minimum split that enforces administrator/developer separation.
- **Approved catalog is defined at platform level, not per project.** Every
  governed project sees the same three aliases. Per-project catalogs remain a
  future extension.
- **Static model catalog.** The catalog is explicit in Terraform variables.
  Dynamic model discovery is not used in v1.
- **Public networking.** The APIM service is public. Private networking with
  VNet integration and private endpoints is a planned follow-up sample.
- **Prompt agents through the Responses API.** BYOM `<connection>/<model>`
  resolution requires a prompt agent invoked through the Responses API. The
  classic Assistants API cannot resolve gateway-connected model aliases.
- **Zero-friction third-party providers.** Google Gemini via Google AI Studio and
  Groq were selected because both offer free tiers with no credit card, no phone
  verification, and no expiring credits. Any OpenAI-compatible provider or any
  provider with an equivalent authentication contract can be substituted.

## Prerequisites

When the Terraform modules land, deployment will require the following.

**Azure**

- Two Azure subscriptions in the same tenant, referred to as **Sub A**
  (platform) and **Sub B** (development). The deploying identity needs Owner or
  `User Access Administrator` + `Contributor` on both, to create resources and
  the role assignments that give APIM inference access to the Microsoft Foundry
  model account.
- An existing Microsoft Foundry account in Sub A with at least one Azure OpenAI
  model deployment (for example, `gpt-4o`). The sample layers onto this account
  and does not create it.
- Registered resource providers: `Microsoft.CognitiveServices`,
  `Microsoft.ApiManagement`, `Microsoft.KeyVault`, `Microsoft.OperationalInsights`.
- Sufficient quota for one Azure API Management StandardV2 instance and the
  target Log Analytics workspace region.

**Third-party**

- A **Google AI Studio** API key (`aistudio.google.com`). The free tier is
  quota-based, no credit card, no phone verification, no expiring credits.
- A **Groq** API key (`console.groq.com`). Same characteristics.

**Tooling**

- Azure CLI signed in and set to the correct subscription for each apply.
- Terraform CLI, using the `azurerm`, `azapi`, `random`, and `time` providers
  already standardized across this repository (see other `infrastructure-setup-terraform`
  samples for the version matrix).

**Secret handling**

- Third-party API keys are marked `sensitive = true` in Terraform. They are
  written to Azure Key Vault and referenced from APIM through named values.
  They must never be committed to source. Remote Terraform state must be
  protected accordingly — use an encrypted `azurerm` backend and restrict
  access to the Platform Administrator identity only.

## Module structure

The sample uses a **single-root Terraform module** that targets both
subscriptions through aliased providers (`azurerm.platform`,
`azurerm.development`, `azapi.platform`, `azapi.development`). This choice
lets one `terraform apply` resolve the cross-subscription dependency chain
(Foundry project managed identities → APIM allowlist policy → project
`ApiManagement` connections) without state-file plumbing between two roots.

Files are grouped by tier via naming convention (`platform-*.tf` for Sub A,
`development-*.tf` for Sub B), and shared/cross-cutting resources live in
the standard file names used throughout `infrastructure-setup-terraform`:

```text
46-bring-your-own-models/
├── README.md                          # This document
└── code/
    ├── versions.tf                    # Provider version pins (incl. azuread)
    ├── providers.tf                   # Aliased providers: platform, development, azuread
    ├── variables.tf                   # All inputs (sensitive keys marked)
    ├── locals.tf                      # Naming, catalog, apim_gateway_target
    ├── main.tf                        # Two resource groups (one per subscription)
    ├── outputs.tf                     # Gateway URL, project endpoints, catalog
    ├── example.tfvars                 # Placeholder tfvars (REPLACE_ME markers)
    │
    │   # ---- Platform tier (Sub A — administrator-owned) ----
    ├── platform-foundry.tf            # Foundry model account + gpt-4o deployment
    ├── platform-monitor.tf            # Log Analytics + Application Insights
    ├── platform-keyvault.tf           # Key Vault + Gemini/Groq API-key secrets
    ├── platform-apim.tf               # APIM service, MI, Cog Services User, logger
    ├── platform-apim-api.tf           # /inference API + diagnostic → App Insights
    ├── platform-apim-named-values.tf  # KV-backed named values for provider keys
    ├── platform-apim-policy.tf        # templatefile → API-scope policy binding
    ├── apim-policy.xml.tftpl          # Multi-vendor policy (auth, quota, routing)
    │
    │   # ---- Development tier (Sub B — developer-facing) ----
    ├── development-foundry.tf         # Foundry agent account (no deployments)
    ├── development-projects.tf        # Project Alpha, Project Beta with MIs
    ├── development-connections.tf     # ApiManagement connections + catalog
    └── development-rbac.tf            # Developer group role on each project
```

A single `terraform apply` provisions both tiers. The dependency graph is
enforced implicitly through references: the APIM policy consumes the project
MI application (client) IDs looked up via the `azuread` provider, and the
project `ApiManagement` connections consume the APIM gateway URL and depend
on the policy being in place before they are created.

## Where this fits in the broader collection

- The **golden-path guide** for BYOM is defined in
  [`../../infrastructure-setup-bicep/golden-path/README.md`](../../infrastructure-setup-bicep/golden-path/README.md).
  This sample implements the Terraform equivalent of the Bicep
  `01-connections/public-byom-apim` scenario extended to multiple vendors and
  a two-subscription topology.
- For the single-vendor, single-subscription baseline, see
  [`../41-standard-agent-setup`](../41-standard-agent-setup/).
- For the private-networking equivalent of the BYOM AI Gateway pattern, see the
  Bicep reference at
  [`../../infrastructure-setup-bicep/16-private-network-standard-agent-apim-setup/extensions/byom-cross-region`](../../infrastructure-setup-bicep/16-private-network-standard-agent-apim-setup/extensions/byom-cross-region/).
  A Terraform port of that scenario is planned as a follow-up to this sample.

## References

- [Bring your own model to Microsoft Foundry Agent Service](https://learn.microsoft.com/azure/foundry/agents/how-to/ai-gateway)
- [Azure API Management policies for large language model APIs](https://learn.microsoft.com/azure/api-management/llm-token-limit-policy)
- [Microsoft Foundry role-based access control](https://learn.microsoft.com/azure/ai-foundry/concepts/rbac-azure-ai-foundry)
- [`azapi` Terraform provider](https://registry.terraform.io/providers/azure/azapi/latest/docs)
- [`azurerm` Terraform provider](https://registry.terraform.io/providers/hashicorp/azurerm/latest/docs)

## Non-goals

The following are explicitly out of scope for this sample and will be tracked
as separate follow-ups.

- Private networking, VNet integration, and private endpoints for APIM.
- Cross-region backends and disaster-recovery routing.
- Per-project approved catalogs (the platform-level catalog is used here).
- Dynamic model discovery through the gateway.
- Content-safety policies beyond token quotas.
- Cost chargeback pipelines beyond raw per-project token telemetry.

Third-party providers are named because concrete examples make the pattern
easier to follow. The pattern itself is provider-neutral — any provider that
exposes an authenticated HTTPS inference endpoint can be plugged into the same
APIM gateway using the mechanisms shown here.
