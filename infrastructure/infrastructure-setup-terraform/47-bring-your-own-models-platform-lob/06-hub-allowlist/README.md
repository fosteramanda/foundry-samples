# hub-allowlist — step 6

Platform admin re-runs this after every merge of an allowlist PR (step 5).
This is the smallest and most frequently applied root in the sample.

## Prereqs

- `../04-hub-workload/` has been applied and `../04-hub-workload/terraform.tfstate` exists.
- A PR that added one or more OIDs to `allowlist.auto.tfvars` has been merged.

## What this root creates

- Exactly one resource: `azurerm_api_management_api_policy.inference`.
- Renders `apim-policy.xml.tftpl` with:
  - The populated `project_oid_allowlist` from `allowlist.auto.tfvars`.
  - Catalog routing inputs (foundry / gemini / groq upstreams) pulled from `hub-workload` outputs.
  - The token quota published by `hub-workload`.

## What this root does NOT touch

- APIM itself, its identity, or its named values — those live in `hub-workload`.
- The Foundry account, model deployments, Key Vault, or any spoke resource.

## Apply

```pwsh
Copy-Item terraform.tfvars.example terraform.tfvars           # subscription / tenant
Copy-Item allowlist.auto.tfvars.example allowlist.auto.tfvars # then edit OIDs

terraform init
terraform plan
terraform apply
```

## Contract with the PR

The step-4 PR modifies **only `allowlist.auto.tfvars`**. Reviewers should reject PRs that touch any other file in this root; the safety story depends on that.
