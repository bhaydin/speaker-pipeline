[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SubscriptionId,
    [Parameter(Mandatory)][string]$ApiApplicationId,
    [Parameter(Mandatory)][string]$OwnerObjectId,
    [Parameter(Mandatory)][string]$PrivateDirectory,
    [string]$ResourceGroup = 'rg-speakerpipeline-dev',
    [string]$SharedResourceGroup = 'rg-speakerpipeline-shared',
    [string]$Repository = 'bhaydin/speaker-pipeline',
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'

function Read-AzJson([string[]]$Arguments) {
    $raw = & az @Arguments --output json
    if ($LASTEXITCODE -ne 0) { throw 'Azure command failed; no subsequent changes will run.' }
    if ($raw) { return ($raw | ConvertFrom-Json) }
}

function Write-GraphJson([string]$Method, [string]$Uri, $Body, [string]$Name) {
    $file = Join-Path $PrivateDirectory "$Name.json"
    $Body | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $file -Encoding utf8NoBOM
    $null = Read-AzJson @('rest', '--method', $Method, '--url', $Uri, '--body', "@$file")
}

# The caller must provide a private directory outside the public repository.
$privatePath = [IO.Path]::GetFullPath($PrivateDirectory)
$repoPath = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent)) + [IO.Path]::DirectorySeparatorChar
if ($privatePath.StartsWith($repoPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Identity backups must be outside the repository.'
}
if (-not (Test-Path -LiteralPath $privatePath -PathType Container)) { throw 'Create an access-controlled private directory first.' }

$app = Read-AzJson @('ad', 'app', 'show', '--id', $ApiApplicationId)
$apiSp = Read-AzJson @('ad', 'sp', 'show', '--id', $ApiApplicationId)
$deployApps = @(Read-AzJson @('ad', 'app', 'list', '--filter', "displayName eq 'github-actions-speakerpipeline-deploy'"))
if ($deployApps.Count -ne 1) { throw 'Expected exactly one existing deployment application.' }
$deployApp = $deployApps[0]
$deploySp = Read-AzJson @('ad', 'sp', 'show', '--id', $deployApp.appId)
$function = Read-AzJson @('functionapp', 'show', '--subscription', $SubscriptionId, '-g', $ResourceGroup, '-n', 'func-speakerpipeline-dev')
$identities = @($function.identity.userAssignedIdentities.PSObject.Properties | ForEach-Object { $_.Value.principalId })
if ($identities.Count -ne 1) { throw 'Expected the existing Functions host to use exactly one user-assigned identity.' }

$roles = @($app.appRoles | Select-Object allowedMemberTypes, description, displayName, id, isEnabled, value)
foreach ($definition in @(
    @{ value = 'Pipeline.Access'; members = @('User', 'Application'); description = 'Read and maintain the speaking pipeline.' },
    @{ value = 'Pipeline.Maintenance'; members = @('User'); description = 'Operator access to audited data maintenance during maintenance windows.' }
)) {
    $existing = @($roles | Where-Object value -eq $definition.value)
    if ($existing.Count -eq 0) {
        # Entra stores roles and delegated scopes in one entitlement collection.
        # Reuse a matching scope's ID and shared properties to keep its existing
        # consent contract while adding application/user role assignments.
        $scope = @($app.api.oauth2PermissionScopes | Where-Object value -eq $definition.value)
        $roles += [pscustomobject]@{
            allowedMemberTypes = $definition.members
            description = $(if ($scope.Count) { $scope[0].adminConsentDescription } else { $definition.description })
            displayName = $(if ($scope.Count) { $scope[0].adminConsentDisplayName } else { $definition.value })
            id = $(if ($scope.Count) { $scope[0].id } else { [Guid]::NewGuid().ToString() })
            isEnabled = $true
            value = $definition.value
        }
    } elseif (-not $existing[0].isEnabled) {
        throw "Existing role $($definition.value) is disabled; inspect before continuing."
    }
}
$subject = "repo:${Repository}:environment:production"
$federation = @(Read-AzJson @('ad', 'app', 'federated-credential', 'list', '--id', $deployApp.id))
$assignments = Read-AzJson @('rest', '--method', 'GET', '--url', "https://graph.microsoft.com/v1.0/servicePrincipals/$($apiSp.id)/appRoleAssignedTo")
if ($assignments.'@odata.nextLink') { throw 'Paginated role assignments require explicit inspection before applying.' }

if (-not $Apply) {
    [pscustomobject]@{
        ApiApplication = $app.displayName
        DeploymentApplication = $deployApp.displayName
        ProductionSubject = $subject
        Roles = @('Pipeline.Access', 'Pipeline.Maintenance')
        AccessPrincipals = 3
        MaintenancePrincipals = 1
        Mode = 'Preview'
    }
    return
}

# Preserve the pre-change directory state privately; never emit IDs or secrets.
foreach ($item in @(@('api-app', $app), @('api-service-principal', $apiSp), @('deployment-federation', $federation), @('api-role-assignments', $assignments))) {
    $file = Join-Path $privatePath "$($item[0]).before.json"
    if (-not (Test-Path -LiteralPath $file)) { $item[1] | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $file -Encoding utf8NoBOM }
}

Write-GraphJson 'PATCH' "https://graph.microsoft.com/v1.0/applications/$($app.id)" @{appRoles = $roles} 'api-roles.request'
$accessRole = ($roles | Where-Object value -eq 'Pipeline.Access').id
$maintenanceRole = ($roles | Where-Object value -eq 'Pipeline.Maintenance').id
$desired = @(
    @{ principalId = $OwnerObjectId; appRoleId = $accessRole },
    @{ principalId = $OwnerObjectId; appRoleId = $maintenanceRole },
    @{ principalId = $identities[0]; appRoleId = $accessRole },
    @{ principalId = $deploySp.id; appRoleId = $accessRole }
)
foreach ($assignment in $desired) {
    $exists = @($assignments.value | Where-Object { $_.principalId -eq $assignment.principalId -and $_.appRoleId -eq $assignment.appRoleId })
    if ($exists.Count -eq 0) {
        Write-GraphJson 'POST' "https://graph.microsoft.com/v1.0/servicePrincipals/$($apiSp.id)/appRoleAssignedTo" @{
            principalId = $assignment.principalId; resourceId = $apiSp.id; appRoleId = $assignment.appRoleId
        } 'role-assignment.request'
    }
}
if (-not ($federation | Where-Object { $_.subject -eq $subject -and $_.issuer -eq 'https://token.actions.githubusercontent.com' })) {
    Write-GraphJson 'POST' "https://graph.microsoft.com/v1.0/applications/$($deployApp.id)/federatedIdentityCredentials" @{
        name = 'github-production'; issuer = 'https://token.actions.githubusercontent.com'
        subject = $subject; audiences = @('api://AzureADTokenExchange')
    } 'production-federation.request'
}
# Existing legacy roles and federation stay until verified cutover; the API code
# authorizes only the explicit production roles. No credentials are rotated here.
Write-Output 'Production roles, assignments, and federation configured. Legacy configuration retained for rollback.'
