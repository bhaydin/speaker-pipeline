// -----------------------------------------------------------------------------
// API App Service — the SpeakerPipeline.Api host. This is the only project that
// touches Storage; every other consumer reaches data through this API.
//
// This models the CORRECTED desired state, not the hand-built dev app: it brings
// the API up to the same production-shaped bar the Functions host already meets —
// user-assigned managed identity, Key Vault-referenced auth config, identity-based
// Table Storage access, App Insights. See CLAUDE.md hard rules 2, 3, and 5.
//
// The caller supplies the shared managed identity, the data storage table
// endpoint, the App Insights connection string, and Key Vault secret URIs for the
// JwtBearer Authority/Audience. RBAC (Table Data Contributor, Key Vault Secrets
// User) is wired by the caller where it has the target resources in scope.
// -----------------------------------------------------------------------------

@description('Location for the plan and app.')
param location string

@description('App Service name, e.g. app-speakerpipeline-api-dev.')
param appName string

@description('App Service Plan name, e.g. asp-speakerpipeline-dev.')
param planName string

@description('Plan SKU. B1 is the minimum that supports Always On; Free (F1) is not production-shaped.')
param planSku string = 'B1'

@description('Tags to apply.')
param tags object

@description('Resource ID of the user-assigned managed identity the API runs as (mi-api-dev).')
param apiIdentityResourceId string

@description('Client ID of that identity — DefaultAzureCredential selects it via AZURE_CLIENT_ID.')
param apiIdentityClientId string

@description('Table endpoint of the pipeline data storage account, e.g. https://stspeakerpipelinedev.table.core.windows.net/.')
param tableEndpoint string

@description('Application Insights connection string (shared appi-speakerpipeline).')
param appInsightsConnectionString string

@description('Key Vault secret URI for the JwtBearer Authority (https://login.microsoftonline.com/<tenant>/v2.0).')
param authorityKeyVaultSecretUri string

@description('Key Vault secret URI for the JwtBearer Audience (the API app-id GUID or api://<app-id> URI).')
param audienceKeyVaultSecretUri string

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: planSku
  }
  properties: {
    reserved: true // Linux
  }
}

resource api 'Microsoft.Web/sites@2023-12-01' = {
  name: appName
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${apiIdentityResourceId}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    // Resolve @Microsoft.KeyVault(...) references through the user-assigned
    // identity — there is no system-assigned identity on this app.
    keyVaultReferenceIdentity: apiIdentityResourceId
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
      appSettings: [
        {
          name: 'Authentication__Authority'
          value: '@Microsoft.KeyVault(SecretUri=${authorityKeyVaultSecretUri})'
        }
        {
          name: 'Authentication__Audience'
          value: '@Microsoft.KeyVault(SecretUri=${audienceKeyVaultSecretUri})'
        }
        {
          name: 'Storage__TableEndpoint'
          value: tableEndpoint
        }
        {
          // DefaultAzureCredential binds to mi-api-dev via this client id.
          name: 'AZURE_CLIENT_ID'
          value: apiIdentityClientId
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
      ]
    }
  }
}

output appName string = api.name
output defaultHostName string = api.properties.defaultHostName
