// Runtime settings are merged by the release/operator workflow. Omitting
// appSettings here preserves authentication, storage and maintenance controls.
param location string
param appName string
param planName string
param tags object
param apiIdentityResourceId string
param appInsightsResourceId string

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: 'B1'
    tier: 'Basic'
  }
  properties: {
    reserved: true
  }
}

resource api 'Microsoft.Web/sites@2023-12-01' = {
  name: appName
  location: location
  tags: union(tags, {
    'hidden-link: /app-insights-resource-id': appInsightsResourceId
  })
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
    keyVaultReferenceIdentity: apiIdentityResourceId
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      // The hosted Blazor client adds a second runtimeconfig.json to the publish
      // output. Pin the server entry point instead of relying on Oryx detection.
      appCommandLine: 'dotnet /home/site/wwwroot/SpeakerPipeline.Api.dll'
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
      healthCheckPath: '/health'
    }
  }
}

output defaultHostName string = api.properties.defaultHostName
