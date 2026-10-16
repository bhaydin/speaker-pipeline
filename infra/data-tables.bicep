// -----------------------------------------------------------------------------
// Ensures all six tables exist on the existing production data account.
// Declaring an existing table does not seed, delete or replace its entities.
//
// Deploy target: the production resource group (historical dev suffix).
//   az deployment group create -g rg-speakerpipeline-dev -f infra/data-tables.bicep
// -----------------------------------------------------------------------------

targetScope = 'resourceGroup'

@description('Name of the pipeline data storage account.')
param storageAccountName string = 'stspeakerpipelinedev'

@description('Tables backing the Core repositories.')
param tableNames array = [
  'Events'
  'Submissions'
  'Talks'
  'Topics'
  'Blackouts'
  'NotificationLog'
]

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' existing = {
  parent: storageAccount
  name: 'default'
}

resource tables 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = [
  for name in tableNames: {
    parent: tableService
    name: name
  }
]
