using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge.UI.Controls;
using UnityOpenMcpVerify.Cache;

namespace UnityOpenMcpBridge
{
    public partial class UnityOpenMcpBridgeWindow
    {
        // The Extensions tab has two sections:
        //  1. Optional Unity dependencies (M18 T18.4.2) — the live install /
        //     status panel for the embedded domain tool groups. Owns the
        //     one-click UPM install/remove actions.
        //  2. Community / planned packs — the legacy ExtensionCatalog mirror.
        //     Shipped domains no longer need a separate pack (their tools are
        //     embedded), so this section now advertises only third-party and
        //     planned packs.
        private void DrawExtensionsTab()
        {
            // Page scroll is owned by the shell (DrawContent).
            OptionalDependenciesPanel.Draw();

            BridgeGUIUtilities.HorizontalLine(2, 8);

            DrawCommunityPacksSection();
        }

        private void DrawCommunityPacksSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Community / planned packs", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Shipped domain tools (NavMesh, Input System, ProBuilder, " +
                "Particle System, Animation) are embedded inside the bridge " +
                "and activate automatically when the matching Unity package is " +
                "present — see the Optional Unity dependencies panel above for " +
                "one-click install. The rows below cover the legacy catalog: " +
                "third-party / community packs still live under " +
                "packages/extensions/ as separate UPM packages, and planned " +
                "domains are coming-soon placeholders.",
                MessageType.None);

            BridgeGUIUtilities.HorizontalLine(2, 4);

            var installedCount = 0;
            foreach (var pack in ExtensionCatalog.Packs)
            {
                if (DrawExtensionPackRow(pack)) installedCount++;
            }

            BridgeGUIUtilities.HorizontalLine(2, 4);
            EditorGUILayout.LabelField(
                $"Installed: {installedCount} / {ExtensionCatalog.Packs.Length}",
                EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                "Catalog source: packages/bridge/Editor/UI/ExtensionCatalog.cs " +
                "(add a new pack here + mirror it in hub/src/lib/services/extensions.ts).",
                EditorStyles.miniLabel);
        }

        // Returns true when the pack is installed in this project.
        private bool DrawExtensionPackRow(ExtensionPack pack)
        {
            var installed = IsExtensionPackInstalled(pack);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();

            // Status dot + display name.
            var dotColor = !pack.Shipped
                ? new Color(0.7f, 0.7f, 0.7f)
                : installed
                    ? new Color(0.6f, 0.9f, 0.6f)
                    : new Color(1f, 0.85f, 0.4f);
            var prev = GUI.color;
            GUI.color = dotColor;
            GUILayout.Label(new GUIContent("●",
                "Pack status: green = installed in this project, amber = available but not installed, grey = planned (not yet shipped)."),
                EditorStyles.boldLabel, GUILayout.Width(18));
            GUI.color = prev;

            EditorGUILayout.LabelField(pack.DisplayName, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();

            var statusLabel = !pack.Shipped ? "planned" : (installed ? "installed" : "available");
            var statusTooltip = !pack.Shipped
                ? "Planned pack: not yet shipped. Listed as a preview of upcoming domains."
                : installed
                    ? "Installed: the pack's assembly is loaded and its tools are registered in this project."
                    : "Available: the pack is shipped but not installed in this project. Add the UPM dependency to enable it.";
            BridgeGUIUtilities.DrawColoredLabel(statusLabel, dotColor, 90, statusTooltip);

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(pack.Description, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.FieldLabel("Package",
                "UPM package id for this extension pack.", 70);
            EditorGUILayout.SelectableLabel(pack.Id, EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(pack.UpmDependency))
            {
                EditorGUILayout.BeginHorizontal();
                BridgeGUIUtilities.FieldLabel("Unity dep",
                    "Unity package this domain needs to compile (e.g. com.unity.ai.navigation). Install it to activate the embedded tools.",
                    70);
                EditorGUILayout.LabelField(pack.UpmDependency, EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();
            }

            if (pack.ToolIds != null && pack.ToolIds.Length > 0)
            {
                EditorGUILayout.BeginHorizontal();
                BridgeGUIUtilities.FieldLabel("Tools",
                    "Tool ids this pack contributes. Once installed they appear in the Tools tab.", 70);
                EditorGUILayout.LabelField(
                    $"{pack.ToolIds.Length} tool(s) — {pack.ToolIds[0]}…",
                    EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.FieldLabel("Install",
                "Snippet to paste into your Packages/manifest.json dependencies to add this pack via local file reference.",
                70);
            EditorGUILayout.SelectableLabel(
                $"\"{pack.Id}\": \"file:../../{pack.LocalPath}\"",
                EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
            return installed;
        }

        // A pack is installed when at least one of its tool ids is registered
        // (the extension assembly is loaded → BridgeToolRegistry picked it up).
        // Planned packs (shipped:false) report as not-installed by definition.
        private static bool IsExtensionPackInstalled(ExtensionPack pack)
        {
            if (!pack.Shipped || pack.ToolIds == null || pack.ToolIds.Length == 0)
                return false;
            return BridgeToolRegistry.Contains(pack.ToolIds[0]);
        }

        // ---------- Info tab ----------
    }
}
