# Security Policy

This is an agent-driven system that touches real cloud resources and (eventually) my real calendar and inbox. Security reports are taken seriously.

## Reporting a vulnerability

**Please do not open a public GitHub issue for security problems.**

Email **brian@brianhaydin.com** with:

- A description of the issue and the impact you believe it has
- Steps to reproduce, or a proof-of-concept if you have one
- Any suggested mitigation

You should expect an acknowledgement within **5 business days** and a substantive response (triage outcome, planned action, or request for more information) within **15 business days**. If the issue is confirmed, I will coordinate disclosure timing with you and credit you in the release notes if you would like.

## Scope

In scope:

- The agent code under [src/](src/) once it exists (Phase 2+)
- The provisioning scripts under [scripts/](scripts/)
- Authentication and authorization patterns (managed identity, role assignments, scopes)
- Any sample or seed data that accidentally contains real personal data
- Any committed file that contains a secret, token, key, or connection string

Out of scope:

- Vulnerabilities in upstream Microsoft Agent Framework, Azure SDKs, Azure AI Foundry, or OpenClaw — please report those to the respective projects
- Social-engineering scenarios against the maintainer
- Denial-of-service against a personal speaking pipeline

## Operational expectations

- Auth to Azure Table Storage is via **Managed Identity**. There should be no connection strings, account keys, or SAS tokens in this repo. If you find one, that is itself a vulnerability — please report it.
- All outbound action (emails, submissions, messages) goes through an **approval gate** in OpenClaw. Any code path that bypasses that gate is a security defect.
- Real CFP data, real contact details, and real personal email addresses must never appear in this repository. Samples under [samples/](samples/) are sanitized and fictional.
