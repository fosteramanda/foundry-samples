# spoke-workload/project — step 3

The LoB app team runs this after `02-spoke-base` and `03-network-registration` are up.

## Prereqs

- `../02-spoke-base/` applied (VNet, PE subnet, LAW).
- `../03-network-registration/` applied (peering + DNS zone links so the private endpoints resolve).
- `../contracts/hub-workload.contract.json` exists — published by `04-hub-workload`.

## What this root creates

- Foundry **agent** account with `publicNetworkAccess = Disabled`, `disableLocalAuth = true`, `allowProjectManagement = true`.
- Private endpoint for the account in the spoke PE subnet, with a **`private_dns_zone_group`** referencing the three Foundry zones owned by `01-hub-base` (`services.ai.azure.com`, `openai.azure.com`, `cognitiveservices.azure.com`). Terraform reads the zone IDs from the hub base state and passes them to the zone group — the zone group is a child of the PE in the spoke subscription but references zones in the hub subscription. Without the zone group, Azure creates the PE but never populates the A records, and the `<account>.services.ai.azure.com` FQDN returns NXDOMAIN.
- One project with a system-assigned MI. **Its `principal_id` is the value that goes into `hub-allowlist/allowlist.auto.tfvars` in the step-4 PR.**
- Capability host on the project (`kind = Agents`) — enables the Foundry data-proxy layer required for private egress from prompt/tool calls.
- `ApiManagement` connection on the project targeting the platform's gateway URL from the contract. `authType = ProjectManagedIdentity`; `audience` matches the contract; `deploymentInPath = true` puts the alias in the request path. The connection `metadata.models` array uses the Foundry canonical shape `{ name, properties.model = { name, version, format } }` where the inner `name` **equals the alias** (not the upstream model) because APIM routes on alias and the alias is what appears in the request URL.

## What this root does NOT create

- The agent definition itself (that's `../agent/`).
- Any AGW listener/backend (that's `../agent/`).
- Any model deployment (all inference is proxied via the gateway).
- Any storage / Cosmos / AI Search — production capability hosts wire these up for thread persistence and vector search; out of scope for this sample.

## Apply

```pwsh
Copy-Item terraform.tfvars.example terraform.tfvars
terraform init
terraform plan
terraform apply
```

## Step-4 handoff

After apply:

```pwsh
terraform output -raw project_managed_identity_object_id
```

Paste that OID into a PR that adds one line to `../06-hub-allowlist/allowlist.auto.tfvars`. Reviewer approval + merge is step 5. Platform admin re-applying `06-hub-allowlist` is step 6.
