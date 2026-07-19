[English](../../setup/client-configuration.md) · Русский

# Конфигурация MCP-клиента

Это инструкция по подключению MCP-клиента к Unity проекту.

## Перед настройкой клиента

- Установите Node.js 18 или новее.
- Определите абсолютный корень проекта Unity: каталог, содержащий `Assets/`,
  `Packages/` и `ProjectSettings/`.
- Предпочитайте локальную для проекта конфигурацию, когда клиент её поддерживает.
- Используйте версию сервера, совпадающую с привязками пакетов bridge и verify.
  Перед самостоятельным изменением одной из сторон см. [Версионирование](../../versioning.md).

Стандартная запись сервера:

```json
{
  "command": "npx",
  "args": ["-y", "unity-open-mcp@0.7.0"],
  "env": {
    "UNITY_PROJECT_PATH": "/absolute/path/to/project"
  }
}
```

`npx` скачивает и запускает именно эту версию. Первый запуск может занять 10–60
секунд; последующие используют кэш npm. Для обновления измените привязку версии
npm и привязки пакетов bridge/verify одновременно.

## Файлы клиентов и оболочки

| Клиент | Предпочтительная конфигурация | Оболочка |
|---|---|---|
| Cursor | `<project>/.cursor/mcp.json` | `mcpServers` |
| Claude Desktop | OS-глобальная конфигурация | `mcpServers` |
| Claude Code | регистрация через CLI | `claude mcp add` |
| VS Code Copilot | `<project>/.vscode/mcp.json` | `servers` with `type: "stdio"` |
| Visual Studio Copilot | `<project>/.vs/mcp.json` | `servers` with `type: "stdio"` |
| OpenCode | `<project>/opencode.json` | `mcp`; массив command; `environment` |
| ZCode | `<project>/.zcode/cli/config.json` | `mcp.servers` with `type: "stdio"` |
| Codex | `<project>/.codex/config.toml` | TOML-таблица `mcp_servers` |
| Cline | Глобальные MCP-настройки клиента | `mcpServers` |
| Gemini CLI | `<project>/.gemini/settings.json` | `mcpServers` |
| GitHub Copilot CLI | `<project>/.mcp.json` | `mcpServers` |
| Kilo Code | `<project>/.kilocode/mcp.json` | `mcpServers` |
| Rider (Junie) | `<project>/.junie/mcp/mcp.json` | `mcpServers` |
| Unity AI | `<project>/UserSettings/mcp.json` | `mcpServers` |
| ZooCode | `<project>/.roo/mcp.json` | `mcpServers` |
| Antigravity | Глобальная конфигурация Antigravity MCP | `mcpServers` |

Если конфигурация уже существует, объедините запись `unity-open-mcp`, не заменяя
посторонние настройки или соседние MCP-серверы.

### Клиенты с `mcpServers`

Используйте эту форму для Cursor, Claude Desktop, Cline, Gemini CLI, GitHub
Copilot CLI, Kilo Code, Rider, Unity AI, ZooCode и Antigravity:

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

Если сервер уже зарегистрирован, удалите и добавьте его заново, когда команду,
привязку версии или путь к проекту нужно изменить.

## Переменные окружения

| Переменная | Обязательно | Назначение |
|---|---|---|
| `UNITY_PROJECT_PATH` | да | Абсолютный корень проекта Unity. |
| `UNITY_OPEN_MCP_BRIDGE_PORT` | нет | Зафиксировать порт моста вместо поиска по пути. |
| `UNITY_PATH` | нет | Явный исполняемый файл Unity для пакетного отката, когда авто-обнаружение недоступно. |

Обработка стартовых и установившихся модальных окон имеет дополнительные переменные
окружения. Полный список, значения по умолчанию, опции безопасности и матрица
политик — в [Политике диалогов](../../dialog-policy.md).

## Альтернативные команды сервера

Для глобальной установки:

```bash
npm install -g unity-open-mcp
```

Используйте `"command": "unity-open-mcp"` без аргументов (или эквивалентный массив
команд для OpenCode). Обновляйте явно через
`npm update -g unity-open-mcp`.

Для локального чекаута соберите `mcp-server/` и замените запись `npx` на:

```json
{
  "command": "node",
  "args": ["/absolute/path/to/unity-open-mcp/mcp-server/dist/index.js"],
  "env": {
    "UNITY_PROJECT_PATH": "/absolute/path/to/project"
  }
}
```

Полный рабочий процесс контрибьютора см. в [Установке для разработки](development-setup.md).

## После правки конфигурации

Перезапустите MCP-клиент, чтобы он перечитал файл, откройте тот же проект Unity и
дождитесь завершения компиляции. Затем вызовите `unity_open_mcp_ping` или
`unity_open_mcp_capabilities`.

По подключению и восстановлению моста — [Устранение неполадок](../../troubleshooting.md).
