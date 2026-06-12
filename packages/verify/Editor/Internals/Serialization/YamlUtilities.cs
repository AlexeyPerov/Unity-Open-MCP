// Extracted from Unity-Scanner: Editor/Utilities/Serialization/USYamlUtilities.cs

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UnityAgentVerify.Internals.Serialization
{
    public static class YamlUtilities
    {
        public static readonly List<string> KeyWordsToIgnore = new List<string>
        {
            "objectReference: {fileID:",
            "m_CorrespondingSourceObject: {fileID:",
            "m_PrefabInstance: {fileID:",
            "m_PrefabAsset: {fileID:",
            "m_GameObject: {fileID:",
            "m_Icon: {fileID:",
            "m_Father: {fileID:"
        };

        public static readonly List<string> KeyWordsToIgnoreInSceneAsset = new List<string>
        {
            "m_OcclusionCullingData: {fileID:",
            "m_HaloTexture: {fileID:",
            "m_CustomReflection: {fileID:",
            "m_Sun: {fileID:",
            "m_LightmapParameters: {fileID:",
            "m_LightingDataAsset: {fileID:",
            "m_LightingSettings: {fileID:",
            "m_NavMeshData: {fileID:",
            "m_Icon: {fileID:",
            "m_StaticBatchRoot: {fileID:",
            "m_ProbeAnchor: {fileID:",
            "m_LightProbeVolumeOverride: {fileID:",
            "m_Cookie: {fileID:",
            "m_Flare: {fileID:",
            "m_TargetTexture: {fileID:"
        };

        public static string[] TryReadAllLines(string path)
        {
            if (Directory.Exists(path))
                return Array.Empty<string>();

            try
            {
                return File.ReadAllLines(path);
            }
            catch (Exception e)
            {
                Debug.LogError(e);
                return Array.Empty<string>();
            }
        }

        public static bool IsSystemReference(string line, List<string> keyWordsToIgnore)
        {
            foreach (var keyword in keyWordsToIgnore)
            {
                if (line.Contains(keyword))
                    return true;
            }

            return false;
        }
    }
}
