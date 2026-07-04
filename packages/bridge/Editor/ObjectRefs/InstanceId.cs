// Central instance-ID helper — isolates the Unity-version dance for reading
// and resolving instance IDs behind one pair of methods. Every typed tool and
// extension that emits or consumes an instanceId field goes through here so the
// #if version-gating lives in one audited place.
//
// See docs/code-conventions.md §Instance IDs for the contract.
//
// Unity 6000.0+ introduced UnityEngine.EntityId (an opaque struct) and marked
// the int APIs [Obsolete]. Unity 6000.5 escalated the diagnostic to CS0619
// (error) AND widened EntityId to 8 bytes — values like 568105589213726936 no
// longer fit in int and exceed JS Number.MAX_SAFE_INTEGER (2^53). The
// agent-facing instanceId / objectId / gameObjectId JSON fields are therefore
// serialized as STRINGS (e.g. "568105589213726936") for full fidelity.
//
// On UNITY_6000_0_OR_NEWER the helper uses EntityId.ToULong()/FromULong(); on
// older Unity it uses GetInstanceID()/InstanceIDToObject(int). In both cases
// the wire value is a long (the legacy int widens cleanly), and is serialized
// as a JSON string.
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.ObjectRefs
{
    // Public so the companion extension packs (UnityOpenMcpExtensions.*) —
    // separate assemblies that reference the bridge — can share the same
    // version-gated instance-ID read/resolve path.
    public static class InstanceId
    {
        /// <summary>
        /// Read the instance ID of a live Object as a long. Use this for any
        /// internal arithmetic; for JSON serialization use <see cref="ToJson"/>
        /// (which emits the quoted-string form agents expect).
        /// </summary>
        public static long Of(Object obj)
        {
#if UNITY_6000_0_OR_NEWER
            return (long)UnityEngine.EntityId.ToULong(obj.GetEntityId());
#else
            return obj.GetInstanceID();
#endif
        }

        /// <summary>
        /// Append the instance ID of <paramref name="obj"/> to a StringBuilder
        /// as a JSON string token (with surrounding quotes), e.g. "12345".
        /// Returns 0 (quoted) when obj is null. This is the canonical JSON form
        /// for the instanceId / objectId / gameObjectId fields — the value is a
        /// string so it round-trips losslessly across JS / JSON even when the
        /// underlying EntityId exceeds Number.MAX_SAFE_INTEGER (Unity 6000.5+).
        /// </summary>
        public static string ToJson(Object obj)
        {
            if (obj == null) return "\"0\"";
            return "\"" + Of(obj).ToString(CultureInfo.InvariantCulture) + "\"";
        }

        /// <summary>
        /// Resolve a string-or-long instance ID back to a live Object. Returns
        /// null if the id is null/empty/"0" or no longer maps to a loaded
        /// Object (e.g. after a domain reload). Accepts both the canonical
        /// quoted-string form ("12345") and a bare long/int for backward
        /// compatibility with older clients.
        /// </summary>
        public static Object ToObject(long id)
        {
            if (id == 0) return null;
#if UNITY_6000_0_OR_NEWER
            return EditorUtility.EntityIdToObject(ToEntityId(id));
#else
            return EditorUtility.InstanceIDToObject((int)id);
#endif
        }

        /// <summary>
        /// Parse an instance-ID wire value (string or long) into a long.
        /// Strings are parsed invariant-culture; longs pass through. Returns 0
        /// on null/empty/unparseable input.
        /// </summary>
        public static long Parse(object value)
        {
            if (value == null) return 0;
            if (value is long l) return l;
            if (value is int i) return i;
            if (value is string s)
            {
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
            }
            return 0;
        }

        /// <summary>
        /// Build a UnityEngine.EntityId from the long wire value (negative ids
        /// preserved through a uint cast). 6000.0+ only.
        /// </summary>
#if UNITY_6000_0_OR_NEWER
        internal static UnityEngine.EntityId ToEntityId(long id)
            => UnityEngine.EntityId.FromULong((ulong)id);
#endif
    }
}
