targetScope = 'resourceGroup'

@description('Existing azd environment name.')
param environmentName string

@description('Region used by the existing Foundry deployment.')
param location string

@description('Existing Foundry account name.')
param accountName string

@description('Existing Foundry project name.')
param projectName string

@description('Object ID of the operator who will read the diagnostic traces.')
param operatorPrincipalId string

@description('Daily Log Analytics ingestion cap in GB.')
param dailyQuotaGb string = '1'

var workspaceName = 'log-${environmentName}'
var insightsName = 'appi-${environmentName}'
var readerRoleId = '73c42c96-874c-492b-b04d-ab87d138a893'
var tags = {
  'azd-env-name': environmentName
  purpose: 'foundry-activity-diagnostics'
}

resource account 'Microsoft.CognitiveServices/accounts@2025-09-01' existing = {
  name: accountName
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2026-03-01' existing = {
  parent: account
  name: projectName
}

module workspace 'br/public:avm/res/operational-insights/workspace:0.16.1' = {
  name: 'observability-workspace'
  params: {
    name: workspaceName
    location: location
    enableTelemetry: false
    skuName: 'PerGB2018'
    dataRetention: 30
    dailyQuotaGb: dailyQuotaGb
    forceCmkForQuery: false
    features: {
      disableLocalAuth: true
      enableLogAccessUsingOnlyResourcePermissions: true
    }
    tags: tags
    roleAssignments: [
      {
        principalId: operatorPrincipalId
        principalType: 'User'
        roleDefinitionIdOrName: readerRoleId
      }
      {
        principalId: project.identity.principalId
        principalType: 'ServicePrincipal'
        roleDefinitionIdOrName: readerRoleId
      }
    ]
  }
}

module insights 'br/public:avm/res/insights/component:0.8.0' = {
  name: 'observability-appinsights'
  params: {
    name: insightsName
    location: location
    enableTelemetry: false
    applicationType: 'web'
    kind: 'web'
    workspaceResourceId: workspace.outputs.resourceId
    retentionInDays: 30
    disableIpMasking: false
    samplingPercentage: 100
    ingestionMode: 'LogAnalytics'
    tags: tags
    roleAssignments: [
      {
        principalId: operatorPrincipalId
        principalType: 'User'
        roleDefinitionIdOrName: readerRoleId
      }
      {
        principalId: project.identity.principalId
        principalType: 'ServicePrincipal'
        roleDefinitionIdOrName: readerRoleId
      }
    ]
  }
}

// Match the sample's existing connection-string instrumentation without changing its runtime identity.
resource connection 'Microsoft.CognitiveServices/accounts/projects/connections@2026-03-01' = {
  parent: project
  name: 'appinsights'
  properties: {
    category: 'AppInsights'
    target: insights.outputs.resourceId
    authType: 'ApiKey'
    isSharedToAll: true
    credentials: {
      key: insights.outputs.connectionString
    }
    metadata: {
      ApiType: 'Azure'
      ResourceId: insights.outputs.resourceId
    }
  }
}

output APPLICATIONINSIGHTS_RESOURCE_ID string = insights.outputs.resourceId
output APPLICATIONINSIGHTS_APP_ID string = insights.outputs.applicationId
output LOG_ANALYTICS_WORKSPACE_RESOURCE_ID string = workspace.outputs.resourceId
output FOUNDRY_APPINSIGHTS_CONNECTION_ID string = connection.id
