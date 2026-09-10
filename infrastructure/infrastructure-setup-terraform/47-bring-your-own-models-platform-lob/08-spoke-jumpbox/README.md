# spoke-jumpbox — step 7.5 (runtime demo host)

App team runs this after step 7 to spin up a small Ubuntu jumpbox in the spoke VNet so `demo.ipynb` (agent create + invoke) can actually reach the private data plane.

## Prereqs

- `../02-spoke-base/` applied (spoke VNet).
- `../05-spoke-workload-project/` applied (project id we grant on).
- Enough address space in `10.20.0.0/16` for two new subnets (`AzureBastionSubnet` and `snet-jumpbox`). Defaults use `10.20.3.0/26` + `10.20.4.0/28`.

## What this root creates

- `AzureBastionSubnet` and `snet-jumpbox` on the existing spoke VNet.
- Standard-SKU Azure Bastion + public IP on the spoke, with **`tunneling_enabled = true`** and **`ip_connect_enabled = true`** — both are required for `az network bastion ssh` and `az network bastion tunnel` to work from the CLI. Without `tunneling_enabled`, the CLI errors with `KeyError: 'enableTunneling'`.
- Ubuntu 22.04 **`Standard_D2s_v3`** VM with a fresh RSA 4096 SSH keypair.
  - VM SKU note: swedencentral has 0 default quota for `Standard_DSv5Family`, so this root pins the D-series v3 family which has default 10-core quota. `Standard_B2s`, `Standard_DSv6Family` are also viable.
  - The **private key** is written to `./jumpbox_id_rsa` and a `local-exec` provisioner runs `icacls` to lock the ACLs to the current user (read-only). On Windows, Terraform's `file_permission = "0600"` is ignored — OpenSSH will refuse a key that inherits the default profile ACLs (`bad permissions`).
- System-Assigned managed identity on the VM (unused today; reserved for future scripted flows).
- **`Azure AI Developer` role assignment** on the spoke Foundry project for the human OID you supply (`var.human_object_id`) — required to author and invoke agents from the notebook via interactive `az login`.
- Cloud-init installs `curl`, `jq`, Azure CLI, Python 3, and Jupyter on first boot.

## What this root does NOT create

- Real Bastion tunneling into the VM as anything other than SSH (no RDP, no port forwarding beyond default).
- A separate managed identity for the notebook to use — see `demo.ipynb` §2 which uses the interactive `az login` token, not IMDS.
- Any change to the AGW, project, or APIM. This is pure operator infra.

## Apply

```pwsh
Copy-Item terraform.tfvars.example terraform.tfvars

# Fill in tenant, spoke subscription, and your own signed-in-user OID:
$oid = az ad signed-in-user show --query id -o tsv
Write-Host "Put this in terraform.tfvars: human_object_id = `"$oid`""

terraform init
terraform plan
terraform apply
```

Expect ~5–8 minutes (Bastion Standard SKU dominates).

## Connect via Bastion

```pwsh
terraform output -raw connect_command | Invoke-Expression
```

This uses the `az bastion` extension. First time you may be prompted to install it (`az extension add --name bastion`).

Once inside the VM:

```bash
az login                                  # interactive device-code login
az account set --subscription <spoke sub>
# then either open the notebook remotely:
jupyter notebook --no-browser --port 8888
# or copy the demo cells straight into the shell — they're all curl.
```

To run `demo.ipynb` from Bastion, either:

- Use `az network bastion tunnel --resource-port 8888 --port 8888 ...` alongside SSH to forward the Jupyter port to your laptop, then browse to `http://127.0.0.1:8888`.
- Or SCP `../07-spoke-workload-agent/demo.ipynb` up via Bastion's `az network bastion ssh` support, run Jupyter on the VM, and forward the port.

## Cleanup

Once the demo is done, `terraform destroy` here removes the two subnets, Bastion, PIP, VM, disk, NIC, NSG, RBAC assignment, and the local key files. Nothing this root creates is depended on by any earlier root, so destroy is safe.
