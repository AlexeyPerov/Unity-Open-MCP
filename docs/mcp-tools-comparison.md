# MCP tools for Unity — comparison

If you didn't find what you were looking for here, this page lists the other MCP tools and AI assistants that connect to Unity and compares them feature by feature. You may find one that better matches your workflow — and that's fine. Pick whatever gets your game shipped.

> **A note on categories.** Most entries below are **MCP servers** — they expose Unity to an AI client of *your* choice (Claude, Cursor, Windsurf, etc.). Two entries (**Bezi** and **Unity's official AI**) are **bundled assistants** that ship their own AI and simply live inside Unity; they're included because they solve the same problem from the user's side.

## Legend

- ✅ supported &nbsp;·&nbsp; 🟡 partial / limited &nbsp;·&nbsp; ❌ not available &nbsp;·&nbsp; ⭐ standout
- `BYO AI` = bring your own AI client via MCP; `bundled` = the tool ships its own AI.

## Feature matrix

Columns are abbreviated; see **Projects** below for full names and links.

| Feature | Open MCP | Unity-MCP (Ivan) | Coplay | unity-cli | UCP | Unified | AnkleBreaker | Unity AI (official) | Bezi |
|---|---|---|---|---|---|---|---|---|---|
| Model | BYO AI | BYO AI | BYO AI | BYO AI | BYO AI | BYO AI | BYO AI | bundled | bundled |
| License | ✅ MIT | ✅ MIT | ✅ MIT / Apache-2.0 | open | open | open | 🟡 custom¹ | proprietary | proprietary |
| Cost | free | free | free | free | free | free | free | Unity sub + credits | $60–$200/mo² |
| Unity versions | ✅ 2022.3 LTS+ | 2021.3+ | ✅ 2021.3+ | 6 only | 2021.3+ | 2021.3+ | 2021.3 LTS+ | 6+ | see site |
| Setup experience | ⭐ Hub wizard + launcher | ✅ in-Unity window + CLI | ✅ in-Unity configurator | install script | `ucp install` + `doctor` | copy folder | 🟡 plugin + manual JSON config | in-Editor settings | plugin + account |
| Tool surface | ✅ ~160 | ✅ 70+ | ✅ 43 grouped | ~11 | ~30 | ~52 | ✅ ~288 | growing | n/a (assistant) |
| Scene / GameObject / component editing | ✅ | ✅ | ✅ | 🟡 | ✅ | ✅ | ✅ | ✅ | ✅ |
| Asset read / search / references | ✅ | ✅ | ✅ | 🟡 | ✅ | 🟡 | ✅ | 🟡 | 🟡 full-project context |
| Screenshots | ✅ | ⭐ scene/game/iso | ✅ | ✅ | ✅ | ❌ | ✅ | 🟡 | 🟡 visual-bug tracing |
| Console & compile errors | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Profiler | ✅ | ⭐ deep | ✅ | 🟡 | ✅ | 🟡 | ✅ | 🟡 | ❌ |
| Test runner | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | 🟡 | ❌ |
| Dynamic code execution | ✅ Roslyn | ✅ Roslyn | ✅ Roslyn | ✅ Roslyn | ✅ | ❌ | ✅ Roslyn | 🟡 | ✅ in-engine Actions |
| Safety: checkpoints / validation / undo | ⭐ gate + delta + regression | ❌ | ❌ | ❌ | 🟡 dirty-guard | ❌ | ❌ | ❌ | ✅ reviewable + one-click undo |
| Offline / CI / batch | ✅ | ✅ server mode | ✅ CLI + remote | ❌ | ✅ | ❌ | ❌ | ❌ | ❌ |
| Multi-agent concurrency | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ⭐ fair queue + routing | ❌ | ❌ |
| Extension / domain packs | ✅ 5 (Nav/Input/ProBuilder/…) | ⭐ 10 (Cinemachine/Terrain/…) | 🟡 tool groups | custom | — | module stubs | 🟡 package-gated | — | — |
| AI client auto-config | ✅ Cursor/Claude/OpenCode/Zed | ⭐ 14+ | ⭐ broad matrix | manual | Claude plugin | generic | ❌ manual | n/a | n/a |

¹ AnkleBreaker uses a custom "AnkleBreaker Open License" (attribution required, resale forbidden) — read it before depending on it commercially.
² Bezi is credit-based with a bundled model (Claude); price is on top of your existing AI subscriptions.

## Projects

- **Open MCP** — this project. [GitHub](https://github.com/alexeyperov/Unity-AI-Hub) · MIT.
- **Unity-MCP (Ivan)** — IvanMurzak/Unity-MCP. AI skills, MCP tools, and CLI for Editor & Runtime. [GitHub](https://github.com/IvanMurzak/Unity-MCP) · MIT.
- **Coplay** — CoplayDev/unity-mcp. Bridge between AI assistants and the Unity Editor. [GitHub](https://github.com/CoplayDev/unity-mcp) · MIT / Apache-2.0.
- **unity-cli** — hatayama/unity-ai-cli-bridge. Thin Go CLI pass-through. [PulseMCP](https://www.pulsemcp.com/servers/hatayama-unity-ai-cli-bridge).
- **UCP** — Unity Control Protocol. Rust binary + npm, explicit lifecycle taxonomy.
- **Unified** — UnifiedUnityMCP. In-editor, module-factory design.
- **AnkleBreaker** — AnkleBreaker-Studio/unity-mcp-server. Broadest raw tool count (~288) and the only multi-agent scheduling model. [GitHub](https://github.com/AnkleBreaker-Studio/unity-mcp-server).
- **Unity AI (official)** — Unity's own AI Assistant package (`com.unity.ai.assistant`), MCP-exposed. [Unity docs](https://docs.unity3d.com/Packages/com.unity.ai.assistant@latest).
- **Bezi** — proprietary Unity AI assistant; bundled model, reviewable checkpointed changes. [bezi.com](https://www.bezi.com/) · [pricing](https://www.bezi.com/pricing).

All project details, licenses, and feature claims above are drawn from each project's public repo and docs at the time of writing and may change — verify before committing.
