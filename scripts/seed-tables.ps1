#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Retired direct-table sample seeding entrypoint.
.DESCRIPTION
    Fails without accessing Azure or writing data. Use SpeakerPipeline.Migrate
    through an isolated local API for samples, or its explicit private import
    and maintenance commands for production. See tools/SpeakerPipeline.Migrate/README.md.
#>
[CmdletBinding()]
param(
    [string]$StorageAccountName,
    [string]$SamplesPath
)

throw @'
Direct-table sample seeding is retired. No data was changed.
Preview fictional samples against a local API backed by loopback Azurite:
  dotnet run --project tools/SpeakerPipeline.Migrate -- samples --source samples --api http://localhost:5080/
Use tools/SpeakerPipeline.Migrate/README.md and docs/production-rollout.md for private production import, cleanup, and recovery.
'@
