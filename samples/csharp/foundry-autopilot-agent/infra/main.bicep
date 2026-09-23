targetScope = 'resourceGroup'

// =================================================================================================
// Main parameters
// =================================================================================================

@minLength(1)
@maxLength(64)
@description('Name of the application. Used to ensure resource names are unique.')
param environmentName string

@minLength(1)
@description('Primary location for all resources')
param location string

// =================================================================================================
// Project module parameters
// =================================================================================================

@description('Name of the Cognitive Services account')
param accountName string = '${environmentName}acct'

@description('Name of the Cognitive Services project')
param projectName string = '${environmentName}proj'

@description('Name of the Container Registry')
param containerRegistryName string = '${environmentName}acr'

@description('SKU of Cognitive Services account')
param cognitiveServicesSku string = 'S0'

@description('SKU of Container Registry')
@allowed(['Basic', 'Standard', 'Premium'])
param containerRegistrySku string = 'Basic'

param agentName string = '${environmentName}-autopilot-agent'

// =================================================================================================
// Bot Service module parameters
// =================================================================================================

@description('Model name')
param modelName string = 'gpt-chat-latest'

@description('Model version')
param modelVersion string = '2026-05-28'

@description('Enable monitoring via Application Insights and Log Analytics')
param enableMonitoring bool = true

@description('Name of the Log Analytics workspace')
param logAnalyticsName string = '${environmentName}-logs'

@description('Name of the Application Insights instance')
param applicationInsightsName string = '${environmentName}-appi'

// =================================================================================================
// Common parameters
// =================================================================================================

@description('Tags to apply to all resources')
param tags object = {}

// =================================================================================================
// Module deployments
// =================================================================================================

// 1. Deploy the project module (Cognitive Services account, project, and Container Registry)
module project 'modules/project.bicep' = {
  name: 'project-deployment'
  params: {
    accountName: accountName
    projectName: projectName
    containerRegistryName: containerRegistryName
    location: location
    tags: tags
    cognitiveServicesSku: cognitiveServicesSku
    containerRegistrySku: containerRegistrySku
    modelName: modelName
    modelVersion: modelVersion
    enableMonitoring: enableMonitoring
    logAnalyticsName: logAnalyticsName
    applicationInsightsName: applicationInsightsName
  }
}


// =================================================================================================
// Outputs - These become environment variables in post-provision.sh
// =================================================================================================

@description('ACR login server endpoint')
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = project.outputs.acrloginServer

output AZURE_AI_PROJECT_ENDPOINT string = project.outputs.foundryProjectEndpoint

output SUBSCRIPTION_ID string = subscription().subscriptionId

output RESOURCE_GROUP string = resourceGroup().name

output LOCATION string = location

output ACCOUNT_NAME string = accountName

output PROJECT_NAME string = projectName

output AGENT_NAME string = agentName

output TENANT_ID string = tenant().tenantId

output PROJECT_PRINCIPAL_ID string = project.outputs.foundryProjectPrincipalId

output MODEL_NAME string = modelName

@description('Application Insights connection string (empty when monitoring is disabled)')
output APPLICATIONINSIGHTS_CONNECTION_STRING string = project.outputs.applicationInsightsConnectionString

@description('Application Insights resource ID (empty when monitoring is disabled)')
output APPLICATIONINSIGHTS_RESOURCE_ID string = project.outputs.applicationInsightsResourceId
