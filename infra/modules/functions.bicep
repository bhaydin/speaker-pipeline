// -----------------------------------------------------------------------------
// Flex Consumption Function App that hosts the remote MCP server
// (SpeakerPipeline.Mcp). Ships with the MCP commit — it belongs with the thing
// it hosts.
//
// Identity-based everything: the shared user-assigned managed identity is used
// for the deployment container, AzureWebJobsStorage (blob/queue/table), and
// acquiring a token for the API. No connection strings or account keys.
// -----------------------------------------------------------------------------

@description('Location for the Function App and plan.')
param location string

@description('Short prefix for resource names.')
param namePrefix string

@description('Environment moniker.')
param environment string

@description('Tags to apply.')
param tags object

@description('Name of the shared storage account (host storage + deployment container).')
param storageAccountName string

@description('Resource ID of the shared user-assigned managed identity.')
param managedIdentityResourceId string

@description('Client ID of the shared user-assigned managed identity (for AzureWebJobsStorage identity auth).')
param managedIdentityClientId string

@description('Application Insights connection string.')
param appInsightsConnectionString string

@description('Base URL of the deployed pipeline API, e.g. https://<api-host>/.')
param apiBaseUrl string = ''

@description('Entra scope the MCP host requests when calling the API, e.g. api://<app-id>/.default.')
param apiScope string = ''

@description('Blob container used by Flex Consumption for the deployment package.')
param deploymentContainerName string = 'deployments'

@description('Max instance count for the Flex Consumption plan.')
param maximumInstanceCount int = 40

@description('Per-instance memory in MB (512, 2048, or 4096).')
param instanceMemoryMB int = 2048

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'plan-${namePrefix}-mcp-${environment}'
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

resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: 'func-${namePrefix}-mcp-${environment}'
  location: location
  tags: tags
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
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storageAccount.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: managedIdentityResourceId
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: instanceMemoryMB
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
    }
    siteConfig: {
      appSettings: [
        {
          name: 'AzureWebJobsStorage__accountName'
          value: storageAccountName
        }
        {
          name: 'AzureWebJobsStorage__credential'
          value: 'managedidentity'
        }
        {
          name: 'AzureWebJobsStorage__clientId'
          value: managedIdentityClientId
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
        {
          name: 'SpeakerPipelineApi__BaseUrl'
          value: apiBaseUrl
        }
        {
          name: 'SpeakerPipelineApi__Scope'
          value: apiScope
        }
        {
          name: 'AZURE_CLIENT_ID'
          value: managedIdentityClientId
        }
      ]
    }
  }
}

output functionAppName string = functionApp.name
output functionAppHostName string = functionApp.properties.defaultHostName
