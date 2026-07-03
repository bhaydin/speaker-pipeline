// -----------------------------------------------------------------------------
// Role assignments for the shared managed identity. No connection strings, no
// account keys — the identity gets least-privilege data-plane roles:
//   - Storage Table Data Contributor       (Events/Submissions/Talks/Topics/...)
//   - Storage Blob Data Contributor         (artifacts + Flex deployment container)
//   - Storage Queue Data Contributor        (MCP SSE transport via AzureWebJobsStorage)
//   - Storage Queue Data Message Processor  (MCP SSE transport)
//   - Key Vault Secrets User                (read secrets at runtime)
// -----------------------------------------------------------------------------

@description('Storage account to scope the storage role assignments to.')
param storageAccountName string

@description('Key Vault to scope the secrets role assignment to.')
param keyVaultName string

@description('Principal (object) ID of the managed identity receiving the roles.')
param principalId string

// Built-in role definition IDs (stable GUIDs).
var storageTableDataContributor = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
var storageBlobDataContributor = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var storageQueueDataContributor = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
var storageQueueDataMessageProcessor = '8a0f0c08-91a1-4084-bc3d-661d67233fed'
var keyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource tableRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, principalId, storageTableDataContributor)
  scope: storageAccount
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributor)
  }
}

resource blobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, principalId, storageBlobDataContributor)
  scope: storageAccount
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributor)
  }
}

resource queueRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, principalId, storageQueueDataContributor)
  scope: storageAccount
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageQueueDataContributor)
  }
}

resource queueMessageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, principalId, storageQueueDataMessageProcessor)
  scope: storageAccount
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageQueueDataMessageProcessor)
  }
}

resource secretsRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, principalId, keyVaultSecretsUser)
  scope: keyVault
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser)
  }
}
