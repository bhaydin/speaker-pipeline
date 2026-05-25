#!/usr/bin/env bash
#
# provision-azure.sh
#
# Provisions the Azure resources for the speaking pipeline:
#   - Resource group
#   - Standard general-purpose v2 storage account (LRS, Hot, TLS 1.2 min)
#   - Three tables: Events, Submissions, Talks
#   - Storage Table Data Contributor role assignment for the MAF managed identity
#
# Commands mirror docs/pipeline_table_storage_schema.md §1. Treat the schema
# doc as the source of truth — if the two diverge, update this script.
#
# Prereqs:
#   - Azure CLI logged in (`az login`)
#   - Subscription selected (`az account set --subscription <id-or-name>`)
#   - The MAF managed identity already exists; export its object id as
#     MAF_MI_OBJECT_ID before running.
#
# Usage:
#   MAF_MI_OBJECT_ID=<placeholder> ./scripts/provision-azure.sh

set -euo pipefail

# ---- Configurable inputs --------------------------------------------------

RG="${RG:-rg-pipeline-prod}"
LOC="${LOC:-centralus}"
SA="${SA:-sapipelinebh$(date +%s | tail -c 5)}"   # storage account names must be globally unique
MAF_MI_OBJECT_ID="${MAF_MI_OBJECT_ID:-}"

if [[ -z "${MAF_MI_OBJECT_ID}" ]]; then
    echo "ERROR: MAF_MI_OBJECT_ID is required (object id of the MAF managed identity)." >&2
    echo "       Export it before running, e.g. MAF_MI_OBJECT_ID=<placeholder> ./scripts/provision-azure.sh" >&2
    exit 1
fi

echo "Provisioning:"
echo "  Resource group : ${RG}"
echo "  Location       : ${LOC}"
echo "  Storage account: ${SA}"

# ---- Resource group -------------------------------------------------------

az group create \
    --name "${RG}" \
    --location "${LOC}" \
    --output none

# ---- Storage account ------------------------------------------------------

az storage account create \
    --name "${SA}" \
    --resource-group "${RG}" \
    --location "${LOC}" \
    --sku Standard_LRS \
    --kind StorageV2 \
    --allow-blob-public-access false \
    --min-tls-version TLS1_2 \
    --output none

# ---- Tables ---------------------------------------------------------------

az storage table create --name Events      --account-name "${SA}" --auth-mode login --output none
az storage table create --name Submissions --account-name "${SA}" --auth-mode login --output none
az storage table create --name Talks       --account-name "${SA}" --auth-mode login --output none

# ---- Role assignment ------------------------------------------------------
# Grant the MAF managed identity Storage Table Data Contributor on the
# storage account so agents can read/write with DefaultAzureCredential.

SA_ID="$(az storage account show \
    --name "${SA}" \
    --resource-group "${RG}" \
    --query id \
    --output tsv)"

az role assignment create \
    --assignee "${MAF_MI_OBJECT_ID}" \
    --role "Storage Table Data Contributor" \
    --scope "${SA_ID}" \
    --output none

echo
echo "Done."
echo "  Storage account : ${SA}"
echo "  Tables          : Events, Submissions, Talks"
echo "  Role assignment : Storage Table Data Contributor → ${MAF_MI_OBJECT_ID}"
echo
echo "Next: pwsh ./scripts/seed-tables.ps1 to seed Talks and Events."
