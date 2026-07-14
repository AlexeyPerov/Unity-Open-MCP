# Code conventions

Non-obvious decisions in the C# packages that are worth writing down once and
pointing to from the source, instead of repeating a paragraph-long comment at
every call site.

## Instance IDs

Unity 6000.0+ introduced `UnityEngine.EntityId` (an opaque struct) and marked
the int instance-ID APIs `[Obsolete]`:

- `Object.GetInstanceID()` → `Object.GetEntityId()`
- `EditorUtility.InstanceIDToObject(int)` → `EditorUtility.EntityIdToObject(EntityId)`

Unity 6000.5 escalated the diagnostic from CS0618 (warning) to CS0619 (error)
and widened `EntityId` from 4 to 8 bytes. The int APIs can no longer be used on
6000.5+ at all (no pragma suppresses CS0619).

The bridge keeps the agent-facing instance-ID contract but routes every read
and resolve through one central, version-gated helper so the `#if` lives in one
audited place:

[`packages/bridge/Editor/ObjectRefs/InstanceId.cs`](../packages/bridge/Editor/ObjectRefs/InstanceId.cs)

```csharp
InstanceId.Of(obj)        // long — the raw id (any Unity version)
InstanceId.ToJson(obj)    // string — the JSON form: "12345" (quoted, lossless)
InstanceId.ToObject(id)   // Object — resolve a long id (any Unity version)
```

**JSON wire format: STRING.** The `instanceId` / `objectId` / `gameObjectId`
fields are serialized as quoted JSON strings (e.g. `"568105589213726936"`), not
bare numbers. This is required because Unity 6000.5 widened `EntityId` to 8
bytes — values exceed JS `Number.MAX_SAFE_INTEGER` (2^53) and would lose
precision if serialized as JSON numbers. The parser
(`JsonBody.GetLongFlexible`) accepts both the canonical string form and a bare
number for backward compatibility with older clients.

Under the hood, on `UNITY_6000_0_OR_NEWER` the helper calls
`EntityId.ToULong(obj.GetEntityId())` (returns `ulong`, cast to `long`) and
`EditorUtility.EntityIdToObject(FromULong((ulong)id))`; on older Unity it
falls back to `GetInstanceID()` (int, widened to long) /
`InstanceIDToObject((int)id)`.

### How this shows up in the source

Every typed tool and community pack that emits or consumes an instanceId field uses
`InstanceId.Of(...)` / `InstanceId.ToObject(...)` and carries a
`using UnityOpenMcpBridge.ObjectRefs;`. The helper is `public` so the
companion community packs (separate assemblies referencing the bridge) share
the same path. Do not call `GetInstanceID()` / `InstanceIDToObject()` /
`GetEntityId()` / `EntityIdToObject()` directly outside the helper — the
version gating and the int↔EntityId round-trip must stay centralised.

The canonical JSON-handle contract lives in
[`packages/bridge/Editor/ObjectRefs/ObjectHandle.cs`](../packages/bridge/Editor/ObjectRefs/ObjectHandle.cs);
the typed-tool and extension files that emit or consume instance IDs follow the
same rule via the helper.

### History

The bridge previously suppressed CS0618 (and, briefly, CS0619) with pragmas and
kept calling the deprecated int APIs directly. Unity 6000.5's CS0619 escalation
made that untenable, AND the 8-byte EntityId widening meant the int contract
itself was lossy. The version-gated `InstanceId` helper now owns that contract
across the bridge and community-pack call sites: `long` internally and a
lossless string on the wire.

## Namespace/type shadowing

Some community packs put their domain in a namespace whose last segment matches
a `UnityEngine` type. The clearest case is the Particle System pack: its
original namespace `UnityOpenMcpExtensions.ParticleSystem` collided with
`UnityEngine.ParticleSystem`, so a bare `ParticleSystem` reference (and the
nested `ParticleSystem.MainModule` / `.MinMaxCurve` / …) resolved to the
namespace instead of the type, producing CS0118 / CS0234 errors.

A `using ParticleSystem = UnityEngine.ParticleSystem;` alias does **not** fix
this — inside `namespace …ParticleSystem`, the name `ParticleSystem` always
binds to the enclosing namespace; namespace lookup beats a same-named alias
(C# language spec). The only robust fix is to name the namespace so it does not
collide. The pack uses `UnityOpenMcpExtensions.ParticleSystemExt`:

```csharp
namespace UnityOpenMcpExtensions.ParticleSystemExt { ... }
```

so `ParticleSystem` and its nested module types resolve to `UnityEngine.*`
unambiguously. The asmdef `rootNamespace` is kept in sync. Apply the same
rename-first approach to any future pack whose domain matches a `UnityEngine`
type name. (A different problem — a pack whose domain types shadow a
`UnityEngine.*` namespace rather than a single type — is handled by aliasing;
see the NavMesh rule in [`packages/extensions/AGENTS.md`](../packages/extensions/AGENTS.md).)

## Related docs

- [Architecture](architecture.md) — package boundaries and runtime flow.
