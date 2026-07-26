// Regression: code-review finding B38 — ReadAssetTool.ReadPropertyValue's Enum
// branch only checked the upper bound (`enumDisplayNames.Length > enumValueIndex`),
// so the -1 Unity reports for combined [Flags] bits indexed out of range, threw,
// and the outer catch in SerializeFields aborted the WHOLE field iteration —
// dropping that field and every field after it. The fix guards both bounds and
// falls back to the numeric value; SerializeFields also wraps each field read so
// one bad field is skipped rather than aborting the rest.
using NUnit.Framework;
using UnityEditor;
using UnityOpenMcpBridge.MetaTools;
using UnityEngine;

namespace UnityOpenMcpBridge.Tests
{
    // ScriptableObject with a [Flags] enum field. Combined flag bits make Unity
    // report enumValueIndex = -1 (no single named entry), the case that triggered
    // the out-of-range throw. A trailing int field proves the iteration continues
    // past the enum (pre-fix it was dropped along with the enum).
    public class FlagsEnumReadAssetFixture : ScriptableObject
    {
        [System.Flags]
        public enum Mode
        {
            None = 0,
            Read = 1,
            Write = 2,
            Execute = 4
        }

        public Mode access = Mode.None;
        public int trailingField = 0;
    }

    public class ReadAssetEnumFlagsTests
    {
        // B38 — combined [Flags] bits (enumValueIndex = -1) must NOT throw and
        // must fall back to the numeric value rather than indexing out of range.
        [Test]
        public void ReadPropertyValue_CombinedFlagBits_DoesNotThrow()
        {
            var so = ScriptableObject.CreateInstance<FlagsEnumReadAssetFixture>();
            try
            {
                // Read | Write | Execute = 7 — a combination with no single named
                // entry, so Unity reports enumValueIndex = -1.
                so.access = FlagsEnumReadAssetFixture.Mode.Read
                            | FlagsEnumReadAssetFixture.Mode.Write
                            | FlagsEnumReadAssetFixture.Mode.Execute;

                using (var serialized = new SerializedObject(so))
                {
                    var prop = serialized.FindProperty("access");
                    Assert.IsNotNull(prop, "access property must resolve");
                    // Pre-fix this threw IndexOutOfRangeException and the caller
                    // (SerializeFields) aborted the iteration. Now it must return
                    // a string fallback (the numeric index, -1) without throwing.
                    Assert.DoesNotThrow(() =>
                    {
                        var value = ReadAssetTool.ReadPropertyValue(prop);
                        Assert.IsNotNull(value, "Enum value must not be null");
                    });
                }
            }
            finally
            {
                Object.DestroyImmediate(so);
            }
        }

        // B38 — the SerializeFields iteration must continue past a [Flags] enum
        // field and still emit the trailing field. Pre-fix the outer catch
        // aborted the whole walk, dropping every field after the enum.
        [Test]
        public void SerializeFields_FlagsEnumDoesNotAbortIteration()
        {
            var so = ScriptableObject.CreateInstance<FlagsEnumReadAssetFixture>();
            try
            {
                so.access = FlagsEnumReadAssetFixture.Mode.Read
                            | FlagsEnumReadAssetFixture.Mode.Write;
                so.trailingField = 42;

                // field_limit must be > 0 to engage the field walk; request more
                // than the two fields the fixture has so both are considered.
                var fields = ReadAssetTool_TestAccess.SerializeFields(so, 16);

                // The trailing int field MUST appear even though the enum field
                // preceding it has combined flag bits. Pre-fix the enum throw
                // aborted the iteration and trailingField never appeared.
                bool hasTrailing = false;
                bool hasAccess = false;
                foreach (var kv in fields)
                {
                    if (kv.Key == "access") hasAccess = true;
                    if (kv.Key == "trailingField")
                    {
                        hasTrailing = true;
                        Assert.AreEqual("42", kv.Value,
                            "trailing int field must serialize its value");
                    }
                }
                Assert.IsTrue(hasTrailing,
                    "trailingField must survive the [Flags] enum field preceding it " +
                    "(pre-fix the enum throw aborted the whole iteration)");
                Assert.IsTrue(hasAccess,
                    "access field must be present (skipping it would also be a regression)");
            }
            finally
            {
                Object.DestroyImmediate(so);
            }
        }
    }

    // Test-only access to the internal SerializeFields helper. Keeps the
    // production API surface minimal while letting the EditMode suite exercise
    // the iteration directly without round-tripping through an on-disk asset.
    internal static class ReadAssetTool_TestAccess
    {
        public static System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>
            SerializeFields(Object asset, int fieldLimit)
        {
            return ReadAssetTool.SerializeFields(asset, fieldLimit);
        }
    }
}
