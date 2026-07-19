[English](../../setup/client-configuration.md) · Русский

# Конфигурация MCP-клиента

Подключите MCP-клиент к одному проекту Unity: найдите клиент, скопируйте
фрагмент, укажите путь к проекту, сохраните файл, перезапустите клиент.

## Сделайте так

1. Найдите свой клиент в [таблице](#куда-положить) и запомните путь к файлу
   конфигурации.
2. Скопируйте подходящий фрагмент из [Скопируйте это](#скопируйте-это).
3. Замените `/absolute/path/to/project` на абсолютный корень проекта Unity
   (папка с `Assets/`, `Packages/` и `ProjectSettings/`).
4. Запишите содержимое в файл. Если в файле уже есть другие MCP-серверы,
   добавьте только запись `unity-open-mcp` — не стирайте соседние.
5. Перезапустите MCP-клиент, чтобы он перечитал конфигурацию.

Зафиксируйте ту же версию сервера, что у пакетов bridge/verify (`0.7.0` ниже).
При обновлении см. [Версионирование](../../versioning.md). Первый запуск `npx`
может занять 10–60 секунд на скачивание пакета; последующие запуски быстрые.

## Куда положить

| Клиент | Файл конфигурации | Фрагмент |
|---|---|---|
| Cursor | `<project>/.cursor/mcp.json` | [`mcpServers`](#mcpservers-cursor-и-большинство-клиентов) |
| Claude Desktop | OS-глобальная конфигурация | [`mcpServers`](#mcpservers-cursor-и-большинство-клиентов) |
| Claude Code | CLI (без файла) | [`Claude Code`](#claude-code) |
| VS Code Copilot | `<project>/.vscode/mcp.json` | [VS Code](#vs-code-и-visual-studio-copilot) |
| Visual Studio Copilot | `<project>/.vs/mcp.json` | [VS Code](#vs-code-и-visual-studio-copilot) |
| OpenCode | `<project>/opencode.json` | [OpenCode](#opencode) |
| ZCode | `<project>/.zcode/cli/config.json` | [ZCode](#zcode) |
| Codex | `<project>/.codex/config.toml` | [Codex](#codex) |
| Cline | Глобальные MCP-настройки клиента | [`mcpServers`](#mcpservers-cursor-и-большинство-клиентов) |
| Gemini CLI | `<project>/.gemini/settings.json` | [`mcpServers`](#mcpservers-cursor-и-большинство-клиентов) |
| GitHub Copilot CLI | `<project>/.mcp.json` | [`mcpServers`](#mcpservers-cursor-и-большинство-клиентов) |
| Kilo Code | `<project>/.kilocode/mcp.json` | [`mcpServers`](#mcpservers-cursor-и-большинство-клиентов) |
| Rider (Junie) | `<project>/.junie/mcp/mcp.json` | [`mcpServers`](#mcpservers-cursor-и-большинство-клиентов) |
| Unity AI | `<project>/UserSettings/mcp.json` | [`mcpServers`](#mcpservers-cursor-и-большинство-клиентов) |
| ZooCode | `<project>/.roo/mcp.json` | [`mcpServers`](#mcpservers-cursor-и-большинство-клиентов) |
| Antigravity | Глобальная конфигурация Antigravity MCP | [`mcpServers`](#mcpservers-cursor-и-большинство-клиентов) |

Предпочитайте локальный для проекта путь, когда клиент его поддерживает.

## Скопируйте это

### `mcpServers` (Cursor и большинство клиентов)

Для Cursor, Claude Desktop, Cline, Gemini CLI, GitHub Copilot CLI, Kilo Code,
Rider, Unity AI, ZooCode и Antigravity:

```json
{
  "mcpServers": {
    "unity-open-mcp": {
      "command": "npx",
      "args": ["-y", "unity-open-mcp@0.7.0"],
      "env": {
        "UNITY_PROJECT_PATH": "/absolute/path/to/project"
      }
    }
  }
}
```

### VS Code и Visual Studio Copilot

```json
{
  "servers": {
    "unity-open-mcp": {
      "type": "stdio",
      "command": "npx",
      "args": ["-y", "unity-open-mcp@0.7.0"],
      "env": { "UNITY_PROJECT_PATH": "/absolute/path/to/project" }
    }
  }
}
```

### OpenCode

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "unity-open-mcp": {
      "type": "local",
      "command": ["npx", "-y", "unity-open-mcp@0.7.0"],
      "enabled": true,
      "environment": { "UNITY_PROJECT_PATH": "/absolute/path/to/project" }
    }
  }
}
```

### ZCode

```json
{
  "mcp": {
    "servers": {
      "unity-open-mcp": {
        "type": "stdio",
        "command": "npx",
        "args": ["-y", "unity-open-mcp@0.7.0"],
        "env": { "UNITY_PROJECT_PATH": "/absolute/path/to/project" }
      }
    }
  }
}
```

### Codex

```toml
[mcp_servers.unity-open-mcp]
enabled = true
command = "npx"
args = ["-y", "unity-open-mcp@0.7.0"]

[mcp_servers.unity-open-mcp.env]
UNITY_PROJECT_PATH = "/absolute/path/to/project"
```

### Claude Code

```sh
claude mcp add unity-open-mcp \
  --env UNITY_PROJECT_PATH=/absolute/path/to/project \
  -- npx -y unity-open-mcp@0.7.0
```

Если сервер уже зарегистрирован, удалите и добавьте его заново, когда нужно
изменить команду, привязку версии или путь к проекту.

## Опционально

| Переменная | Обязательно | Назначение |
|---|---|---|
| `UNITY_PROJECT_PATH` | да | Абсолютный корень проекта Unity. |
| `UNITY_OPEN_MCP_BRIDGE_PORT` | нет | Зафиксировать порт моста вместо поиска по пути. |
| `UNITY_PATH` | нет | Явный исполняемый файл Unity для пакетного отката. |

Переменные стартовых модальных окон: [Политика диалогов](../../dialog-policy.md).

**Глобальная установка** (вместо `npx`): `npm install -g unity-open-mcp`, затем
`"command": "unity-open-mcp"` без `args`. Обновление:
`npm update -g unity-open-mcp`.

**Локальный чекаут:** соберите `mcp-server/` и укажите
`node /absolute/path/to/unity-open-mcp/mcp-server/dist/index.js` — см.
[Установку для разработки](development-setup.md).

Проблемы с подключением после настройки — в
[Устранении неполадок](../../troubleshooting.md).
