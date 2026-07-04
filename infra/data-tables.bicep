// -----------------------------------------------------------------------------
// Ensures the pipeline's Table Storage tables exist on the existing data
// account. The full storage.bicep module declares these, but the deployed
// stspeakerpipelinedev account was created by hand and never got them, so the
// API 500s on the first query (TableNotFound). Declaring a table that already
// exists is a no-op — this is safe and idempotent, and touches no data.
//
// Deploy target: the per-env compute resource group.
//   az deployment group create -g rg-speakerpipeline-dev -f infra/data-tables.bicep
// -----------------------------------------------------------------------------

targetScope = 'resourceGroup'

@description('Name of the pipeline data storage account.')
param storageAccountName string = 'stspeakerpipelinedev'

@description('Tables backing the Core repositories. Matches infra/modules/storage.bicep.')
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
