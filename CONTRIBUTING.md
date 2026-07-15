# Contributing

Bug reports, feature proposals, documentation fixes, and pull requests are
welcome.

## Before you start

1. Search existing issues and pull requests before opening a new one.
2. Use [Development setup](docs/setup/development-setup.md) to prepare a local
   checkout.
3. Repository contribution and automation guidance follows layered
   [`AGENTS.md`](AGENTS.md) files: start at the root, then every touched
   subtree. These files are not a substitute for user-facing docs under
   `docs/`. The root layer inventory lists every local rule file.
4. For embedded domains or community packs, also follow
   [Contributing — extensions](docs/contributing/extensions.md).

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
