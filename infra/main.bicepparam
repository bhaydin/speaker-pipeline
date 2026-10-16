using 'main.bicep'

// The existing deployed resources are production. Do not rename or substitute
// prod suffixes: that creates new resources rather than promoting these.
param apiAppName = 'app-speakerpipeline-api-dev'
param apiPlanName = 'asp-speakerpipeline-dev'
param functionsAppName = 'func-speakerpipeline-dev'
param functionsPlanName = 'ASP-rgspeakerpipelinedev-902b'
param dataStorageAccountName = 'stspeakerpipelinedev'
param hostStorageAccountName = 'stspeakerpipelinefuncdev'
param sharedResourceGroupName = 'rg-speakerpipeline-shared'
param alertsEnabled = false
