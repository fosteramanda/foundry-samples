targetScope = 'resourceGroup'

@description('Whether to create a Key Vault in this resource group.')
param enabled bool = true

@description('Name of the Key Vault to create.')
param keyVaultName string

@description('Azure region for the Key Vault.')
param location string = resourceGroup().location

resource keyVault 'Microsoft.KeyVault/vaults@2024-11-01' = if (enabled) {
  name: keyVaultName
  location: location
  properties: {
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    accessPolicies: []
    enableRbacAuthorization: true
    enablePurgeProtection: false
    softDeleteRetentionInDays: 90
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Deny'
      ipRules: []
      virtualNetworkRules: []
    }
  }
}

output keyVaultName string = enabled ? keyVault.name : ''
output keyVaultResourceId string = enabled ? keyVault.id : ''
