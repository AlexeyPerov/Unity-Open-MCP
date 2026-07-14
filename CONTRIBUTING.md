# Contributing

Bug reports, feature proposals, documentation fixes, and pull requests are
welcome.

## Before you start

1. Search existing issues and pull requests before opening a new one.
2. Use [Development setup](docs/setup/development-setup.md) to prepare a local
   checkout.
3. Read the nearest `AGENTS.md` and package README for the area you will change.
   These files own package-specific architecture, validation, and documentation
   requirements.
4. For embedded domains or community packs, also follow
   [Contributing — extensions](docs/contributing/extensions.md).

The main ownership guides are:

- [`packages/bridge/AGENTS.md`](packages/bridge/AGENTS.md) for bridge transport,
  typed tools, registration, and mutation gates.
- [`packages/verify/AGENTS.md`](packages/verify/AGENTS.md) for verification
  rules, issue codes, and fixes.
- [`packages/extensions/AGENTS.md`](packages/extensions/AGENTS.md) for community
  packs.
- [`mcp-server/AGENTS.md`](mcp-server/AGENTS.md) and
  [`mcp-server/README.md`](mcp-server/README.md) for MCP tools, routing, and the
  Node server.
- [`hub/AGENTS.md`](hub/AGENTS.md) and [`hub/README.md`](hub/README.md) for
  Unity Hub Pro.

## Issues

Use the issue forms and include enough information to reproduce the behavior:

- Unity and operating-system versions;
- Unity Open MCP server, bridge, and verify versions;
- relevant Unity package versions;
- minimal reproduction steps;
- expected and actual behavior;
- logs or screenshots with secrets and private project data removed.

Use feature requests to describe the workflow and motivation, not only a
proposed implementation.

## Pull requests

Keep each pull request focused. Explain the problem, the chosen approach, and
any user-visible or contract changes. Link the related issue when one exists.

Before requesting review:

- run the validation required by the nearest `AGENTS.md` or package README;
- add or update tests for behavior changes;
- update the owning public documentation when behavior, contracts, or
  contributor workflows change;
- report the commands and manual checks you ran;
- confirm generated files and version-synchronized targets are current.

For CI pipeline shape and commands, see [CI templates](docs/ci/README.md). For
local bridge, worker, and test-suite failures, see
[Contributor troubleshooting](docs/troubleshooting-contributors.md).

## Releases

Maintainers should follow
[Maintainer versioning and releases](docs/contributing/versioning.md). GitHub
Releases own release notes and changelog history; the repository does not
maintain a duplicate `CHANGELOG.md`.
