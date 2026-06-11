using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityAgentVerify.Rules
{
    public class MissingReferencesRule : IVerifyRule
    {
        public string Id => "missing_references";

        public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
        {
            if (scope.Paths == null) return;

            foreach (var assetPath in scope.Paths)
            {
                if (string.IsNullOrEmpty(assetPath)) continue;
                if (assetPath.EndsWith(".unity", System.StringComparison.OrdinalIgnoreCase)) continue;

                ScanAsset(assetPath, sink);
            }
        }

        static void ScanAsset(string assetPath, List<VerifyIssue> sink)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null) return;

            if (asset is GameObject go)
                ScanGameObjectHierarchy(go, assetPath, sink);
            else
                ScanSerializedObject(asset, assetPath, asset.GetType().Name, sink);
        }

        static void ScanGameObjectHierarchy(GameObject root, string assetPath, List<VerifyIssue> sink)
        {
            ScanGameObjectAndChildren(root, assetPath, sink);
        }

        static void ScanGameObjectAndChildren(GameObject go, string assetPath, List<VerifyIssue> sink)
        {
            var components = go.GetComponents<Component>();
            foreach (var component in components)
            {
                if (component == null)
                {
                    sink.Add(new VerifyIssue(
                        "missing_references",
                        VerifySeverity.Error,
                        assetPath,
                        "MISSING_SCRIPT",
                        $"Missing script on GameObject '{GetHierarchyPath(go)}'"));
                    continue;
                }

                ScanSerializedObject(component, assetPath,
                    $"{component.GetType().Name} on '{GetHierarchyPath(go)}'", sink);
            }

            for (int i = 0; i < go.transform.childCount; i++)
                ScanGameObjectAndChildren(go.transform.GetChild(i).gameObject, assetPath, sink);
        }

        static void ScanSerializedObject(Object obj, string assetPath, string context, List<VerifyIssue> sink)
        {
            if (obj == null) return;

            SerializedObject so = null;
            try
            {
                so = new SerializedObject(obj);
            }
            catch
            {
                return;
            }

            var prop = so.GetIterator();
            while (prop.NextVisible(true))
            {
                if (prop.propertyType == SerializedPropertyType.ObjectReference
                    && prop.objectReferenceValue == null
                    && prop.objectReferenceInstanceIDValue != 0)
                {
                    sink.Add(new VerifyIssue(
                        "missing_references",
                        VerifySeverity.Error,
                        assetPath,
                        "MISSING_REF",
                        $"Missing reference '{prop.propertyPath}' on {context}"));
                }
            }
        }

        static string GetHierarchyPath(GameObject go)
        {
            var path = go.name;
            var current = go.transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }
            return path;
        }
    }
}
