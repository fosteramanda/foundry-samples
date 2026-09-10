# network-registration — step 1c

Platform admin runs this **once per (hub, spoke) pair** after both base roots are up. Re-runs only when a new spoke is added or an address space changes.

## Prereqs

- `../01-hub-base/terraform.tfstate` exists.
- `../02-spoke-base/terraform.tfstate` exists.

## What this root creates

- **VNet peering pair** — hub→spoke and spoke→hub.
- **Private DNS zone links** attaching every hub-owned private DNS zone to the spoke VNet. This is what lets a spoke agent resolve `<apim>.privatelink.azure-api.net` to the APIM private endpoint IP in the hub subscription.

## Why this lives in its own root

Peering pairs cross subscriptions. Neither `hub-base` nor `spoke-base` has legal read access to the other side's state, and neither should. `network-registration` is the only root that talks to both, and it owns nothing but the handshake.

## Apply

```pwsh
Copy-Item terraform.tfvars.example terraform.tfvars
# edit terraform.tfvars

terraform init
terraform plan
terraform apply
```

## Outputs

None consumed by downstream roots — this root is purely a bridge.
