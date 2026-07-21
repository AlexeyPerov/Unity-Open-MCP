[English](../../setup/manual-setup.md) · Русский

# Ручная установка

Установка Unity Open MCP без Unity Hub Pro.

## Для кого это

Это обычный способ установить Unity Open MCP из терминала: добавить пакеты
Unity, затем указать MCP-клиенту на сервер.

Нужен GUI? Путь через **мастер** — [wizard-setup.md](wizard-setup.md).
Хотите экспериментальную установку ИИ-агентом? См. [agent-setup.md](agent-setup.md).
Работаете с этим репозиторием? См. [development-setup.md](development-setup.md).

У Unity Open MCP две половины, которые нужно установить: **сторона Unity**
(пакеты bridge + verify в редакторе) и **сторона ИИ** (небольшой Node
MCP-сервер, который запускает клиент). Ниже — шаги для каждой.

## Требования

- **Unity 2022.3 LTS или новее**.
- **Node.js 18 или новее** — нужен только чтобы MCP-клиент мог запустить сервер
  (`npx`). Установите с <https://nodejs.org/> (LTS), перезапустите терминал и
  проверьте через `node --version`.
- **MCP-клиент с поддержкой stdio MCP-серверов** — Cursor, Claude Desktop,
  Claude Code, OpenCode, ZCode, Cline, Codex, VS Code Copilot, Gemini CLI или
  любой совместимый клиент. Готовые фрагменты — в
  [Конфигурации MCP-клиента](client-configuration.md).

## 1) Добавьте пакеты Unity

Откройте `Packages/manifest.json` в проекте Unity (например
`MyGame/Packages/manifest.json`) и добавьте эти записи в `dependencies`:

```json
{
  "dependencies": {
    "com.alexeyperov.unity-open-mcp-bridge": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/bridge#bridge-v0.7.0",
    "com.alexeyperov.unity-open-mcp-verify": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/verify#verify-v0.7.0"
  }
}
```

Зафиксируйте ту же версию на стороне MCP-сервера (шаг 2). При обновлении см.
[Версионирование](../../versioning.md).

Опциональные доменные пакеты (NavMesh, Input System, …) и панель установки в
редакторе — в [Расширениях](../../extensions.md); для базовой установки они не
нужны.

## 2) Настройте MCP-клиент

1. Откройте [Конфигурацию MCP-клиента](client-configuration.md).
2. Найдите свой клиент в таблице и запомните путь к файлу конфигурации.
3. Скопируйте фрагмент для этого клиента.
4. Замените `/absolute/path/to/project` на абсолютный путь к корню проекта
   Unity (папка с `Assets/`, `Packages/`, `ProjectSettings/`).
5. Сохраните файл (если в нём уже есть другие MCP-серверы, добавьте только
   запись `unity-open-mcp`).

## 3) Откройте Unity и проверьте

1. Откройте **тот же** проект Unity (`UNITY_PROJECT_PATH`) в редакторе.
2. Дождитесь компиляции скриптов (статус-бар справа внизу).
3. Перезапустите MCP-клиент, чтобы он перечитал конфигурацию из шага 2.
4. В Unity откройте **Tools → Unity Open MCP Bridge** — должен быть статус
   **connected**. Если так — готово.

Попросите ИИ-клиент запустить любой инструмент Unity Open MCP (например, список
возможностей). Если он отвечает данными Unity, обе половины общаются.

## Опциональные следующие шаги

- Доменные пакеты и активация — [Расширения](../../extensions.md)
- CI / CLI-автоматизация — [CLI и автоматизация](../../api/cli-automation.md)
- Стартовые модальные окна на автономных машинах — [Политика диалогов](../../dialog-policy.md)

## Устранение неполадок

Убедитесь, что Unity открыт на том же абсолютном `UNITY_PROJECT_PATH`,
компиляция завершена, а MCP-клиент перезапущен. Затем следуйте
[Устранению неполадок](../../troubleshooting.md). Политика модальных окон и
детали macOS Accessibility — в [Политике диалогов](../../dialog-policy.md).

## Связанные документы

- [Установка агентом](agent-setup.md)
- [Конфигурация MCP-клиента](client-configuration.md)
- [Установка через мастер](wizard-setup.md)
- [Установка для разработки](development-setup.md)
- [Unity Hub Pro](../../unity-hub-pro.md)
