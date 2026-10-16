// Reconcile the existing production API without replacing app settings.
// az deployment group what-if -g rg-speakerpipeline-dev -f infra/api.bicep
targetScope = 'resourceGroup'

param location string = resourceGroup().location
param appName string = 'app-speakerpipeline-api-dev'
param planName string = 'asp-speakerpipeline-dev'
param sharedResourceGroupName string = 'rg-speakerpipeline-shared'
param dataStorageAccountName string = 'stspeakerpipelinedev'
param apiIdentityName string = 'mi-api-dev'
param appInsightsName string = 'appi-speakerpipeline'
param tags object = {
  project: 'speaker-pipeline'
  environment: 'production'
  managedBy: 'bicep'
  'cost-center': 'personal-rd'
}

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: apiIdentityName
  scope: resourceGroup(sharedResourceGroupName)
}
resource dataStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: dataStorageAccountName
}
resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
  scope: resourceGroup(sharedResourceGroupName)
}

module api 'modules/appservice.bicep' = {
  name: 'api-appservice'
  params: {
    location: location
    appName: appName
    planName: planName
    tags: tags
    apiIdentityResourceId: apiIdentity.id
    appInsightsResourceId: appInsights.id
  }
}

// Existing identity/table role is retained and checked in the rollout preflight.
// It is intentionally not recreated under a new role-assignment GUID.
output apiHostName string = api.outputs.defaultHostName
output tableEndpoint string = dataStorage.properties.primaryEndpoints.table
