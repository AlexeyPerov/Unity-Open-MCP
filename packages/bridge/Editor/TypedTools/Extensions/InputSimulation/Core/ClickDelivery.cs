// inputsim_pointer routing: "raycast_real" — did the injected touch click?
//
// The pre-injection raycast only says what is on top at the press point; the
// scene's input module decides what actually gets pressed and clicked, frames
// later. Without a check a touch the module never turned into a uGUI click
// (another EventSystem consuming it, a module that lost focus, an overlay that
// moved on top in the meantime) answers like a successful click. Two
// witnesses, read during the injected frames:
//   - the module's own pointer record for the finger after the press frame
//     (PointerInputModule, e.g. StandaloneInputModule): the object it pressed
//     and what its raycast hit;
//   - for a uGUI Button handler, a temporary onClick listener: the click ran.
// The Button witness decides `clickDelivered`; without one, a press that went
// to another object decides `false`, and otherwise it stays null (unknown).
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Extensions.InputSimulation
{
    internal sealed class ClickDelivery : System.IDisposable
    {
        private static readonly FieldInfo PointerDataField =
            typeof(PointerInputModule).GetField("m_PointerData", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>The object that should receive the click (null: nothing at the point handles one).</summary>
        internal readonly GameObject ClickHandler;
        private readonly Button _button;
        private readonly UnityAction _onClick;
        private int _clicks;
        private bool _pressRead;
        private GameObject _pressed;
        private GameObject _moduleHit;

        private ClickDelivery(GameObject clickHandler)
        {
            ClickHandler = clickHandler;
            _button = clickHandler != null ? clickHandler.GetComponent<Button>() : null;
            if (_button != null)
            {
                _onClick = () => _clicks++;
                _button.onClick.AddListener(_onClick);
            }
        }

        /// <summary>Start watching a click at a point whose top hit is `topHit` (or the target itself).</summary>
        internal static ClickDelivery Watch(GameObject topHit)
            => new ClickDelivery(topHit != null ? ExecuteEvents.GetEventHandler<IPointerClickHandler>(topHit) : null);

        public void Dispose()
        {
            if (_button != null && _onClick != null) _button.onClick.RemoveListener(_onClick);
        }

        /// <summary>After the press frame: what the input module pressed for this finger.</summary>
        internal void ReadPress(int finger)
        {
            var module = EventSystem.current != null ? EventSystem.current.currentInputModule as PointerInputModule : null;
            if (module == null || PointerDataField == null) return;
            try
            {
                // The finger's record, or the mouse's when the module took the
                // touch through mouse emulation.
                if (PointerDataField.GetValue(module) is Dictionary<int, PointerEventData> data
                    && (data.TryGetValue(finger, out var ped) && ped != null
                        || data.TryGetValue(PointerInputModule.kMouseLeftId, out ped) && ped != null))
                {
                    _pressRead = true;
                    _pressed = ped.pointerPress;
                    _moduleHit = ped.pointerCurrentRaycast.gameObject;
                }
                else
                {
                    // The module holds no record for the finger: it never saw the press.
                    _pressRead = true;
                }
            }
            catch { /* unreadable on this uGUI version: no press witness */ }
        }

        /// <summary>True / false when a witness decides it, null when unknown.</summary>
        internal bool? Delivered
        {
            get
            {
                if (_button != null) return _clicks > 0;
                if (_pressRead && ClickHandler != null
                    && (_pressed == null || !PointerTargets.IsSameOrDescendant(ClickHandler, _pressed) && !PointerTargets.IsSameOrDescendant(_pressed, ClickHandler)))
                    return false;
                return null;
            }
        }

        internal int Clicks => _clicks;
        internal bool HasButtonWitness => _button != null;
        internal bool PressRead => _pressRead;
        internal GameObject Pressed => _pressed;
        internal GameObject ModuleHit => _moduleHit;

        /// <summary>The active EventSystem(s), for a click that did not arrive.</summary>
        internal static string EventSystemJson()
        {
            var es = EventSystem.current;
            var active = SceneQuery.FindAllOfType<EventSystem>(SceneQuery.Inactive.Exclude);
            int enabled = 0;
            foreach (var e in active) if (e != null && e.isActiveAndEnabled) enabled++;
            var sb = new StringBuilder(192);
            sb.Append('{');
            if (es == null) sb.Append("\"current\":null");
            else
            {
                var module = es.currentInputModule;
                sb.Append("\"current\":").Append(InputSimulationJson.Esc(PointerTargets.BuildPath(es.gameObject)));
                sb.Append(",\"scene\":").Append(InputSimulationJson.Esc(es.gameObject.scene.name));
                sb.Append(",\"enabled\":").Append(es.isActiveAndEnabled ? "true" : "false");
                sb.Append(",\"isFocused\":").Append(es.isFocused ? "true" : "false");
                sb.Append(",\"module\":").Append(module == null ? "null" : InputSimulationJson.Esc(module.GetType().Name));
                sb.Append(",\"moduleEnabled\":").Append(module != null && module.isActiveAndEnabled ? "true" : "false");
            }
            sb.Append(",\"activeEventSystems\":").Append(enabled);
            sb.Append('}');
            return sb.ToString();
        }
    }
}
