// -----------------------------------------------------------------------------
// API App Service deployment for a single environment, reconciled to the real
// estate. Deploys the API plan + app into the per-env compute RG and references
// the existing SHARED identity/Key Vault/App Insights and the per-env data
// storage account. This is the "API module first" slice of the broader Bicep
// reconciliation — it is safe to what-if in isolation against a live env.
//
// Deploy target: the per-env compute resource group (e.g. rg-speakerpipeline-dev).
//   az deployment group what-if \
//     --resource-group rg-speakerpipeline-dev \
//     --template-file infra/api.bicep
//   az deployment group create \
//     --resource-group rg-speakerpipeline-dev \
//     --template-file infra/api.bicep
//
// PREREQUISITES (one-time, out of band — see infra/README.md):
//   1. Secrets 'api-auth-authority' and 'api-auth-audience' exist in the Key
//      Vault. Authority = https://login.microsoftonline.com/<tenant-id>/v2.0.
//      Audience = the API app registration's client id (or api://<client-id>).
//   2. mi-api-dev has the "Key Vault Secrets User" role on the Key Vault so the
//      app can resolve the @Microsoft.KeyVault(...) references. (This role sits
//      in the shared RG; grant it there — it is out of scope for this template.)
// -----------------------------------------------------------------------------

targetScope = 'resourceGroup'

@description('Location for the plan and app. Defaults to the RG location.')
param location string = resourceGroup().location

@description('Short prefix for resource names.')
param namePrefix string = 'speakerpipeline'

@description('Environment moniker baked into names.')
@allowed([
  'dev'
  'prod'
])
param environment string = 'dev'

@description('Resource group holding the shared identity, Key Vault, and App Insights.')
param sharedResourceGroupName string = 'rg-speakerpipeline-shared'

@description('Name of the per-env pipeline data storage account (the Tables backing store).')
param dataStorageAccountName string = 'stspeakerpipelinedev'

@description('Name of the shared Key Vault.')
param keyVaultName string = 'kv-speakerpipeline'

@description('Name of the shared Application Insights component.')
param appInsightsName string = 'appi-speakerpipeline'

@description('Name of the API user-assigned managed identity in the shared RG.')
param apiIdentityName string = 'mi-api-dev'

@description('Key Vault secret name holding the JwtBearer Authority.')
param authoritySecretName string = 'api-auth-authority'

@description('Key Vault secret name holding the JwtBearer Audience.')
param audienceSecretName string = 'api-auth-audience'

@description('App Service Plan SKU. B1 is the minimum that supports Always On.')
param planSku string = 'B1'

@description('Tags applied to created resources.')
param tags object = {
  project: 'speaker-pipeline'
  environment: environment
  managedBy: 'bicep'
}

// --- Existing shared resources (rg-speakerpipeline-shared) -------------------

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: apiIdentityName
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

// --- Existing per-env data storage (this RG) ---------------------------------

resource dataStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: dataStorageAccountName
}

// --- API plan + app ----------------------------------------------------------

module api 'modules/appservice.bicep' = {
  name: 'api-appservice'
  params: {
    location: location
    appName: 'app-${namePrefix}-api-${environment}'
    planName: 'asp-${namePrefix}-${environment}'
    planSku: planSku
    tags: tags
    apiIdentityResourceId: apiIdentity.id
    apiIdentityClientId: apiIdentity.properties.clientId
    tableEndpoint: dataStorage.properties.primaryEndpoints.table
    appInsightsConnectionString: appInsights.properties.ConnectionString
    authorityKeyVaultSecretUri: '${keyVault.properties.vaultUri}secrets/${authoritySecretName}'
    audienceKeyVaultSecretUri: '${keyVault.properties.vaultUri}secrets/${audienceSecretName}'
  }
}

// --- Data-plane RBAC: the API identity reads/writes the three tables ---------
// Storage Table Data Contributor. Scoped to the data storage account, so it lives
// with the compute RG rather than the shared RG.
var storageTableDataContributorId = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'

resource tableDataRbac 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(dataStorage.id, apiIdentity.id, storageTableDataContributorId)
  scope: dataStorage
  properties: {
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributorId)
  }
}

output apiHostName string = api.outputs.defaultHostName
