using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace UnityOpenMcpBridge
{
    public static class BridgeResourceRegistry
    {
        static readonly Dictionary<string, BridgeResourceEntry> _resources = new();

        public static int Count => _resources.Count;

        public static void Scan()
        {
            _resources.Clear();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
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

        static void ScanAssembly(Assembly assembly)
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
    }
}
