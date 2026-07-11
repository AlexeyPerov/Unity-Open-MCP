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
        private void DrawBatchTab()
        {
            // Page scroll is owned by the shell (DrawContent). The panel's
            // active/completed lists keep their own bounded MinHeight scrolls.
            BridgeBatchPanel.Draw();
        }
    }
}
