# spoke-base — step 1b

Platform admin runs this **once per LoB** (per environment). Re-run only when the LoB network topology or the S-series policy set changes.

## Prereqs

- `../01-hub-base/` has been applied (state file read for the hub Firewall private IP that seeds the local forced-tunnel route).

## What this root creates

- LoB resource group.
- Spoke VNet with subnets: `snet-appgw`, `snet-workload`, `snet-pe`.
- **Application Gateway WAF v2** shell — the only public surface. spoke-workload/agent adds real listeners; the AGW created here has a placeholder HTTP listener so it starts healthy.
- Static public IP for AGW.
- WAF v2 policy in Prevention mode with OWASP 3.2.
- NSGs on each subnet — internet is denied on non-AGW subnets.
- Route table (spoke-owned): default route `0.0.0.0/0` → `VirtualAppliance` next-hop = hub Firewall private IP (read from `01-hub-base` state). Associated to `snet-workload` and `snet-pe`. The RT is created **in the spoke subscription** because route tables cannot be referenced across subscriptions; the hub↔spoke peering carries the actual traffic to the firewall.
- LoB Log Analytics workspace.
- **S1, S3, S5 Azure Policy** assignments at the spoke resource group scope (S2 is deferred — see below; S4 lands in step 3 with the Foundry project; S6–S9 are planned built-ins).

## What this root does NOT create

- Foundry agent account, project, MI, capability host, ApiManagement connection — those live in `spoke-workload/project` (step 3).
- Agent definition, real AGW backend pool + listener — those live in `spoke-workload/agent` (step 7).
- VNet peering, DNS zone links to spoke — those live in `network-registration` (step 1c).

## S2 is deferred

The intended S2 policy would force `properties.instant.modelAllowList = []` on every Foundry account, per the [instant-models enterprise controls](https://learn.microsoft.com/azure/foundry/concepts/instant-models#enterprise-controls). As of authoring, the ARM alias `Microsoft.CognitiveServices/accounts/instant.modelAllowList` is not yet registered, so the Modify-effect policy fails at definition time with `InvalidPolicyAlias`. S1's empty `allowedPublishers` + `allowedAssetIds` lists already deny every spoke model deployment, which achieves the practical outcome until the alias ships.

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

- `network-registration` — spoke VNet id, RG, location.
- `spoke-workload/project` — RG, PE subnet id, LAW id.
- `spoke-workload/agent` — AGW id, workload subnet, LAW id.
