// -----------------------------------------------------------------------------
// Speaker Pipeline — Milestone 1 core infrastructure.
//
// Subscription-scoped: creates the resource group, then deploys the shared
// identity, storage (Tables + Blob), Key Vault, and workspace-based App
// Insights, and wires managed-identity RBAC. No Function App here — that ships
// with the MCP server module (infra/modules/functions.bicep) in the MCP commit.
//
// Deploy:
//   az deployment sub create \
//     --location <location> \
//     --template-file infra/main.bicep \
//     --parameters infra/main.bicepparam
// -----------------------------------------------------------------------------

targetScope = 'subscription'

@description('Location for the resource group and all resources.')
param location string = 'centralus'

@description('Short prefix for resource names (lowercase letters/numbers).')
@minLength(3)
@maxLength(12)
param namePrefix string = 'speakerpipe'

@description('Environment moniker baked into names and tags.')
@allowed([
  'dev'
  'prod'
])
param environment string = 'prod'

@description('Tags applied to the resource group and propagated to resources.')
param tags object = {
  project: 'speaker-pipeline'
  environment: environment
  managedBy: 'bicep'
}

@description('Base URL of the deployed pipeline API for the MCP host. Empty until the API is deployed.')
param apiBaseUrl string = ''

@description('Entra scope the MCP host requests when calling the API. Empty until Entra auth is configured.')
param apiScope string = ''

var resourceGroupName = 'rg-${namePrefix}-${environment}'

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

module identity 'modules/identity.bicep' = {
  name: 'identity'
  scope: rg
  params: {
    location: location
    namePrefix: namePrefix
    environment: environment
    tags: tags
  }
}

module storage 'modules/storage.bicep' = {
  name: 'storage'
  scope: rg
  params: {
    location: location
    namePrefix: namePrefix
    tags: tags
  }
}

module keyvault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  scope: rg
  params: {
    location: location
    namePrefix: namePrefix
    tags: tags
  }
}

module observability 'modules/appinsights.bicep' = {
  name: 'appinsights'
  scope: rg
  params: {
    location: location
    namePrefix: namePrefix
    environment: environment
    tags: tags
  }
}

module rbac 'modules/rbac.bicep' = {
  name: 'rbac'
  scope: rg
  params: {
    storageAccountName: storage.outputs.storageAccountName
    keyVaultName: keyvault.outputs.keyVaultName
    principalId: identity.outputs.principalId
  }
}

module functions 'modules/functions.bicep' = {
  name: 'functions-mcp'
  scope: rg
  params: {
    location: location
    namePrefix: namePrefix
    environment: environment
    tags: tags
    storageAccountName: storage.outputs.storageAccountName
    managedIdentityResourceId: identity.outputs.resourceId
    managedIdentityClientId: identity.outputs.clientId
    appInsightsConnectionString: observability.outputs.connectionString
    apiBaseUrl: apiBaseUrl
    apiScope: apiScope
  }
  dependsOn: [
    rbac
  ]
}

output resourceGroupName string = rg.name
output managedIdentityName string = identity.outputs.name
output managedIdentityClientId string = identity.outputs.clientId
output storageAccountName string = storage.outputs.storageAccountName
output tableEndpoint string = storage.outputs.tableEndpoint
output blobEndpoint string = storage.outputs.blobEndpoint
output keyVaultName string = keyvault.outputs.keyVaultName
output keyVaultUri string = keyvault.outputs.keyVaultUri
output appInsightsConnectionString string = observability.outputs.connectionString
output mcpFunctionAppName string = functions.outputs.functionAppName
output mcpEndpoint string = 'https://${functions.outputs.functionAppHostName}/runtime/webhooks/mcp'
