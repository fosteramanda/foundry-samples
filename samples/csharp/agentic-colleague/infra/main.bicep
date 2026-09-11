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

@description('Name of the model to deploy. Also the deployment name, so appsettings ModelDeployment must match.')
param modelName string = 'gpt-5.6-sol'

@description('Version of the model to deploy')
param modelVersion string = '2026-07-09'

@description('GlobalStandard capacity (1000 TPM per unit) for the model deployment')
param modelCapacity int = 100

@description('Enable monitoring via Application Insights and Log Analytics')
param enableMonitoring bool = true

@description('Name of the Log Analytics workspace')
param logAnalyticsName string = '${environmentName}-logs'

@description('Name of the Application Insights instance')
param applicationInsightsName string = '${environmentName}-appi'

param agentName string = '${environmentName}-agent'

// =================================================================================================
// Azure Table Storage parameters
// =================================================================================================

@description('Storage account used for agent table data (allowlist and work items)')
param storageAccountName string = take(toLower(replace('${environmentName}storage', '-', '')), 24)

@description('Table name used for direct-message allowlist data')
param directMessageAllowListTableName string = 'digitalworkerallowlist'

@description('Table name used for work items data')
param workItemsTableName string = 'workitems'

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
  name: 'project1-deployment'
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
    modelCapacity: modelCapacity
    enableMonitoring: enableMonitoring
    logAnalyticsName: logAnalyticsName
    applicationInsightsName: applicationInsightsName
  }
}

// 2. Deploy Azure Table Storage for agent data (allowlist + work items).
//
// There is deliberately no bot service and no blueprint here. The agent endpoint is
// authorized with the BotServiceRbac scheme instead of a Bot Service resource, and the
// blueprint is created by the platform when the agent version is created, which is where
// its client id is returned. Creating one here would produce a second, unused blueprint.
module tables 'modules/tables.bicep' = {
  name: 'tables-deployment'
  params: {
    storageAccountName: storageAccountName
    tableNames: [
      directMessageAllowListTableName
      workItemsTableName
    ]
    location: location
    tags: tags
  }
}

// =================================================================================================
// Outputs - These become environment variables in post-provision.sh
// =================================================================================================

@description('ACR login server endpoint')
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = project.outputs.acrloginServer

output AZURE_AI_PROJECT_ENDPOINT string = project.outputs.foundryProjectEndpoint

output SUBSCRIPTION_ID string = subscription().subscriptionId

@description('Resource group name. Needed by agent-creation-script.ps1 to build the role assignment scope.')
output RESOURCE_GROUP string = resourceGroup().name

output LOCATION string = location

output ACCOUNT_NAME string = accountName

output PROJECT_NAME string = projectName

output AGENT_NAME string = agentName

output TENANT_ID string = tenant().tenantId

output PROJECT_PRINCIPAL_ID string = project.outputs.foundryProjectPrincipalId

@description('Model deployment name, consumed by the image build and the agent version.')
output MODEL_NAME string = modelName

output PROJECT_DEFAULT_INSTANCE_CLIENT_ID string = project.outputs.foundryProjectDefaultInstanceClientId

output DIRECT_MESSAGE_ALLOWLIST_TABLE_SERVICE_URI string = tables.outputs.tableServiceUri

output DIRECT_MESSAGE_ALLOWLIST_TABLE_NAME string = directMessageAllowListTableName

output DIRECT_MESSAGE_ALLOWLIST_STORAGE_ACCOUNT_RESOURCE_ID string = tables.outputs.storageAccountResourceId

output WORK_ITEMS_TABLE_SERVICE_URI string = tables.outputs.tableServiceUri

output WORK_ITEMS_TABLE_NAME string = workItemsTableName

output WORK_ITEMS_STORAGE_ACCOUNT_RESOURCE_ID string = tables.outputs.storageAccountResourceId

@description('Application Insights connection string (empty when monitoring is disabled)')
output APPLICATIONINSIGHTS_CONNECTION_STRING string = project.outputs.applicationInsightsConnectionString

@description('Application Insights resource ID (empty when monitoring is disabled)')
output APPLICATIONINSIGHTS_RESOURCE_ID string = project.outputs.applicationInsightsResourceId
