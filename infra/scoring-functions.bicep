// -----------------------------------------------------------------------------
// Scoring Functions app configuration, reconciled to the real estate. Sets the
// app settings the scoring agent needs — Foundry binding, managed-identity
// selection, and the API client — on the existing func-speakerpipeline-dev host.
//
// The func app itself is not yet fully modelled in Bicep (that lands with the
// broader reconciliation). Until then this manages ONLY its app settings, and it
// does so safely: Bicep's appsettings resource is authoritative (it replaces the
// whole collection), so we read the host's current settings and union the new
// keys on top rather than clobbering AzureWebJobsStorage / App Insights.
//
// Deploy target: the per-env compute resource group.
//   az deployment group what-if -g rg-speakerpipeline-dev -f infra/scoring-functions.bicep
//   az deployment group create   -g rg-speakerpipeline-dev -f infra/scoring-functions.bicep
// -----------------------------------------------------------------------------

targetScope = 'resourceGroup'

@description('Scoring Functions app name.')
param functionAppName string = 'func-speakerpipeline-dev'

@description('Resource group holding the shared managed identity.')
param sharedResourceGroupName string = 'rg-speakerpipeline-shared'

@description('Name of the Functions app user-assigned managed identity (in the shared RG).')
param functionsIdentityName string = 'mi-functions-dev'

@description('Foundry account name; the Azure OpenAI endpoint is derived from it.')
param foundryAccountName string = 'foundry-speakerpipeline-dev'

@description('Chat provider binding for the scoring agent.')
param scoringProvider string = 'foundry'

@description('Foundry deployment the scoring agent calls (e.g. gpt-5-mini, model-router).')
param scoringModelName string = 'gpt-5-mini'

@description('Base URL of the deployed pipeline API.')
param apiBaseUrl string = 'https://app-speakerpipeline-api-dev-aedbhkc6d6a7hpdu.eastus2-01.azurewebsites.net/'

@description('Client id (app id) of the API app registration, used to build the token scope.')
param apiAppClientId string = 'bace4d6a-9ebd-4fac-a152-d1c67d593a41'

// --- Existing resources ------------------------------------------------------

resource functionsIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: functionsIdentityName
  scope: resourceGroup(sharedResourceGroupName)
}

resource functionApp 'Microsoft.Web/sites@2023-12-01' existing = {
  name: functionAppName
}

// --- App settings: preserve existing, add the scoring-agent keys -------------

// AzureOpenAIClient talks to the resource's Azure OpenAI data-plane endpoint.
var foundryOpenAIEndpoint = 'https://${foundryAccountName}.openai.azure.com/'

// Authoritative read of the host's current settings so union() can preserve them.
// list() is the supported way to read existing app settings for a merge; a
// resource-symbol reference can't express "read current values to union onto".
#disable-next-line use-resource-symbol-reference
var currentSettings = list(resourceId('Microsoft.Web/sites/config', functionAppName, 'appsettings'), '2023-12-01').properties

var scoringSettings = {
  // Binds DefaultAzureCredential to mi-functions-dev for BOTH the Foundry call
  // and the API call (BearerTokenHandler). Without it, both fail on this host,
  // which has no system-assigned identity.
  AZURE_CLIENT_ID: functionsIdentity.properties.clientId
  ScoringAgent__Provider: scoringProvider
  ScoringAgent__Endpoint: foundryOpenAIEndpoint
  ScoringAgent__ModelName: scoringModelName
  SpeakerPipelineApi__BaseUrl: apiBaseUrl
  SpeakerPipelineApi__Scope: 'api://${apiAppClientId}/.default'
}

resource appSettings 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: functionApp
  name: 'appsettings'
  properties: union(currentSettings, scoringSettings)
}
