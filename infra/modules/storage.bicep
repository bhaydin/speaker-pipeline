// -----------------------------------------------------------------------------
// Storage account = the portable system of record.
//   Tables: Events, Submissions, Talks (existing) + Topics, Blackouts,
//           NotificationLog (net-new in Milestone 1).
//   Blob:   artifacts container (decks, abstracts, exports, seed archive).
//
// Managed-identity only: shared key access disabled, TLS 1.2 floor, no public
// blob. See docs/architecture-table-storage.md for the schema this backs.
// -----------------------------------------------------------------------------

@description('Location for the storage account.')
param location string

@description('Short prefix for resource names.')
param namePrefix string

@description('Tags to apply.')
param tags object

@description('Table names created in the account.')
param tableNames array = [
  'Events'
  'Submissions'
  'Talks'
  'Topics'
  'Blackouts'
  'NotificationLog'
]

@description('Blob container names created in the account.')
param blobContainerNames array = [
  'artifacts'
  'deployments'
]

// Storage account names: 3-24 chars, lowercase alphanumeric, globally unique.
var storageAccountName = take('${toLower(replace(namePrefix, '-', ''))}${uniqueString(subscription().id, resourceGroup().id)}', 24)

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
}

resource tables 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = [
  for name in tableNames: {
    parent: tableService
    name: name
  }
]

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
}

resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = [
  for name in blobContainerNames: {
    parent: blobService
    name: name
    properties: {
      publicAccess: 'None'
    }
  }
]

output storageAccountName string = storageAccount.name
output storageAccountId string = storageAccount.id
output tableEndpoint string = storageAccount.properties.primaryEndpoints.table
output blobEndpoint string = storageAccount.properties.primaryEndpoints.blob
