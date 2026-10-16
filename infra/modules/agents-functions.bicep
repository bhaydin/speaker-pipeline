// The deployed agents host. MCP remains local source and has no cloud host.
// Existing host storage and deployment container remain separate from data.
param location string
param appName string
param planName string
param hostStorageAccountName string
param deploymentContainerName string
param managedIdentityResourceId string
param appInsightsResourceId string
param tags object

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'functionapp'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true
  }
}

resource agents 'Microsoft.Web/sites@2024-04-01' = {
  name: appName
  location: location
  tags: union(tags, {
    'hidden-link: /app-insights-resource-id': appInsightsResourceId
  })
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${managedIdentityResourceId}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    keyVaultReferenceIdentity: managedIdentityResourceId
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: 'https://${hostStorageAccountName}.blob.${environment().suffixes.storage}/${deploymentContainerName}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: managedIdentityResourceId
          }
        }
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
      scaleAndConcurrency: {
        maximumInstanceCount: 100
        instanceMemoryMB: 2048
      }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
    }
  }
}

output hostName string = agents.properties.defaultHostName
