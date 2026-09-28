[English](../../setup/portable-config.md) · Русский

# Переносимая конфигурация MCP (для коммита)

Положите одну конфигурацию MCP-клиента в репозиторий и дайте ей работать у всей
команды — без абсолютного пути каждого разработчика и без «скопируй пример и
поправь шестую строку».

Эта страница про форму, пригодную для коммита. Каталог путей и фрагментов для
конкретной машины — в [Конфигурации MCP-клиента](client-configuration.md).

## Две раскладки

**Раскладка A — проект Unity и есть репозиторий.** AI-клиент открыт на папке с
`Assets/`, `Packages/` и `ProjectSettings/`.

```
my-game/                    <- здесь открыт AI-клиент
  .cursor/mcp.json          <- в коммите
  Assets/
  Packages/
  ProjectSettings/
```

**Раскладка B — монорепозиторий.** AI-клиент открыт на корне репозитория, а
проект Unity лежит в подпапке.

```
my-game/                    <- здесь открыт AI-клиент, здесь же конфигурация
  .cursor/mcp.json          <- в коммите, указывает на Client/
  Client/                   <- проект Unity (Assets/, Packages/)
  Server/
```

Всё дальнейшее одинаково для обеих раскладок; в раскладке B дополнительно
называется подпапка Unity (`Client`).

## Как определяется путь к проекту

Серверу нужен один абсолютный корень проекта Unity. Он берёт первое из
заданного:

| Приоритет | Вход | Результат |
|---|---|---|
| 1 | `--project <path>` (только CLI) | абсолютный либо относительно рабочего каталога |
| 2 | `UNITY_PROJECT_PATH` | абсолютный либо относительно рабочего каталога |
| 3 | `--project-from-cwd` + необязательный `--unity-subpath <rel>` | рабочий каталог плюс подпапка |
| 4 | ничего | ошибка старта со списком этих вариантов |

`UNITY_PROJECT_PATH` важнее `--project-from-cwd`, поэтому разработчик всё ещё
может переопределить общую конфигурацию своим окружением.

`--unity-subpath` не ломается, если коллега открыл клиент на папке Unity, а не
на корне репозитория: когда `<cwd>/<subpath>` не является проектом Unity, а сам
рабочий каталог является, используется рабочий каталог (источник `cwd`).
Итоговый путь печатается в stderr при старте:

```
[unity-open-mcp] Unity project resolved to /Users/dev/my-game/Client (source: cwd+subpath)
```

Если полученная папка не является проектом Unity, сервер сообщает об этом и
завершается, а не подключается к чужому мосту.

## Матрица клиентов

Три переносимые формы — в зависимости от возможностей клиента:

| Форма | Как работает | Когда использовать |
|---|---|---|
| **Подстановка** | клиент раскрывает `${workspaceFolder}` внутри конфигурации | клиент это умеет (Cursor, VS Code) |
| **Аргументы** | сервер определяет путь по каталогу, в котором его запустили | клиент запускает MCP-серверы из корня рабочей области |
| **Обёртка** | закоммиченный скрипт определяет путь по своему расположению | не работает ни один из вариантов выше |

