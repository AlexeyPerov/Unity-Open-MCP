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
        public int Line { get; }
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
