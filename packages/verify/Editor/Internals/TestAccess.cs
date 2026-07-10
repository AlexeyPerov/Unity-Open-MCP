using System.Runtime.CompilerServices;

// Exposes verify-assembly internals to the verify test assembly so tests can
// unit-test internal orchestration entry points (e.g. VerifyBatchEntry.Execute)
// without routing through the public -batchmode Run() path that calls
// EditorApplication.Exit. Mirrors the bridge assembly's InternalsVisibleTo
// declarations (TestRunnerService.cs / OutputSerializer.cs).
[assembly: InternalsVisibleTo("com.alexeyperov.unity-open-mcp-verify.Editor.Tests")]
