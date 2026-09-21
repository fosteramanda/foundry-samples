# spoke-workload/agent — step 7

LoB app team runs this after step 6 (allowlist merged and applied by the platform team). This is the last **Terraform** root before end users can invoke the agent; the actual agent resource is created at runtime by the runbook — see `demo.ipynb`.

## Prereqs

- `../05-spoke-workload-project/` applied (Foundry account + project + capability host + APIM connection).
- `../06-hub-allowlist/` applied with this project's MI OID (step 5 + step 6).
- `../02-spoke-base/` applied (AGW shell + public IP).

## What this root creates

- **AGW backend wiring** patched onto the existing AGW via `azapi_update_resource`:
  - Frontend port `80`
  - Backend pool pointing at the Foundry agent account's `services.ai.azure.com` FQDN
  - HTTPS backend probe/settings
  - HTTP listener with a host header
  - Basic routing rule joining the three

The AGW resource itself is owned by `spoke-base`; that root sets `lifecycle.ignore_changes` on backend/listener/rule/probe/settings so this `azapi_update_resource` patch does not fight it.

## What this root does NOT create

- **The agent itself.** Foundry agents live on the project **data plane**, not on ARM — there is no `Microsoft.CognitiveServices/accounts/projects/agents` resource type. Attempting `azapi_resource` against it returns `UnsupportedAction`. Instead, the agent is created at runtime via a `POST {project}/agents?api-version=v1` with a `definition.kind = "prompt"` body. See `demo.ipynb` for the exact `curl` calls.
- **The runtime invocation.** Threads/runs are the older Assistants shape and are not used here; invocation goes via `POST {project}/openai/v1/responses` with an `agent_reference`. All data plane. See `demo.ipynb`.

The `agent_name` variable in this root is used only to derive AGW child names (backend pool, listener, rule, probe). It must match the name used in `demo.ipynb` when the agent is actually created, so the AGW listener / host-header story lines up if you later bind it to a custom domain.

## Apply

```pwsh
Copy-Item terraform.tfvars.example terraform.tfvars
terraform init
terraform plan
terraform apply
```

## After Terraform — create and invoke the agent

Open `demo.ipynb` on a jumpbox VM inside the spoke VNet (via Bastion — see `../08-spoke-jumpbox/`). It walks through:

1. Acquiring a data-plane token with audience `https://ai.azure.com`:  
   `az account get-access-token --resource "https://ai.azure.com"`.
2. `POST {project}/agents?api-version=v1` with body  
   `{ "name": "hr-assistant", "definition": { "kind": "prompt", "model": "gateway/gpt-4.1", "instructions": "..." } }` — creates a prompt agent bound to the `gateway` connection + `gpt-4.1` alias. Note the API family is Foundry Agents v1 (`/agents`), **not** the older Assistants (`/assistants`) shape.
3. `POST {project}/openai/v1/responses` with `{ "agent_reference": { "type": "agent_reference", "name": "hr-assistant" }, "input": [...] }` — invokes the agent end-to-end and proves the model call routes through APIM.
4. Verifying APIM's `x-aigw-tokens-*` quota headers.
5. Negative tests: 401 (bad `oid`) and 404 (unknown alias).

Print only the assistant text with:

```bash
jq -r '.output[0].content[0].text'
```

Why a jumpbox? The spoke Foundry account has `publicNetworkAccess = Disabled` (policy S3 enforces it), so the data-plane hostname only resolves and connects from a network that has the private DNS links and peering — see step 3, `03-network-registration/`.

## Step-8 verify (public path)

```pwsh
$url = terraform output -raw public_endpoint_url
curl.exe -v $url
```

The demo listener uses HTTP:80 with a host header of the AGW's default `*.cloudapp.azure.com` FQDN so no custom DNS / TLS cert is required. In production, replace with an HTTPS listener bound to a Key-Vault-referenced cert and a custom hostname.
