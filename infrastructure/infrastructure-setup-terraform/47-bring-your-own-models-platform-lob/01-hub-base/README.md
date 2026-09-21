# hub-base — step 1a

Platform admin runs this **once** at hub bring-up. Re-run only when the hub network topology or the H-series policy set changes.

## What this root creates

- Hub resource group.
- Hub VNet with subnets: `AzureFirewallSubnet`, `AzureBastionSubnet`, `snet-apim` (delegated to `Microsoft.Web/hostingEnvironments` for APIM Standard v2), `snet-pe` (for private endpoints).
- **Azure Firewall Standard** with a policy that allows HTTPS egress only to the FQDNs in `provider_fqdn_allowlist` (Gemini and Groq by default).
- **Azure Bastion Standard** — the only interactive path into hub or spoke private resources.
- Private DNS zones for `azure-api.net`, `openai.azure.com`, `cognitiveservices.azure.com`, `services.ai.azure.com`, and `vaultcore.azure.net`, each linked to the hub VNet only. The spoke VNet link is added later by `network-registration`.
- A **reference route table** with `0.0.0.0/0 → Firewall private IP`, published for parity with same-subscription topologies. When hub and spoke live in **different subscriptions** (the demo default), route tables cannot be attached cross-sub, so `02-spoke-base` creates its own local RT seeded by the hub firewall's private IP output instead of consuming this one.
- Platform **Log Analytics** workspace.
- **H1–H4 Azure Policy** assignments at the hub resource group scope. Details below.

## H-series Azure Policy — what each one blocks

All four are assigned to the hub resource group with effect `Deny` (see `policy_effect_hub` in `variables.tf`; switch to `Audit` for a soft launch). Effect at RG scope in this sample is a demo convenience — production landing zones assign the same policies at management-group or subscription scope so LoB teams cannot lift them.

### H1 — Approved models only

- **Built-in policy** `Foundry model deployments should only use approved models`.
- **What it denies.** Any write to `Microsoft.CognitiveServices.Data/accounts/deployments` whose `model.publisher` is not in `approved_model_publishers` **and** whose `model.assetId` does not match any entry in `approved_model_asset_ids`. Defaults allow the `Microsoft` publisher (which covers `gpt-4.1` and other Azure OpenAI models); asset ids are empty.
- **Why it matters.** RBAC alone lets a Contributor deploy any model the account family supports. H1 pins the platform team's curated catalog by publisher and (optionally) exact asset id. Spokes get the same policy with **both** allow-lists empty (see S1) so LoB teams cannot deploy models at all — they must consume the hub gateway.

### H2 — Approved deployment SKUs

- **Custom policy** (shape from [Restrict deployment types with Azure Policy](https://learn.microsoft.com/azure/foundry/foundry-models/concepts/deployment-types#restrict-deployment-types-with-azure-policy)).
- **What it denies.** Any deployment whose `sku.name` is not in `approved_deployment_skus` (default: `GlobalStandard`, `DataZoneStandard`).
- **Why it matters.** Blocks accidental provisioning of expensive or preview SKUs — Provisioned Throughput Units (PTU), Batch, DeveloperTier — that RBAC has no concept of. Keeps commercial posture (pay-as-you-go vs. reserved capacity) inside the platform's cost model.

### H3 — Public network access must be Disabled

- **Custom policy.**
- **What it denies.** Any create/update of `Microsoft.CognitiveServices/accounts` whose `properties.publicNetworkAccess` is not `Disabled`.
- **Why it matters.** RBAC does not protect the network path — a Contributor could re-enable public access on a private account. H3 keeps every Foundry account in the hub reachable **only** through its private endpoint into `snet-pe`. If a subsequent apply of `04-hub-workload` regresses the setting, ARM rejects the request before the resource changes state.

### H4 — Disable local (key) authentication

- **Custom policy.**
- **What it denies.** Any create/update of `Microsoft.CognitiveServices/accounts` where `properties.disableLocalAuth` is not `true`.
- **Why it matters.** With local auth enabled, anyone holding an account API key bypasses Entra, RBAC, and conditional access. H4 forces every caller — APIM in `04-hub-workload`, an operator on Bastion, a rogue script — to authenticate as an Entra principal that shows up in sign-in logs and is governed by RBAC + conditional access.

### How the four combine

An attempted misconfiguration is denied by whichever policy fires first, so the demo failure message points at exactly one policy — useful when walking through the guardrail live. Example failures:

| Attempted action | Denied by |
|---|---|
| Deploy a model whose publisher is `Meta` (not in `approved_model_publishers`) | H1 |
| Deploy `gpt-4.1` on `ProvisionedManaged` | H2 |
| Recreate Foundry account with `publicNetworkAccess = Enabled` | H3 |
| Recreate Foundry account with local auth left on | H4 |

## What this root does NOT create

- APIM, Foundry account, Key Vault, model deployments — those live in `hub-workload` (step 2).
- VNet peering, DNS zone links to spoke — those live in `network-registration` (step 1c).
- Anything in the spoke subscription.

## Apply

```pwsh
Copy-Item terraform.tfvars.example terraform.tfvars
# edit terraform.tfvars

terraform init
terraform plan
terraform apply
```

## Outputs

Consumed by:

- `hub-workload` — RG, subnets, DNS zone ids, LAW id.
- `network-registration` — VNet id, DNS zone ids.
- `spoke-base` — **hub firewall private IP** (seeds the spoke's local forced-tunnel route). The `spoke_forced_tunnel_route_table_id` output is retained for same-subscription topologies but is unused when hub and spoke are in different subscriptions.
