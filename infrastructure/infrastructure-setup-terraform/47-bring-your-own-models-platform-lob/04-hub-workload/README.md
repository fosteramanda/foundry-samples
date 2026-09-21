# hub-workload — step 2

Platform admin runs this **after** hub-base is up. Re-runs when the model catalog changes (new model published, model retired, provider key rotated, or APIM shell reconfigured).

## Prereqs

- `../01-hub-base/` has been applied and `../01-hub-base/terraform.tfstate` exists (consumed via `terraform_remote_state`).

## What this root creates

- Foundry model account (PNA=Disabled, disableLocalAuth=true) + private endpoint in `snet-pe`.
- Foundry model deployment (`gpt-4.1` on `GlobalStandard` — matches H2 allowlist).
- Key Vault with `public_network_access = Disabled` + private endpoint. Two secrets: `gemini-api-key`, `groq-api-key`.
- APIM Standard v2 with VNet integration into `snet-apim` (outbound to Foundry PE) and a private endpoint in `snet-pe` (inbound from spokes).
- APIM `Cognitive Services User` role on the Foundry model account (so the `authentication-managed-identity` policy line works).
- App Insights + APIM logger routed to the platform LAW created by hub-base.
- APIM inference **API shell** + diagnostic + Gemini/Groq named values reading from Key Vault.
- **`../contracts/hub-workload.contract.json`** — the platform contract consumed by spoke-workload/project.

## What this root does NOT create

- Hub networking, Firewall, Bastion, private DNS zones — those live in `hub-base` (step 1a).
- **The APIM inference-API policy** — that lives in `hub-allowlist` (step 6). Between step 2 and the first step-6 apply, the API responds to any request with a passthrough; no spoke exists during that window, so nothing can call it via the private endpoint anyway.
- Any spoke resource.

## Notes for operators

### APIM `publicNetworkAccess` — v1 keeps this **Enabled**

The MS-managed Foundry Agents runtime (`POST {project}/openai/v1/responses`) executes on public compute and needs a network path to the connection target it reads from the project's `ApiManagement` connection. In v1 the sample keeps APIM `publicNetworkAccess = Enabled` and pins the target hostname to `<apim>.azure-api.net`.

- The managed TLS cert on APIM StandardV2 covers `<name>.azure-api.net` **only** — it is not a SAN for `<name>.privatelink.azure-api.net`. Using the privatelink hostname as the connection target causes the runtime to fail with `SSL connection could not be established`.
- Inside-VNet callers (jumpbox, other Azure resources with peering + the linked `privatelink.azure-api.net` zone) resolve the same `.azure-api.net` FQDN to APIM's private endpoint IP via the DNS CNAME chain, so the path stays private from within the VNet.
- Authorization is enforced by the `validate-azure-ad-token` policy authored in `06-hub-allowlist`: tenant, audience (`https://cognitiveservices.azure.com`), and the `oid` allowlist. Optionally layer an inbound APIM policy IP restriction to the `AzureCloud.<region>` service tag.

Follow-up path to a fully private posture (**deferred**): enable Foundry Standard Agent Setup with `networkInjections` on the account so the runtime executes inside a delegated spoke subnet. Once that lands, this root can flip APIM back to `publicNetworkAccess = Disabled`.

### Key Vault also uses two-phase `publicNetworkAccess`

Same shape as APIM, for the same reason — Terraform writes secrets over the Key Vault **data plane**, and if it runs from a public laptop / build agent, `publicNetworkAccess = Disabled` blocks it with `ForbiddenByConnection`. This root:

1. Creates the vault with `public_network_access_enabled = true` and `network_acls.default_action = "Allow"` so the deployer's data-plane calls succeed.
2. Writes the two provider secrets (`gemini-api-key`, `groq-api-key`).
3. `azapi_update_resource.kv_disable_public_access` PATCHes both `publicNetworkAccess = "Disabled"` and `networkAcls.defaultAction = "Deny"` after the secrets exist.

`lifecycle.ignore_changes` on the vault covers both fields so subsequent plans stay clean.

**Rotating a secret** with this pattern requires either (a) temporarily re-enabling public access on the vault before running Terraform, then relying on the azapi patch to close it again, or (b) running Terraform from a jumpbox that has a route to the private endpoint (Bastion in hub-base is the intended path for production rotations).

### `azapi` provider needs an ARM-scoped token

`azapi_resource.foundry_model_account` uses the azapi provider, which authenticates through Azure Identity's chained credential. Plain `az login` in a tenant with Conditional Access / MFA may not produce a management-plane token azapi will accept, and you'll see `AADSTS50076` at plan/apply time.

Refresh with the ARM scope before running Terraform:

```pwsh
az login --scope https://management.core.windows.net//.default
```

### Expected apply time

~30–45 minutes end-to-end. APIM StandardV2 provisioning dominates (~25–35 min). Foundry account + PE is ~10 min and runs in parallel. Key Vault + PE + secrets is ~2 min.

## Apply

```pwsh
Copy-Item terraform.tfvars.example terraform.tfvars
# edit terraform.tfvars — fill in provider API keys

terraform init
terraform plan
terraform apply
```

## Outputs

Consumed by:

- `spoke-workload/project` (via `../contracts/hub-workload.contract.json`) — private gateway URL, audience, catalog aliases.
- `hub-allowlist` (via `terraform_remote_state`) — APIM name, RG, API name, tenant, audience, policy render inputs, quota.
