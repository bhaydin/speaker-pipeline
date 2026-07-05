#!/usr/bin/env bash
#
# One-time Telegram wiring. Nothing secret is committed — you pass values via
# environment variables. This stores the three Telegram secrets in Key Vault
# (where the deploy workflow's @Microsoft.KeyVault settings resolve them) and
# registers the bot's webhook against the Function App.
#
# Prereqs:
#   - az login (to a subscription that can write to the Key Vault)
#   - The Function App's managed identity already has "Key Vault Secrets User"
#     on the vault (same access the email lane uses).
#   - A bot from @BotFather (gives you BOT_TOKEN) and your numeric chat id
#     (message @userinfobot to get CHAT_ID).
#
# Usage:
#   VAULT=<vault-name> BOT_TOKEN=<from-botfather> CHAT_ID=<your-id> \
#     bash scripts/telegram-setup.sh
#
# WEBHOOK_SECRET and FUNCTION_APP default sensibly; override if needed.
#
set -euo pipefail

: "${VAULT:?set VAULT to your Key Vault name}"
: "${BOT_TOKEN:?set BOT_TOKEN (from @BotFather)}"
: "${CHAT_ID:?set CHAT_ID (your numeric Telegram id; message @userinfobot)}"
FUNCTION_APP="${FUNCTION_APP:-func-speakerpipeline-dev}"
WEBHOOK_SECRET="${WEBHOOK_SECRET:-$(openssl rand -hex 32)}"

echo "1/2  Storing secrets in Key Vault '$VAULT'..."
az keyvault secret set --vault-name "$VAULT" --name telegram-bot-token      --value "$BOT_TOKEN"      -o none
az keyvault secret set --vault-name "$VAULT" --name telegram-chat-id        --value "$CHAT_ID"        -o none
az keyvault secret set --vault-name "$VAULT" --name telegram-webhook-secret --value "$WEBHOOK_SECRET" -o none

# Resolve the app's real hostname. Azure assigns a unique regional default
# hostname (e.g. func-...-<hash>.eastus2-01.azurewebsites.net), so the plain
# "<name>.azurewebsites.net" form does NOT resolve.
RESOURCE_GROUP="${RESOURCE_GROUP:-rg-speakerpipeline-dev}"
HOST=$(az functionapp list --query "[?name=='${FUNCTION_APP}'].defaultHostName | [0]" -o tsv)
: "${HOST:?could not resolve the Function App hostname — check FUNCTION_APP / RESOURCE_GROUP and az login}"

URL="https://${HOST}/api/telegram/webhook"
echo "2/2  Registering webhook -> ${URL}"
curl -fsS "https://api.telegram.org/bot${BOT_TOKEN}/setWebhook" \
  --data-urlencode "url=${URL}" \
  --data-urlencode "secret_token=${WEBHOOK_SECRET}" \
  --data-urlencode 'allowed_updates=["message"]'
echo

cat <<'NOTE'

Done. Next:
  - Deploy the Functions app (merge to main, or re-run the deploy workflow) so
    the TelegramWebhook function ships and the Telegram__* Key Vault settings
    are applied. If the app was already deployed, restart it so it re-resolves
    the new Key Vault secrets.
  - Then message your bot:  /help
NOTE
