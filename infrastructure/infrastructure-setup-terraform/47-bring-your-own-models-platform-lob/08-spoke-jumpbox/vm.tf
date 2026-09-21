resource "tls_private_key" "jumpbox" {
  algorithm = "RSA"
  rsa_bits  = 4096
}

resource "local_sensitive_file" "ssh_private_key" {
  content         = tls_private_key.jumpbox.private_key_pem
  filename        = local.ssh_priv_path
  file_permission = "0600"

  # Windows: file_permission is ignored; enforce restrictive ACL via icacls
  # so OpenSSH accepts the key.
  provisioner "local-exec" {
    interpreter = ["PowerShell", "-Command"]
    command     = <<-EOT
      if ($IsWindows -or $env:OS -eq "Windows_NT") {
        icacls "${local.ssh_priv_path}" /reset | Out-Null
        icacls "${local.ssh_priv_path}" /inheritance:r | Out-Null
        icacls "${local.ssh_priv_path}" /grant:r "$${env:USERNAME}:(R)" | Out-Null
      }
    EOT
  }
}

resource "local_file" "ssh_public_key" {
  content         = tls_private_key.jumpbox.public_key_openssh
  filename        = local.ssh_pub_path
  file_permission = "0644"
}

resource "azurerm_network_interface" "jumpbox" {
  name                = local.nic_name
  location            = local.spoke_location
  resource_group_name = local.spoke_rg_name
  tags                = local.common_tags

  ip_configuration {
    name                          = "internal"
    subnet_id                     = azurerm_subnet.jumpbox.id
    private_ip_address_allocation = "Dynamic"
  }
}

resource "azurerm_linux_virtual_machine" "jumpbox" {
  name                            = local.vm_name
  resource_group_name             = local.spoke_rg_name
  location                        = local.spoke_location
  size                            = var.vm_size
  admin_username                  = var.vm_admin_username
  disable_password_authentication = true
  network_interface_ids           = [azurerm_network_interface.jumpbox.id]
  tags                            = local.common_tags

  admin_ssh_key {
    username   = var.vm_admin_username
    public_key = tls_private_key.jumpbox.public_key_openssh
  }

  os_disk {
    caching              = "ReadWrite"
    storage_account_type = "Standard_LRS"
  }

  source_image_reference {
    publisher = "Canonical"
    offer     = "0001-com-ubuntu-server-jammy"
    sku       = "22_04-lts-gen2"
    version   = "latest"
  }

  identity {
    type = "SystemAssigned"
  }

  # Install curl, jq, az cli, python, jupyter dependencies at first boot.
  custom_data = base64encode(<<-CLOUDINIT
    #cloud-config
    package_update: true
    package_upgrade: false
    packages:
      - curl
      - jq
      - ca-certificates
      - apt-transport-https
      - lsb-release
      - gnupg
      - python3
      - python3-pip
    runcmd:
      - curl -sL https://aka.ms/InstallAzureCLIDeb | bash
      - su - ${var.vm_admin_username} -c "pip3 install --user jupyter"
    CLOUDINIT
  )
}
