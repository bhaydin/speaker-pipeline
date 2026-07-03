using 'main.bicep'

// Placeholder parameter file. No subscription/tenant IDs, no secrets — those
// come from the deployment context (the signed-in `az` session) at deploy time.
// Override any value with `--parameters key=value` on the CLI.

param location = 'centralus'
param namePrefix = 'speakerpipe'
param environment = 'prod'
