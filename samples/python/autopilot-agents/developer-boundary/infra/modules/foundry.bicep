targetScope = 'resourceGroup'

param location string = resourceGroup().location
param environmentName string
param foundryResourceName string
param foundryResourceExists bool
param foundryProjectName string
param foundryProjectExists bool
param modelDeploymentName string
param modelDeploymentExists bool
param modelName string
param modelFormat string
param modelVersion string
param modelSkuName string
param modelCapacity int
param developerPrincipalId string

var cognitiveServicesUserRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'a97b65f3-24c7-4388-baec-2e87135dc908'
)
var foundryProjectManagerRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'eadc314b-1a2d-4efa-be10-5d325db5065e'
)

resource foundryAccount 'Microsoft.CognitiveServices/accounts@2025-06-01' = if (!foundryResourceExists) {
  name: foundryResourceName
  location: location
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  tags: {
    'azd-env-name': environmentName
  }
  properties: {
    allowProjectManagement: true
    customSubDomainName: foundryResourceName
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource foundryAccountReference 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: foundryResourceName
}

resource modelDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = if (!modelDeploymentExists) {
  parent: foundryAccountReference
  name: modelDeploymentName
  sku: {
    name: modelSkuName
    capacity: modelCapacity
  }
  properties: {
    model: {
      format: modelFormat
      name: modelName
      version: modelVersion
    }
  }
  dependsOn: [
    foundryAccount
  ]
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = if (!foundryProjectExists) {
  parent: foundryAccountReference
  name: foundryProjectName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    displayName: foundryProjectName
    description: 'Project provisioned by the ${environmentName} azd environment.'
  }
  dependsOn: [
    foundryAccount
    modelDeployment
  ]
}

resource projectReference 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' existing = {
  parent: foundryAccountReference
  name: foundryProjectName
}

resource projectModelUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundryAccountReference.id, projectReference.id, cognitiveServicesUserRoleId)
  scope: foundryAccountReference
  properties: {
    principalId: projectReference.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: cognitiveServicesUserRoleId
  }
  dependsOn: [
    project
  ]
}

resource developerProjectManager 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(projectReference.id, developerPrincipalId, foundryProjectManagerRoleId)
  scope: projectReference
  properties: {
    principalId: developerPrincipalId
    principalType: 'User'
    roleDefinitionId: foundryProjectManagerRoleId
  }
  dependsOn: [
    project
  ]
}

output AZURE_AI_ACCOUNT_NAME string = foundryAccountReference.name
output AZURE_AI_PROJECT_ID string = projectReference.id
output AZURE_AI_PROJECT_NAME string = foundryProjectName
output FOUNDRY_PROJECT_ENDPOINT string = 'https://${foundryAccountReference.name}.services.ai.azure.com/api/projects/${foundryProjectName}'
