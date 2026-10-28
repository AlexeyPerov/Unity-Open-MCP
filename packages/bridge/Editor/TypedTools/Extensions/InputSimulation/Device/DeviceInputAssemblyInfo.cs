// The device-input core (backend routing, the legacy touch adapter, the touch
// and gesture tools) lives in the bridge root assembly so it compiles in every
// project — including legacy-only projects without com.unity.inputsystem. The
// optional sub-assemblies plug into it: the Input System half registers its
// touchscreen backend, the uGUI half registers the top-hit probe and uses the
// shared sequence runner for `inputsim_pointer routing: raycast_real`.
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("com.alexeyperov.unity-open-mcp-bridge.InputSystem.Editor")]
[assembly: InternalsVisibleTo("com.alexeyperov.unity-open-mcp-bridge.InputSimulation.Ugui.Editor")]
[assembly: InternalsVisibleTo("com.alexeyperov.unity-open-mcp-bridge.InputSimulation.Ugui.Editor.Tests")]
[assembly: InternalsVisibleTo("com.alexeyperov.unity-open-mcp-bridge.InputSimulation.InputSystem.Editor.Tests")]
