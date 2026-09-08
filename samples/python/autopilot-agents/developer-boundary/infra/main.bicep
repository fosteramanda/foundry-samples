targetScope = 'subscription'

@minLength(1)
@description('Name of the azd environment.')
param environmentName string

@minLength(1)
@description('Azure region for the resource group and Foundry resources.')
param location string

@minLength(1)
@maxLength(90)
@description('Name of the resource group to create or reuse.')
param resourceGroupName string

@description('Whether the resource group already exists and must not be modified.')
param resourceGroupExists bool

@minLength(2)
@maxLength(64)
@description('Globally unique name of the Microsoft Foundry resource.')
param foundryResourceName string

@description('Whether the Foundry resource already exists and must not be modified.')
param foundryResourceExists bool

@minLength(3)
@maxLength(32)
@description('Name of the Microsoft Foundry project.')
param foundryProjectName string

@description('Whether the Foundry project already exists and must not be modified.')
param foundryProjectExists bool

@minLength(1)
@description('Name of the model deployment used by the agent.')
param modelDeploymentName string

@description('Whether the model deployment already exists and must not be modified.')
param modelDeploymentExists bool

@minLength(1)
@description('Model catalog name.')
param modelName string

@minLength(1)
@description('Model provider format from the Foundry catalog.')
param modelFormat string

@minLength(1)
@description('Model version from the Foundry catalog.')
param modelVersion string

@minLength(1)
@description('Model deployment SKU supported by the selected model and region.')
param modelSkuName string

@minValue(1)
@description('Capacity assigned to the model deployment.')
param modelCapacity int

@minLength(36)
@maxLength(36)
@description('Microsoft Entra object ID of the developer who will deploy and manage the hosted agent.')
param developerPrincipalId string

resource resourceGroupDeployment 'Microsoft.Resources/resourceGroups@2024-11-01' = if (!resourceGroupExists) {
  name: resourceGroupName
  location: location
  tags: {
    'azd-env-name': environmentName
  }
}

module foundry 'modules/foundry.bicep' = {
  name: 'foundry'
  scope: az.resourceGroup(resourceGroupName)
  params: {
    location: location
    environmentName: environmentName
    foundryResourceName: foundryResourceName
    foundryResourceExists: foundryResourceExists
    foundryProjectName: foundryProjectName
    foundryProjectExists: foundryProjectExists
    modelDeploymentName: modelDeploymentName
    modelDeploymentExists: modelDeploymentExists
    modelName: modelName
    modelFormat: modelFormat
    modelVersion: modelVersion
    modelSkuName: modelSkuName
    modelCapacity: modelCapacity
    developerPrincipalId: developerPrincipalId
  }
  dependsOn: [
    resourceGroupDeployment
  ]
}

output AZURE_RESOURCE_GROUP string = resourceGroupName
output AZURE_LOCATION string = location
output AZURE_SUBSCRIPTION_ID string = subscription().subscriptionId
output AZURE_AI_ACCOUNT_NAME string = foundry.outputs.AZURE_AI_ACCOUNT_NAME
output AZURE_AI_PROJECT_ID string = foundry.outputs.AZURE_AI_PROJECT_ID
output AZURE_AI_PROJECT_NAME string = foundry.outputs.AZURE_AI_PROJECT_NAME
output FOUNDRY_PROJECT_ENDPOINT string = foundry.outputs.FOUNDRY_PROJECT_ENDPOINT
output AZURE_AI_MODEL_DEPLOYMENT_NAME string = modelDeploymentName
output AZURE_AI_AGENT_NAME string = 'developer-boundary'
