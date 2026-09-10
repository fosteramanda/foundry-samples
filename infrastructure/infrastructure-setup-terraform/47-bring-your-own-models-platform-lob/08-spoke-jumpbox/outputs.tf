output "jumpbox_vm_name" {
  value       = azurerm_linux_virtual_machine.jumpbox.name
  description = "Jumpbox VM name — use with Bastion tunneling."
}

output "jumpbox_vm_id" {
  value       = azurerm_linux_virtual_machine.jumpbox.id
  description = "Full ARM id — needed by az network bastion ssh --target-resource-id."
}

output "jumpbox_admin_username" {
  value       = var.vm_admin_username
  description = "Admin username on the VM."
}

output "bastion_name" {
  value       = azurerm_bastion_host.spoke.name
  description = "Bastion host in the spoke VNet."
}

output "bastion_resource_group_name" {
  value       = local.spoke_rg_name
  description = "Resource group where the Bastion lives."
}

output "ssh_private_key_path" {
  value       = local.ssh_priv_path
  description = "Local file holding the generated PEM-encoded private key. Passed to Bastion via --ssh-key."
}

output "connect_command" {
  description = "Copy-paste command to SSH via Bastion. Requires the 'bastion' extension for az."
  value = format(
    "az network bastion ssh --name %s --resource-group %s --target-resource-id %s --auth-type ssh-key --username %s --ssh-key %s",
    azurerm_bastion_host.spoke.name,
    local.spoke_rg_name,
    azurerm_linux_virtual_machine.jumpbox.id,
    var.vm_admin_username,
    local.ssh_priv_path,
  )
}
