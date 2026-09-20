# Project command fixture

This embedded Editor-only package references the bridge assembly and requires no
server-side command registration. Install the local bridge and verify packages,
open the demo, and wait for a clean compile. The [authoring contract](../../../docs/api/project-commands.md)
contains a minimal copy-pasteable assembly and command example.

1. List `unity_open_mcp_project_commands` with query `project.demo`.
2. Describe `project.demo.catalog_fixture`; invoke with `args: {"labels": null}`.
3. Describe `project.demo.long_write`; start `unity_open_mcp_jobs` with
   `tool_or_command: "project.demo.long_write"`, a fresh `idempotency_key`, and
   `args: {"args": {"seconds": 60}, "gate": "enforce"}`.
4. Call status, then bounded wait until terminal; check the retained gate and
   `Assets/_ValidationSuite/ProjectCommands/invocation.txt`. Same-key retries
   must return the same job. Cancel during preparation to avoid the final write.
5. `project.demo.partial_output` deliberately writes a disposable file before
   returning invalid JSON or throwing. Expect failed execution with terminal
   validation, not rollback or success.

Use only the disposable fixture folder. Preserve any pre-existing fixture files
when testing and restore them afterward. `reload_fixture` requests compilation;
run it only when no unrelated Editor work is in progress. Direct synchronous
invocation of an async declaration refuses without starting it.
