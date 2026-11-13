using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityOpenMcpBridge.Console;

namespace UnityOpenMcpBridge.Tests
{
    // read_console message/row filters: types (several severities), contains
    // (case-insensitive substring), pattern (case-sensitive regex) and the
    // since_index row cursor.
    public class ReadConsoleFilterTests
    {
        private static List<LogEntryInfo> Rows() => new List<LogEntryInfo>
        {
            new LogEntryInfo { Index = 0, Mode = 0, Message = "Boot complete" },
            new LogEntryInfo { Index = 1, Mode = 512, Message = "ObeliskAttempts: retry 1" },
            new LogEntryInfo { Index = 2, Mode = 256, Message = "NullReferenceException in ObeliskAttempts" },
            new LogEntryInfo { Index = 3, Mode = 0, Message = "obeliskattempts lower-case log" },
        };

        [Test]
        public void TypesSelectSeveralSeveritiesAndExceptionCountsAsError()
        {
            var picked = Tool_ReadConsole.FilterByType(Rows(), "all", new[] { "exception", "warning" });
            CollectionAssert.AreEqual(new[] { 1, 2 }, picked.ConvertAll(e => e.Index));
            Assert.AreEqual(4, Tool_ReadConsole.FilterByType(Rows(), "error", new[] { "all" }).Count);
            Assert.AreEqual(1, Tool_ReadConsole.FilterByType(Rows(), "error").Count, "type still applies without types");
        }

        [Test]
        public void ContainsIsCaseInsensitiveAndPatternIsCaseSensitive()
        {
            var byText = Tool_ReadConsole.FilterByText(Rows(), "OBELISKATTEMPTS", null, 0);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, byText.ConvertAll(e => e.Index));
            var byPattern = Tool_ReadConsole.FilterByText(Rows(), null, new Regex("^Obelisk\\w+:"), 0);
            CollectionAssert.AreEqual(new[] { 1 }, byPattern.ConvertAll(e => e.Index));
            var both = Tool_ReadConsole.FilterByText(Rows(), "exception", new Regex("Obelisk"), 0);
            CollectionAssert.AreEqual(new[] { 2 }, both.ConvertAll(e => e.Index));
        }

        [Test]
        public void SinceIndexSkipsRowsAlreadyRead()
        {
            var newer = Tool_ReadConsole.FilterByText(Rows(), null, null, 2);
            CollectionAssert.AreEqual(new[] { 2, 3 }, newer.ConvertAll(e => e.Index));
            Assert.AreEqual(4, Tool_ReadConsole.FilterByText(Rows(), null, null, 0).Count);
        }

        [Test]
        public void InvalidPatternIsAnArgumentError()
        {
            var json = new Tool_ReadConsole().ReadConsole(pattern: "(unclosed");
            StringAssert.Contains("\"code\":\"invalid_arguments\"", json);
            StringAssert.Contains("pattern is not a valid regex", json);
        }
    }
}
