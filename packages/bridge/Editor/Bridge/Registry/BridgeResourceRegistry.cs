using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace UnityOpenMcpBridge
{
    public static class BridgeResourceRegistry
    {
        private static readonly Dictionary<string, BridgeResourceEntry> _resources = new();

        public static int Count => _resources.Count;

        // Production scan entry point. Excludes test assemblies (anything
        // referencing nunit.framework) so the [BridgeResource] fixtures that
        // drive the bridge's EditMode tests never leak into GET /resources or
        // the resource catalog. Mirrors BridgeToolRegistry.Scan(bool).
        public static void Scan()
        {
            Scan(includeTestAssemblies: false);
        }

        // Tests opt into scanning their own nunit assembly via
        // includeTestAssemblies: true (the AttributeScannerTests resource
        // fixtures live in com.alexeyperov.unity-open-mcp-bridge.Editor.Tests,
        // which references nunit.framework and is therefore excluded by the
        // default Scan()).
        internal static void Scan(bool includeTestAssemblies)
        {
            _resources.Clear();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!includeTestAssemblies && IsTestAssembly(assembly)) continue;
                try
                {
                    ScanAssembly(assembly);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[BridgeResourceRegistry] Error scanning assembly {assembly.GetName().Name}: {e.Message}");
                }
            }

            Debug.Log($"[BridgeResourceRegistry] Registered {_resources.Count} resource(s)");
        }

        private static void ScanAssembly(Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (!type.IsClass) continue;
                if (type.GetCustomAttribute<BridgeResourceTypeAttribute>() == null) continue;

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                {
                    var attr = method.GetCustomAttribute<BridgeResourceAttribute>();
                    if (attr == null) continue;
                    if (!attr.Enabled) continue;

                    if (_resources.ContainsKey(attr.Route))
                    {
                        Debug.LogWarning($"[BridgeResourceRegistry] Duplicate resource route '{attr.Route}' — keeping first registered");
                        continue;
                    }

                    var entry = new BridgeResourceEntry(
                        name: attr.Name,
                        route: attr.Route,
                        mimeType: attr.MimeType,
                        description: attr.Description,
                        enabled: attr.Enabled,
                        method: method
                    );

                    _resources[attr.Route] = entry;
                }
            }
        }

        public static List<BridgeResourceEntry> All()
        {
            return _resources.Values.ToList();
        }

        public static BridgeResourceEntry FindByRoute(string route)
        {
            _resources.TryGetValue(route, out var entry);
            return entry;
        }

        // Same identification as BridgeToolRegistry.IsTestAssembly — a
        // reference to nunit.framework marks an EditMode test assembly.
        private static bool IsTestAssembly(Assembly assembly)
        {
            AssemblyName[] refs;
            try
            {
                refs = assembly.GetReferencedAssemblies();
            }
            catch
            {
                return false;
            }
            if (refs == null) return false;
            foreach (var r in refs)
            {
                if (r != null && r.Name == "nunit.framework") return true;
            }
            return false;
        }
    }
}
