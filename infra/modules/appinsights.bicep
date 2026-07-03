// -----------------------------------------------------------------------------
// Workspace-based Application Insights — the AgentOps observability surface.
// Classic (non-workspace) App Insights is retired, so a Log Analytics
// workspace is provisioned alongside the component. The API and MCP server
// export OpenTelemetry here via the connection string.
// -----------------------------------------------------------------------------

@description('Location for observability resources.')
param location string

@description('Short prefix for resource names.')
param namePrefix string

@description('Environment moniker.')
param environment string

@description('Tags to apply.')
param tags object

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${namePrefix}-${environment}'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
    features: {
      searchVersion: 1
    }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${namePrefix}-${environment}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    IngestionMode: 'LogAnalytics'
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

output workspaceId string = workspace.id
output appInsightsName string = appInsights.name
output connectionString string = appInsights.properties.ConnectionString
