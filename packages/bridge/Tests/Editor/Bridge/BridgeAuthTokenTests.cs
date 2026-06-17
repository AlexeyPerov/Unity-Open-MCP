using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // M14 — Unit tests for the token mint + comparison helpers. These run
    // without a live HttpListener (unlike BridgeHttpServerTests) because the
    // auth decision itself is pure: BridgeAuthCheck calls into this class.
    public class BridgeAuthTokenTests
    {
        [Test]
        public void Generate_Returns64LowerHexChars()
        {
            var token = BridgeAuthToken.Generate();
            Assert.IsNotNull(token);
            Assert.AreEqual(BridgeAuthToken.HexLength, token.Length,
                $"Token must be {BridgeAuthToken.HexLength} hex chars. Got: {token}");
            Assert.IsTrue(Regex.IsMatch(token, "^[0-9a-f]+$"),
                $"Token must be lowercase hex. Got: {token}");
        }

        [Test]
        public void Generate_IsRandom()
        {
            // Two consecutive mints must differ. (Cryptographically this fails
            // with vanishing probability; a flake here means the RNG is broken.)
            var a = BridgeAuthToken.Generate();
            var b = BridgeAuthToken.Generate();
            Assert.AreNotEqual(a, b, "Generate must produce distinct tokens.");
        }

        [Test]
        public void EqualsConstantTime_EqualStrings_ReturnsTrue()
        {
            var t = BridgeAuthToken.Generate();
            Assert.IsTrue(BridgeAuthToken.EqualsConstantTime(t, t));
        }

        [Test]
        public void EqualsConstantTime_DifferentStrings_ReturnsFalse()
        {
            var a = BridgeAuthToken.Generate();
            var b = BridgeAuthToken.Generate();
            Assert.IsFalse(BridgeAuthToken.EqualsConstantTime(a, b));
        }

        [Test]
        public void EqualsConstantTime_DifferentLengths_ReturnsFalse()
        {
            Assert.IsFalse(
                BridgeAuthToken.EqualsConstantTime("abcdef", "abcdef0"));
        }

        [Test]
        public void EqualsConstantTime_NullInputs_DoNotThrow()
        {
            // null is coerced to "" — must never throw, and two nulls compare equal.
            Assert.IsTrue(BridgeAuthToken.EqualsConstantTime(null, null));
            Assert.IsFalse(BridgeAuthToken.EqualsConstantTime(null, "x"));
            Assert.IsFalse(BridgeAuthToken.EqualsConstantTime("x", null));
        }

        [Test]
        public void ExtractBearer_ParsesWellFormedHeader()
        {
            var token = BridgeAuthToken.Generate();
            Assert.AreEqual(token, BridgeAuthToken.ExtractBearer($"Bearer {token}"));
        }

        [Test]
        public void ExtractBearer_ToleratesCaseAndWhitespace()
        {
            var token = BridgeAuthToken.Generate();
            Assert.AreEqual(token, BridgeAuthToken.ExtractBearer($"bearer   {token} "));
            Assert.AreEqual(token, BridgeAuthToken.ExtractBearer($"BEARER {token}"));
        }

        [Test]
        public void ExtractBearer_RejectsMissingOrNonBearer()
        {
            Assert.IsNull(BridgeAuthToken.ExtractBearer(null));
            Assert.IsNull(BridgeAuthToken.ExtractBearer(""));
            Assert.IsNull(BridgeAuthToken.ExtractBearer("Basic abc"));
            Assert.IsNull(BridgeAuthToken.ExtractBearer("Bearer")); // scheme but no token
            Assert.IsNull(BridgeAuthToken.ExtractBearer("Bearer ")); // whitespace only
        }
    }
}
