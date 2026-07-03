// -----------------------------------------------------------------------------
// Key Vault (standard) — holds every secret the later milestones need
// (Telegram bot token, Google refresh token, Graph app secrets). RBAC
// authorization only; the shared managed identity reads secrets via the
// "Key Vault Secrets User" role granted in rbac.bicep. No secrets are seeded
// here — they are added out-of-band as each milestone lands.
// -----------------------------------------------------------------------------

@description('Location for the Key Vault.')
param location string

@description('Short prefix for resource names.')
param namePrefix string

@description('Tags to apply.')
param tags object

// Key Vault names: 3-24 chars, globally unique.
var keyVaultName = take('kv-${namePrefix}-${uniqueString(subscription().id, resourceGroup().id)}', 24)

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

output keyVaultName string = keyVault.name
output keyVaultId string = keyVault.id
output keyVaultUri string = keyVault.properties.vaultUri
