// Brownfield reconciliation of the ONE production deployment.
// Deploy incrementally to rg-speakerpipeline-dev. Physical names are retained;
// the dev suffix is historical and never selects an environment.
targetScope = 'resourceGroup'

param location string = resourceGroup().location
param apiAppName string = 'app-speakerpipeline-api-dev'
param apiPlanName string = 'asp-speakerpipeline-dev'
param functionsAppName string = 'func-speakerpipeline-dev'
param functionsPlanName string = 'ASP-rgspeakerpipelinedev-902b'
param dataStorageAccountName string = 'stspeakerpipelinedev'
param hostStorageAccountName string = 'stspeakerpipelinefuncdev'
param deploymentContainerName string = 'app-package-func-speakerpipeline-dev-2c02758'
param sharedResourceGroupName string = 'rg-speakerpipeline-shared'
param apiIdentityName string = 'mi-api-dev'
param functionsIdentityName string = 'mi-functions-dev'
param keyVaultName string = 'kv-speakerpipeline'
param appInsightsName string = 'appi-speakerpipeline'
param workspaceName string = 'log-speakerpipeline'
param foundryAccountName string = 'foundry-speakerpipeline-dev'

@description('Existing action group receiving operational alerts. Required when alertsEnabled is true.')
param alertActionGroupResourceId string = ''

@description('Enable only after maintenance is complete and telemetry routing has been verified.')
param alertsEnabled bool = false

var tags = {
  project: 'speaker-pipeline'
  environment: 'production'
  managedBy: 'bicep'
  'cost-center': 'personal-rd'
}

// Data, host storage, Foundry, Key Vault and shared identities already exist.
// Never recreate these resources or put host queues in the pipeline data store.
resource dataStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: dataStorageAccountName
}
resource hostStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: hostStorageAccountName
}
resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: apiIdentityName
  scope: resourceGroup(sharedResourceGroupName)
}
resource functionsIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: functionsIdentityName
  scope: resourceGroup(sharedResourceGroupName)
}
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
  scope: resourceGroup(sharedResourceGroupName)
}
resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
  scope: resourceGroup(sharedResourceGroupName)
}
resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: workspaceName
  scope: resourceGroup(sharedResourceGroupName)
}
resource foundry 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: foundryAccountName
}

module api 'api.bicep' = {
  name: 'production-api'
  params: {
    location: location
    appName: apiAppName
    planName: apiPlanName
    sharedResourceGroupName: sharedResourceGroupName
    dataStorageAccountName: dataStorageAccountName
    apiIdentityName: apiIdentityName
    appInsightsName: appInsightsName
    tags: tags
  }
}

module agents 'modules/agents-functions.bicep' = {
  name: 'production-agents'
  params: {
    location: location
    appName: functionsAppName
    planName: functionsPlanName
    hostStorageAccountName: hostStorage.name
    deploymentContainerName: deploymentContainerName
    managedIdentityResourceId: functionsIdentity.id
    appInsightsResourceId: appInsights.id
    tags: tags
  }
}

module tables 'data-tables.bicep' = {
  name: 'production-data-tables'
  params: {
    storageAccountName: dataStorage.name
  }
}

// The maintenance API writes private exports/manifests/journals here. No agent
// identity receives a role on the pipeline data account.
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' existing = {
  parent: dataStorage
  name: 'default'
}
resource artifacts 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'artifacts'
  properties: {
    publicAccess: 'None'
  }
}
var blobContributorRole = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
resource apiArtifactsRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(artifacts.id, apiIdentity.id, blobContributorRole)
  scope: artifacts
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobContributorRole)
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

module alerts 'modules/alerts.bicep' = {
  name: 'production-alerts'
  params: {
    location: location
    workspaceResourceId: workspace.id
    apiAppName: apiAppName
    functionsAppName: functionsAppName
    actionGroupResourceId: alertActionGroupResourceId
    enabled: alertsEnabled
    tags: tags
  }
}

output apiHostName string = api.outputs.apiHostName
output functionsHostName string = agents.outputs.hostName
output tableEndpoint string = dataStorage.properties.primaryEndpoints.table
output artifactBlobEndpoint string = dataStorage.properties.primaryEndpoints.blob
output hostStorageAccount string = hostStorage.name
output keyVaultUri string = keyVault.properties.vaultUri
output appInsightsResourceId string = appInsights.id
output foundryResourceId string = foundry.id
