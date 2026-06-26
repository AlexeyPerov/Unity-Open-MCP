# Code conventions

Non-obvious decisions in the C# packages that are worth writing down once and
pointing to from the source, instead of repeating a paragraph-long comment at
every call site.

## Instance IDs

Unity 6000.4 deprecates the int instance-ID APIs and marks them
`[Obsolete]`:

- `Object.GetInstanceID()` → `Object.GetEntityId()`
- `EditorUtility.InstanceIDToObject(int)` → `EntityIdToObject(...)`

The replacement APIs only exist on Unity 6000.4 and newer, while the packages
declare a minimum of 2022.3 LTS (`packages/*/package.json` `"unity": "2022.3"`),
so a blind search-and-replace would not compile on the supported floor.

More importantly, `GetEntityId()` returns **different values** than the int
instance ID. The bridge's JSON object-handle contract — the `objectId` /
`gameObjectId` / `instanceId` fields agents read out of every snapshot and pass
back into mutating tools — is built on the **stable int instance ID**. Handles
produced today must keep round-tripping against that contract, so the
deprecated int APIs are used deliberately wherever a JSON handle is serialized
or resolved.

The canonical home for that contract is
[`packages/bridge/Editor/ObjectRefs/ObjectHandle.cs`](../packages/bridge/Editor/ObjectRefs/ObjectHandle.cs);
the typed-tool and extension files that emit or consume instance IDs follow the
same rule.

### How this shows up in the source

Every file that touches the deprecated APIs carries a one-line pointer and a
pragma suppression at the top, e.g.:

```csharp
// Deliberate use of deprecated GetInstanceID() — see docs/code-conventions.md §Instance IDs.
#pragma warning disable CS0618
```

The suppression is intentional. Do not "fix" these by swapping to
`GetEntityId()` / `EntityIdToObject()` — that would silently change the values
in the agent-facing JSON and break round-tripping.

## Namespace/type shadowing

Some extension packs put their domain in a namespace whose last segment matches
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
