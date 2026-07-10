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
        [NonSerialized] private Vector2 _batchTabScroll;

        private void DrawBatchTab()
        {
            _batchTabScroll = EditorGUILayout.BeginScrollView(_batchTabScroll);
            BridgeBatchPanel.Draw();
            EditorGUILayout.EndScrollView();
        }
    }
}
