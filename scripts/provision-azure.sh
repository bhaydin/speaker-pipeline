#!/usr/bin/env bash
# Retained only so old documentation fails safely rather than creating a second
# deployment or granting an agent direct access to the production data account.
set -euo pipefail
echo 'Retired: this repository operates one existing production deployment.' >&2
echo 'Follow infra/README.md and docs/production-rollout.md; use the reviewed Bicep what-if.' >&2
exit 1
