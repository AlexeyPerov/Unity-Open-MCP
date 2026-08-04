using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityOpenMcpVerify.Rules.MissingReferences
{
    public class ExternalReferenceRegistry
    {
        public ExternalReferenceRegistry(bool fileIdValid, bool guidValid, long fileId, string guid, int line)
        {
            FileIDValid = fileIdValid;
            GuidValid = guidValid;
            FileID = fileId;
            Guid = guid;
            Line = line;
        }

        public bool FileIDValid { get; }
        public bool GuidValid { get; }
        public long FileID { get; }
        public string Guid { get; }
        public int Line { get; }
        public string FieldType { get; set; }
        public List<string> Sample { get; } = new List<string>();
        public bool FileIDExistsInAssets { get; set; }
        public bool GuidExistsInAssets { get; set; }
        public string GuidAssetPath { get; set; }
        public bool FileIDExistsInTargetAsset { get; set; }
        public string HolderName { get; set; }
        public int WarningLevel { get; private set; }

        public void UpdateWarningLevel()
        {
            WarningLevel = 0;
            if (FileIDValid && !FileIDExistsInAssets) WarningLevel++;
            if (GuidValid && !GuidExistsInAssets) WarningLevel++;
            if (GuidValid && GuidExistsInAssets && FileIDValid && !FileIDExistsInTargetAsset) WarningLevel++;
        }
    }

    public class LocalReferenceRegistry
    {
        public LocalReferenceRegistry(long id, int line)
        {
            Id = id;
            IdStr = id.ToString();
            Line = line;
        }

        public bool IdValid => Id > 0;
        public long Id { get; }
        public string IdStr { get; }
        public int Line { get; }
        public int LocalUsagesCount { get; set; }
        public bool ExistsInAssets { get; set; }
    }

    public class EmptyLocalFileIDRegistry
    {
        public EmptyLocalFileIDRegistry(int line) { Line = line; }

        public EmptyLocalFileIDRegistry(int line, long anchor, string property)
        {
            Line = line;
            Anchor = anchor;
            Property = property;
        }

        public int Line { get; }

        // feedback-fable-31-07 §7 — the owning top-level YAML object's fileID
        // (the `&NNN` on the most recent `--- !u!T &NNN` header before this
        // `{fileID: 0}` line) and the serialized property key (e.g.
        // "m_Father", "m_StaticBatchRoot"). Together they key the issue so a
        // delta means something on large scenes: adding an unrelated empty ref
        // elsewhere no longer renames hundreds of issues (the old ordinal keys
        // shifted on every count change). 0 / null when the scanner could not
        // determine them; the mapper falls back to the line in that case.
        public long Anchor { get; }
        public string Property { get; }

        // feedback-04-08-opus §4 — content-addressed identity of the empty-ref
        // site. An anchor fileID is NOT content: an editor script that rebuilds
        // a prefab renumbers every anchor, so the whole issue set renames again
        // (the report saw 729 resolved + 729 new on an idempotent rebuild). The
        // object's transform PATH ("LobbyChrome/LeftOffersTab/Content") plus the
        // property ("PromotionTimer") survives renumbering — that is what makes
        // a delta mean something across a rebuild. Filled by a post-pass after
        // the anchor→metadata map is built; null when a path could not be
        // resolved (the mapper then falls back to the anchor id).
        public string TransformPath { get; set; }

        // feedback-04-08-opus §5 — the GUID of the MonoBehaviour script that
        // declares this empty-ref field (the owning component's m_Script guid),
        // when the owning object is a MonoBehaviour. Null for built-in
        // components (Transform, RectTransform, Button, Image, …). The mapper
        // uses this to split severity: a field on a user script whose GUID
        // resolves under Assets/ stays a Warning (a real, possibly-shipping
        // null); the same field on a built-in Unity/package component is demoted
        // to Info (empty-by-default noise — m_SelectOn*, sprite-swap sprites,
        // TMP material/style fields — that buries the real bugs under a 98 %
        // noise floor).
        public string OwnerScriptGuid { get; set; }
    }

    public class MissingMethodEntry
    {
        public MissingMethodEntry(string className, string methodName, int line)
        {
            ClassName = className;
            MethodName = methodName;
            Line = line;
        }

        public string ClassName { get; }
        public string MethodName { get; }
        public int Line { get; }
    }

    public class TypeMismatchEntry
    {
        public TypeMismatchEntry(string typeName, int line)
        {
            TypeName = typeName;
            Line = line;
        }

        public string TypeName { get; }
        public int Line { get; }
    }

    public class MissingScriptEntry
    {
        public MissingScriptEntry(string scriptGuid, int line)
        {
            ScriptGuid = scriptGuid;
            Line = line;
        }

        public string ScriptGuid { get; }
        public int Line { get; }
    }

    public class DuplicateComponentEntry
    {
        public DuplicateComponentEntry(string componentType, int count, string gameObjectName)
        {
            ComponentType = componentType;
            Count = count;
            GameObjectName = gameObjectName;
        }

        public string ComponentType { get; }
        public int Count { get; }
        public string GameObjectName { get; }
    }

    public class InvalidLayerEntry
    {
        public InvalidLayerEntry(int layerIndex, int line)
        {
            LayerIndex = layerIndex;
            Line = line;
        }

        public int LayerIndex { get; }
        public int Line { get; }
    }

    public class AssetReferencesData
    {
        public List<ExternalReferenceRegistry> ExternalReferences { get; } = new List<ExternalReferenceRegistry>();
        public List<LocalReferenceRegistry> LocalReferences { get; } = new List<LocalReferenceRegistry>();
        public List<EmptyLocalFileIDRegistry> EmptyFileIDs { get; } = new List<EmptyLocalFileIDRegistry>();
        public List<MissingMethodEntry> MissingMethods { get; } = new List<MissingMethodEntry>();
        public List<TypeMismatchEntry> TypeMismatches { get; } = new List<TypeMismatchEntry>();
        public List<MissingScriptEntry> MissingScripts { get; } = new List<MissingScriptEntry>();
        public List<DuplicateComponentEntry> DuplicateComponents { get; } = new List<DuplicateComponentEntry>();
        public List<InvalidLayerEntry> InvalidLayers { get; } = new List<InvalidLayerEntry>();

        // feedback-01-08-glm §10 — every fileID DECLARED by a top-level YAML
        // object header (`--- !u!T &NNN`, including negative/stripped ids) in
        // this asset. AssetDatabase.LoadAllAssetsAtPath only surfaces the main
        // asset + a small subset for a .unity scene, NOT every serialized
        // object header, so scene-internal declared-but-not-loadable fileIDs
        // read as missing_local_fileid false positives. The declared-anchors
        // set is the authoritative "this fileID exists in the file" source and
        // is OR'd into ExistsInAssets resolution in ResolveReferences.
        public HashSet<long> DeclaredFileIDs { get; } = new HashSet<long>();

        public int MissingFileIDAndGuidCount { get; private set; }
        public int MissingGuidCount { get; private set; }
        public int MissingFileIDCount { get; private set; }
        public int MissingLocalFileIDCount { get; private set; }

        public bool HasWarnings => MissingFileIDAndGuidCount > 0 || MissingGuidCount > 0
            || MissingMethods.Count > 0 || TypeMismatches.Count > 0
            || MissingScripts.Count > 0 || DuplicateComponents.Count > 0 || InvalidLayers.Count > 0;

        public void CalculateCounters()
        {
            MissingFileIDAndGuidCount = ExternalReferences.Count(x =>
                x.FileIDValid && x.GuidValid && !x.FileIDExistsInAssets && !x.GuidExistsInAssets);
            MissingGuidCount = ExternalReferences.Count(x => x.GuidValid && !x.GuidExistsInAssets);
            MissingFileIDCount = ExternalReferences.Count(x =>
                x.GuidValid && x.GuidExistsInAssets && x.FileIDValid && !x.FileIDExistsInTargetAsset);
            MissingLocalFileIDCount = LocalReferences.Count(x =>
                x.IdValid && x.LocalUsagesCount == 0 && !x.ExistsInAssets);

            foreach (var registry in ExternalReferences)
                registry.UpdateWarningLevel();
        }
    }

    public class AssetData
    {
        public AssetData(string path, Type type, string typeName, string guid, AssetReferencesData refsData)
        {
            Path = path;
            Type = type;
            TypeName = typeName;
            Guid = guid;
            RefsData = refsData;
        }

        public string Path { get; }
        public Type Type { get; }
        public string TypeName { get; }
        public string Guid { get; }
        public AssetReferencesData RefsData { get; }
        public HashSet<string> MissingFieldTypes { get; } = new HashSet<string>();
        public bool ValidType => Type != null;
    }
}
