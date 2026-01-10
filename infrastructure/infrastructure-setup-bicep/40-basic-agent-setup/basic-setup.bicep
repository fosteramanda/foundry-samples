param aiServicesName string = 'foundy'
param projectName string = 'project'
param projectDescription string = 'some description'
param projectDisplayName string = 'project_display_name'
param deploymentTimestamp string = utcNow('yyyyMMddHHmmss')
param location string = 'westus'
param modelName string = 'gpt-5.2'
param modelFormat string = 'OpenAI'
param modelVersion string = '2025-12-11'
param modelSkuName string = 'GlobalStandard'
param modelCapacity int = 30

var uniqueSuffix = substring(uniqueString(format('{0}-{1}', resourceGroup().id, deploymentTimestamp)), 0, 4)
var accountName = toLower(format('{0}{1}', aiServicesName, uniqueSuffix))

resource account 'Microsoft.CognitiveServices/accounts@2025-04-01-preview' = {
  name: accountName
  location: location
  sku: {
    name: 'S0'
  }
  kind: 'AIServices'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    allowProjectManagement: true
    customSubDomainName: toLower(accountName)
    networkAcls: {
      defaultAction: 'Allow'
      virtualNetworkRules: []
      ipRules: []
    }
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
  }
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-04-01-preview' = {
  name: '${accountName}/${projectName}'
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    description: projectDescription
    displayName: projectDisplayName
  }
  dependsOn: [ account ]
}

resource deployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  name: '${accountName}/${modelName}'
  sku: {
    capacity: modelCapacity
    name: modelSkuName
  }
  properties: {
    model: {
      name: modelName
      format: modelFormat
      version: modelVersion
    }
  }
  dependsOn: [ account ]
}

output accountName string = accountName
output projectName string = projectName
output accountEndpoint string = reference(resourceId('Microsoft.CognitiveServices/accounts', accountName), '2025-04-01-preview').endpoint
