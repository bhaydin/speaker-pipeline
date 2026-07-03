// -----------------------------------------------------------------------------
// User-assigned managed identity shared by the API and the Functions-hosted
// MCP server. One identity to hold the storage + Key Vault role assignments so
// service-to-service auth uses no connection strings or keys.
// -----------------------------------------------------------------------------

@description('Location for the identity.')
param location string

@description('Short prefix for resource names.')
param namePrefix string

@description('Environment moniker.')
param environment string

@description('Tags to apply.')
param tags object

resource uami 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${namePrefix}-${environment}'
  location: location
  tags: tags
}

output name string = uami.name
output resourceId string = uami.id
output principalId string = uami.properties.principalId
output clientId string = uami.properties.clientId
