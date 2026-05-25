# Contributing

Thanks for taking a look. This repo is a personal-pipeline tool built in the open as an AgentOps reference. Contributions are welcome, especially around the agent patterns, observability, and the OpenClaw integration.

## Ways to contribute

- **File an issue.** Bug reports, design questions, or "have you considered…" ideas all land in [GitHub Issues](../../issues). Use the templates.
- **Open a pull request.** For anything beyond a typo, please open an issue first so we can align on scope before you sink time into it.
- **Improve docs.** The architecture docs and ADRs are first-class. Doc PRs are very welcome.

## Ground rules

- **No real data, no secrets.** Sample data is sanitized fictional events. No real CFP details, no real contact info, no connection strings, no keys, no subscription IDs — not even commented out. See [SECURITY.md](SECURITY.md).
- **Microsoft Agent Framework — never Semantic Kernel.** That name is retired. If you see it, treat it as a bug.
- **Microsoft-first stack.** Foundry, Azure SDKs, managed identity, App Insights. Deviations should be justified in an ADR under [docs/adr/](docs/adr/).
- **Production-shaped, not toy.** Real auth, real observability, real idempotency. If a pattern wouldn't survive in a client engagement, it doesn't belong here.

## Local development

Phase 2 will fill this in with concrete `dotnet` / `pwsh` / `openclaw` commands. For Phase 1 (scaffolding), the only check is markdown rendering. Preview your changes on GitHub before requesting review.

## Commit style

This repo uses [Conventional Commits](https://www.conventionalcommits.org/):

```
feat: add Sessionize discovery agent
fix: prevent duplicate Events upserts when slug collides
docs: clarify partition strategy in schema doc
chore: bump Azure.Data.Tables to 12.x
```

Common prefixes: `feat`, `fix`, `docs`, `chore`, `refactor`, `test`, `ci`.

## Pull request expectations

- Keep PRs focused — one concern per PR.
- Update or add docs when behavior changes.
- For architectural choices, add an ADR ([docs/adr/](docs/adr/)) instead of burying the decision in a commit message.
- Be kind in review. This is a learning artifact as much as a tool.

## Code of conduct

Be respectful, assume good faith, and remember this repo is a public reference. If something feels off, email **brian@brianhaydin.com** directly.
