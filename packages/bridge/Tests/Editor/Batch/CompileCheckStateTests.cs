using System.Collections.Generic;
using NUnit.Framework;
using UnityOpenMcpBridge.Batch;
using UnityEditor.Compilation;

namespace UnityOpenMcpBridge.Tests
{
    public static class CompileCheckStateTests
    {
        // A PendingState created with the default field-initializer timeout must
        // match the public default the server clamps to. Keeps the two
        // duplicated constants honest (see PendingState doc comment).
        [Test]
        public static void PendingState_DefaultTimeout_MatchesPublicDefault()
        {
            var pending = new PendingState();
            Assert.AreEqual(300_000, pending.timeoutMs);
        }

        [Test]
        public static void CollectErrors_OnlyKeepsErrorSeverity()
        {
            var pending = new PendingState();
            var messages = new CompilerMessage[]
            {
                MakeMessage("warning CS0219: unused variable", CompilerMessageType.Warning),
                MakeMessage("error CS0103: name does not exist", CompilerMessageType.Error),
                MakeMessage("error CS0246: type not found", CompilerMessageType.Error),
            };

            CompileCheckState.CollectErrors(pending, "Assembly-CSharp.dll", messages);

            Assert.AreEqual(2, pending.errors.Count);
            Assert.AreEqual("CS0103", pending.errors[0].code);
            Assert.AreEqual("CS0246", pending.errors[1].code);
        }

        [Test]
        public static void CollectErrors_CapsAtMaxErrors()
        {
            var pending = new PendingState();
            // Build well over MaxErrors messages and confirm the count is capped,
            // not unbounded — a catastrophically broken project must not produce
            // a multi-megabyte payload.
            var messages = new List<CompilerMessage>();
            for (int i = 0; i < CompileCheckState.MaxErrors + 50; i++)
            {
                messages.Add(MakeMessage($"error CS100{i % 10}: msg {i}", CompilerMessageType.Error));
            }

            CompileCheckState.CollectErrors(pending, "A.dll", messages.ToArray());

            Assert.AreEqual(CompileCheckState.MaxErrors, pending.errors.Count);
        }

        [Test]
        public static void CollectErrors_TracksDistinctAssemblies()
        {
            var pending = new PendingState();
            CompileCheckState.CollectErrors(
                pending,
                "A.dll",
                new[] { MakeMessage("error CS0001: a", CompilerMessageType.Error) });
            CompileCheckState.CollectErrors(
                pending,
                "A.dll", // duplicate — must not double-count
                new[] { MakeMessage("error CS0002: a2", CompilerMessageType.Error) });
            CompileCheckState.CollectErrors(
                pending,
                "B.dll",
                new[] { MakeMessage("error CS0003: b", CompilerMessageType.Error) });

            Assert.AreEqual(2, pending.assembliesSeen.Count);
            Assert.IsTrue(pending.assembliesSeen.Contains("A.dll"));
            Assert.IsTrue(pending.assembliesSeen.Contains("B.dll"));
        }

        [Test]
        public static void BuildResultJson_ReportsPassedWhenNoErrors()
        {
            var pending = new PendingState { assembliesSeen = new List<string> { "A.dll" } };
            var json = CompileCheckState.BuildResultJson(pending, timedOut: false);

            Assert.IsTrue(json.Contains("\"status\":\"compile_passed\""));
            Assert.IsTrue(json.Contains("\"errorCount\":0"));
            Assert.IsTrue(json.Contains("\"assembliesChecked\":1"));
            Assert.IsFalse(json.Contains("\"timedOut\""));
        }

        [Test]
        public static void BuildResultJson_ReportsFailedWithErrors()
        {
            var pending = new PendingState();
            CompileCheckState.CollectErrors(
                pending,
                "A.dll",
                new[]
                {
                    MakeMessage("error CS0246: The type 'Foo' could not be found", CompilerMessageType.Error),
                });

            var json = CompileCheckState.BuildResultJson(pending, timedOut: false);

            Assert.IsTrue(json.Contains("\"status\":\"compile_failed\""));
            Assert.IsTrue(json.Contains("\"errorCount\":1"));
            Assert.IsTrue(json.Contains("\"CS0246\""));
            Assert.IsFalse(json.Contains("\"timedOut\""));
        }

        // Acceptance: a compile_check against a known-broken script (a missing
        // type reference — the canonical CS0246) must surface the structured CS
        // error. This is the fixture case from the task spec: rather than spin
        // up a live CompilationPipeline (non-deterministic in EditMode), we feed
        // the exact CompilerMessage the pipeline would emit for a broken script
        // through the collection + result-building steps and assert the CS error
        // is present with its locator fields.
        [Test]
        public static void KnownBrokenScript_SurfacesCsError()
        {
            var pending = new PendingState();
            var broken = new CompilerMessage
            {
                // Realistic Unity diagnostic line for Assets/Broken.cs:10.
                message = "error CS0246: The type or namespace name 'MissingType' could not be found (are you missing a using directive or an assembly reference?)",
                type = CompilerMessageType.Error,
                file = "Assets/Broken.cs",
                line = 10,
                column = 14,
            };

            CompileCheckState.CollectErrors(pending, "Assembly-CSharp.dll", new[] { broken });

            var json = CompileCheckState.BuildResultJson(pending, timedOut: false);

            Assert.IsTrue(json.Contains("\"status\":\"compile_failed\""), "status must be compile_failed");
            Assert.IsTrue(json.Contains("\"errorCount\":1"));
            Assert.IsTrue(json.Contains("\"CS0246\""), "CS error code must be present");
            Assert.IsTrue(json.Contains("\"Assets/Broken.cs\""), "source file must be present");
            Assert.IsTrue(json.Contains("\"line\":10"), "line number must be present");
            Assert.IsTrue(json.Contains("MissingType"), "error message text must be present");
        }

        [Test]
        public static void BuildResultJson_IncludesTimedOutFlagWhenSet()
        {
            var pending = new PendingState();
            var json = CompileCheckState.BuildResultJson(pending, timedOut: true);
            Assert.IsTrue(json.Contains("\"timedOut\":true"));
        }

        [Test]
        public static void BuildResultJson_EscapesQuotesInMessages()
        {
            var pending = new PendingState();
            // Synthesize an error whose message contains a quote to confirm the
            // JSON body is escaped, not emitted raw (which would break parsing).
            pending.errors.Add(new CompileError
            {
                assembly = "A.dll",
                code = "CS0246",
                message = "The type \"Foo\" was not found",
                file = "Assets/Broken.cs",
                line = 12,
            });

            var json = CompileCheckState.BuildResultJson(pending, timedOut: false);

            // Escaped form: \"Foo\" — never a bare, unescaped quote inside the value.
            StringAssert.Contains("\\\"Foo\\\"", json);
        }

        [TestCase("error CS0246: The type 'Foo' could not be found", ExpectedResult = "CS0246")]
        [TestCase("  error CS1002: ; expected", ExpectedResult = "CS1002")]
        [TestCase("no code here", ExpectedResult = "")]
        [TestCase("", ExpectedResult = "")]
        public static string ExtractCode_PullsCsToken(string message, string expected)
        {
            return CompileCheckState.ExtractCode(message);
        }

        private static CompilerMessage MakeMessage(string message, CompilerMessageType type)
        {
            return new CompilerMessage
            {
                message = message,
                type = type,
                file = "Assets/Test.cs",
                line = 1,
                column = 1,
            };
        }
    }
}