| Клиент | Файл конфигурации | Раскладка A | Раскладка B |
|---|---|---|---|
| Cursor | `<workspace>/.cursor/mcp.json` | `${workspaceFolder}` | `${workspaceFolder}/Client` |
| Claude Code | `<workspace>/.mcp.json` | `--project-from-cwd` | `--project-from-cwd --unity-subpath Client` |
| ZCode | `<workspace>/.zcode/config.json` | `--project-from-cwd` | `--project-from-cwd --unity-subpath Client` |
| VS Code Copilot | `<workspace>/.vscode/mcp.json` | `${workspaceFolder}` | `${workspaceFolder}/Client` |
| Visual Studio Copilot | `<workspace>/.vs/mcp.json` | `${workspaceFolder}` | `${workspaceFolder}/Client` |
| OpenCode | `<workspace>/opencode.json` | `--project-from-cwd` | `--project-from-cwd --unity-subpath Client` |
| GitHub Copilot CLI | `<workspace>/.mcp.json` | `--project-from-cwd` | `--project-from-cwd --unity-subpath Client` |
| Gemini CLI | `<workspace>/.gemini/settings.json` | `--project-from-cwd` | `--project-from-cwd --unity-subpath Client` |
| Kilo Code | `<workspace>/.kilocode/mcp.json` | `--project-from-cwd` | `--project-from-cwd --unity-subpath Client` |
| Rider (Junie) | `<workspace>/.junie/mcp/mcp.json` | `--project-from-cwd` | `--project-from-cwd --unity-subpath Client` |
| ZooCode | `<workspace>/.roo/mcp.json` | `--project-from-cwd` | `--project-from-cwd --unity-subpath Client` |
| Unity AI | `<unity-project>/UserSettings/mcp.json` | `--project-from-cwd` | — (конфигурация лежит в проекте Unity) |
| Codex | `<workspace>/.codex/config.toml` | [обёртка](#скрипт-обёртка) | [обёртка](#скрипт-обёртка) |
| Claude Desktop | OS-глобальная конфигурация | только абсолютный путь | только абсолютный путь |
| Cline | глобальные настройки MCP клиента | только абсолютный путь | только абсолютный путь |
| Antigravity | глобальная конфигурация Antigravity | только абсолютный путь | только абсолютный путь |

У глобальной конфигурации нет рабочей области, поэтому там всегда абсолютный
путь. Это и есть назначение абсолютной формы: запасной вариант для одной
машины, а не командный стандарт.

## Скопируйте это

### Cursor — `${workspaceFolder}`

```json
{
  "mcpServers": {
    "unity-open-mcp": {
      "command": "npx",
      "args": ["-y", "unity-open-mcp@1.3.0"],
      "env": { "UNITY_PROJECT_PATH": "${workspaceFolder}/Client" }
    }
  }
}
```

Для раскладки A уберите `/Client`.

### Claude Code и другие клиенты «только аргументы»

```json
{
  "mcpServers": {
    "unity-open-mcp": {
      "command": "npx",
      "args": [
        "-y",
        "unity-open-mcp@1.3.0",
        "--project-from-cwd",
        "--unity-subpath",
        "Client"
      ],
      "env": {}
    }
  }
}
```

Для раскладки A уберите два последних аргумента.

Чтобы этот файл записал сам Claude Code, выполните команду из корня
репозитория и закоммитьте получившийся `.mcp.json`:

```bash
claude mcp add --scope project unity-open-mcp -- npx -y unity-open-mcp@1.3.0 --project-from-cwd --unity-subpath Client
```

Без `--scope project` запись попадёт в пользовательский конфиг Claude Code и
не будет общей.

### ZCode

`<repo>/.zcode/config.json` (файл рабочей области; `~/.zcode/cli/config.json` —
ваш личный):

```json
{
  "mcp": {
    "servers": {
      "unity-open-mcp": {
        "type": "stdio",
        "command": "npx",
        "args": ["-y", "unity-open-mcp@1.3.0", "--project-from-cwd", "--unity-subpath", "Client"],
        "env": {}
      }
    }
  }
}
```

ZCode запускает stdio-серверы в рабочем каталоге сессии — корне репозитория, на
котором он открыт, — и не раскрывает `${...}` в конфигах, поэтому переносимая
форма для него — аргументы. Для раскладки A уберите два последних аргумента.
Эту запись пишет `setup --client zcode --unity-subpath Client`, запущенный из
корня репозитория. Одноимённый сервер в `~/.zcode/cli/config.json` важнее
записи рабочей области, поэтому удалите там старую личную запись
`unity-open-mcp`.

### OpenCode

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "unity-open-mcp": {
      "type": "local",
      "command": ["npx", "-y", "unity-open-mcp@1.3.0", "--project-from-cwd", "--unity-subpath", "Client"],
      "enabled": true,
      "environment": {}
    }
  }
}
```

### Скрипт-обёртка

Для клиентов, которые не раскрывают ни переменную рабочей области, ни
переменную окружения, закоммитьте небольшой скрипт и укажите на него:

```json
{
  "mcpServers": {
    "unity-open-mcp": {
      "command": "bash",
      "args": ["scripts/mcp/unity-open-mcp.sh"]
    }
  }
}
```

```toml
# .codex/config.toml
[mcp_servers.unity-open-mcp]
enabled = true
command = "bash"
args = ["scripts/mcp/unity-open-mcp.sh"]
```

Скрипт запишет окно bridge (см. [Из редактора Unity](#из-редактора-unity)),
либо создайте его через setup-CLI, чтобы версия в нём совпадала с реально
установленным пакетом:

```bash
cd my-game
npx -y unity-open-mcp@latest setup --client codex --unity-subpath Client
```

Для монорепозитория он появится в `scripts/mcp/unity-open-mcp.sh`, а когда
проект Unity и есть репозиторий — в `.unity-open-mcp/mcp-wrapper.sh`. Скрипт
определяет корень Unity по своему расположению, экспортирует
`UNITY_PROJECT_PATH` и запускает зафиксированный пакет — рабочий каталог
клиента при этом не важен. Для разового запуска подпапку Unity можно
переопределить через `UNITY_SUBPATH=OtherClient`.

Для Codex команда пишет скрипт и таблицу `[mcp_servers.unity-open-mcp]` в
`.codex/config.toml`, не меняя остальные байты файла. Обёртка — bash-скрипт,
поэтому на Windows ей нужен Git Bash или WSL; там setup пишет абсолютную запись
Codex, если не передан `--portable`.

## Запись через setup-CLI

```bash
cd my-game
npx -y unity-open-mcp@latest setup --client cursor --unity-subpath Client
```

- Запускайте из корня репозитория: `--unity-subpath` называет папку Unity под
  текущим каталогом. Для раскладки A запускайте в проекте Unity без подпапки.
  `--project <path>` (абсолютный или относительный) указывает проект Unity из
  любого другого места.
- Конфигурация клиента и навык пишутся в **корень рабочей области**
  (`my-game/`), а пины пакетов Unity — всегда в **проект Unity**
  (`my-game/Client/Packages/manifest.json`).
- Переносимая форма — по умолчанию для монорепозитория и для проекта Unity,
  который сам является корнем git-репозитория. `--no-portable` принудительно
  вернёт абсолютный путь, `--portable` — форму для коммита.
- Если запустить внутри `Client/` большего репозитория, setup настроит сам
  `Client/` и в предупреждении подскажет команду для запуска из корня.
- `--workspace <path>` задаёт корень репозитория явно; без него setup выводит
  его из проекта минус `--unity-subpath`.
- `--dry-run` печатает точный фрагмент, ничего не записывая — удобно проверить,
  что перед коммитом в нём нет машинного пути.

Повторный запуск с другими флагами перезаписывает запись на месте: абсолютная
становится переносимой и наоборот, соседние MCP-серверы сохраняются.

## Из редактора Unity

В **Tools → Unity Open MCP Bridge → Status → Configure AI client** есть
переключатель **Commit-safe config**. Он включён по умолчанию, если проект
Unity лежит внутри git-репозитория (как подпапка или сам является
репозиторием), и указывает целевой файл в корне репозитория. Во включённом состоянии сниппет не содержит ни пути этой машины,
ни порта bridge:

- клиенты с `${workspaceFolder}` (проектный конфиг Cursor, VS Code, Visual
  Studio) получают интерполированный `UNITY_PROJECT_PATH`;
- клиенты «только аргументы», включая ZCode (project), получают
  `--project-from-cwd [--unity-subpath Client]`; для Claude Code панель
  показывает команду `claude mcp add --scope project …`;
- Codex получает форму со скриптом-обёрткой и кнопку **Write**, которая создаёт
  скрипт по показанному пути.

Клиенты, читающие только общий для машины конфиг (Claude Desktop, Cline,
Antigravity, глобальные строки Cursor/OpenCode/ZCode), остаются с абсолютной
формой, и панель об этом сообщает. Опция **Commit-safe config** в мастере Hub
включается по умолчанию так же — когда репозиторий найден на уровне проекта
Unity или над ним.

## Как держать версию актуальной

Закоммиченная запись по-прежнему фиксирует версию (`unity-open-mcp@1.3.0`), и
её нужно менять вместе с пинами bridge и verify в `Packages/manifest.json`.
Поток **Updates** в окне bridge разрешает portable-записи относительно
каталога, в котором найден конфиг (корня рабочей области), и переписывает те,
что указывают на этот проект: `UNITY_PROJECT_PATH` с `${workspaceFolder}` или
относительным путём, `--project-from-cwd [--unity-subpath …]` и закоммиченный
скрипт-обёртка (`scripts/mcp/unity-open-mcp.sh` или
`.unity-open-mcp/mcp-wrapper.sh`) с зашитой подпапкой по умолчанию. Запись,
указывающая на другой Unity-проект того же репозитория, не трогается, как любая
чужая запись. Закоммитьте обновлённые файлы вместе.

## После клонирования у коллеги

Ничего машинного править не нужно. Каждый коллега:

1. Ставит Node.js 18 или новее.
2. Открывает проект Unity (`Client/` в монорепозитории), чтобы Package Manager
   подтянул пины bridge и verify из `Packages/manifest.json`.
3. Открывает AI-клиент на корне репозитория и подтверждает MCP-серверы
   проекта, когда клиент спросит: Claude Code и Cursor спрашивают один раз про
   серверы из репозитория, а Codex читает `.codex/config.toml` только для
   доверенного проекта.

Если у коллеги уже была локальная незакоммиченная копия того же конфига, её
нужно удалить или переименовать до `git pull`: Git не перезаписывает
неотслеживаемый файл отслеживаемым.

## Что нельзя коммитить

- **`UNITY_OPEN_MCP_BRIDGE_PORT`.** Порт по умолчанию выводится из абсолютного
  пути проекта, поэтому на каждой машине он свой. Не указывайте его — пусть
  сервер сам найдёт работающий мост. Фиксируйте порт только в локальном,
  не закоммиченном переопределении.
- **`UNITY_PATH`.** Расположение редактора Unity индивидуально.
- **Секреты.** Остальные серверы в том же файле должны читать их из окружения
  (`${env:…}`), а не из закоммиченного JSON.

## Проверка без редактора

Из корня репозитория:

```bash
npx -y unity-open-mcp@1.3.0 ping --project-from-cwd --unity-subpath Client
```

Строка при старте показывает, какой вход победил и какой абсолютный путь
получился. Ненулевой код возврата до запуска моста — это нормально; проверяется
именно строка с разрешённым путём.

Пути и абсолютные фрагменты для каждого клиента — в [Конфигурации
MCP-клиента](client-configuration.md). Первая установка — в [Установке через
агента](agent-setup.md) или [Ручной установке](manual-setup.md).
