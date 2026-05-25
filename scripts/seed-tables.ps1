#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Seed the speaking-pipeline Azure Tables (Events, Submissions, Talks).

.DESCRIPTION
    Reads sanitized JSON arrays from ../samples/ and upserts them into the
    three tables defined in docs/architecture-table-storage.md. Used as
    a one-shot bootstrap after running scripts/provision-azure.sh.

    Auth is via DefaultAzureCredential — make sure you are logged in (`az login`
    or `Connect-AzAccount`) and that your principal has Storage Table Data
    Contributor on the target storage account.

.PARAMETER StorageAccountName
    Name of the storage account provisioned by provision-azure.sh.

.PARAMETER SamplesPath
    Path to the samples/ directory. Defaults to ../samples relative to this
    script.

.EXAMPLE
    pwsh ./scripts/seed-tables.ps1 -StorageAccountName <placeholder>

.NOTES
    Phase 1 status: STUB. Real implementation lands in Phase 2 alongside the
    .NET solution. The script below documents the intended shape so future
    work can drop in.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$StorageAccountName,

    [Parameter(Mandatory = $false)]
    [string]$SamplesPath = (Join-Path $PSScriptRoot '..' 'samples')
)

Write-Host "Seed Tables — Phase 1 stub" -ForegroundColor Yellow
Write-Host ""
Write-Host "  Storage account: $StorageAccountName"
Write-Host "  Samples path   : $SamplesPath"
Write-Host ""

# TODO Phase 2 — implementation outline:
#
# 1. Install / import the Az.Storage module (or use the Azure.Data.Tables
#    .NET assembly via Add-Type for closer parity with the agent code).
#
# 2. Acquire a TableServiceClient with DefaultAzureCredential:
#
#        $endpoint = "https://$StorageAccountName.table.core.windows.net"
#        $cred     = [Azure.Identity.DefaultAzureCredential]::new()
#        $service  = [Azure.Data.Tables.TableServiceClient]::new(
#                        [Uri]$endpoint, $cred)
#
# 3. For each (table, sample-file) pair:
#
#        @(
#            @{ Table = 'Talks';       File = 'sample-talks.json' }
#            @{ Table = 'Events';      File = 'sample-events.json' }
#            @{ Table = 'Submissions'; File = 'sample-submissions.json' }
#        )
#
#    Read the JSON, project each object to a TableEntity, and call
#    UpsertEntityAsync with TableUpdateMode.Merge so reseeding is safe.
#
# 4. Honor SchemaVersion — if a sample row has SchemaVersion higher than
#    the existing row, take the new shape; otherwise merge.
#
# 5. Print a summary: rows inserted, rows merged, rows skipped.
#
# Until Phase 2 lands, this script exits with code 0 and a banner. It is
# safe to call from CI smoke tests as a no-op.

Write-Host "No-op until Phase 2. See script comments for the intended shape." -ForegroundColor Yellow
exit 0
