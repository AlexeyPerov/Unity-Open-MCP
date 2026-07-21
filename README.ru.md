# Unity Open MCP

[![Docs](https://img.shields.io/badge/Docs-unity--mcp-4f46e5)](https://alexeyperov.github.io/unity-open-mcp/)
[![](https://badge.mcpx.dev?status=on 'MCP Enabled')](https://modelcontextprotocol.io/introduction)
[![](https://img.shields.io/badge/Unity-000000?style=flat&logo=unity&logoColor=white 'Unity')](https://unity.com/releases/editor/archive)
[![](https://img.shields.io/badge/Node.js-339933?style=flat&logo=nodedotjs&logoColor=white 'Node.js')](https://nodejs.org/en/download/)
[![](https://img.shields.io/github/stars/AlexeyPerov/Unity-Open-MCP 'Stars')](https://github.com/AlexeyPerov/Unity-Open-MCP/stargazers)
[![](https://img.shields.io/github/last-commit/AlexeyPerov/Unity-Open-MCP 'Last Commit')](https://github.com/AlexeyPerov/Unity-Open-MCP/commits/master)
[![](https://img.shields.io/badge/License-MIT-red.svg 'MIT License')](https://opensource.org/licenses/MIT)

| [🇺🇸 English](README.md) | [🇨🇳 简体中文](README.zh-CN.md) | [🇷🇺 Русский](README.ru.md) |
|-------------------------|--------------------------------|------------------------------|

<p align="center">
  <img src="hub/src-tauri/icons/Square310x310Logo.png" alt="Unity Open MCP" width="250">
</p>

Unity Open MCP открывает ИИ-агентам набор инструментов для работы с проектами Unity.

Сам MCP-сервер состоит из **250+ инструментов** для типовых рабочих задач Unity
редактора, а также из дополнительных методов проверок результатов работы и валидации, анализа ресурсов, диагностики.

---
Часть набора Open MCP
---
[![Unity Open MCP](https://img.shields.io/badge/Unity-Open%20MCP-000000?style=flat&logo=unity&logoColor=white)](https://github.com/AlexeyPerov/Unity-Open-MCP) [![Unreal Open MCP](https://img.shields.io/badge/Unreal-Open%20MCP-0E1128?style=flat&logo=unrealengine&logoColor=white)](https://github.com/AlexeyPerov/Unreal-Open-MCP) [![Godot Open MCP](https://img.shields.io/badge/Godot-Open%20MCP-478CBF?style=flat&logo=godotengine&logoColor=white)](https://github.com/AlexeyPerov/Godot-Open-MCP)
---

## Ключевые возможности

### Интеллект по ассетам

Структурированный поиск, инспекция, пересериализация и анализ ссылок /
зависимостей — включая офлайн-чтение, когда Unity закрыт.

> **Пример:** «Найди все префабы, ссылающиеся на `PlayerController`, и кратко
> опиши входящие зависимости.»

### Живой мост, batch-fallback и офлайн-чтение

Предпочтителен живой Editor; для поддерживаемых инструментов — headless batch;
ассеты и ошибки компиляции можно читать с диска.

> **Пример:** «Мост офлайн — покажи последние ошибки компиляции из Editor.log.»

### Типизированный редактор и встроенные домены

GameObject, сцены, префабы, материалы, пакеты, плюс домены с зависимостями от
пакетов (NavMesh, Input System, Cinemachine, Timeline, Shader Graph и другие).

> **Пример:** «Активируй `cinemachine` и создай follow-камеру для Player.»

### Сенсоры агента и диагностика

Test Runner, скриншоты, консоль, profiler / memory snapshots и event pull для
замкнутых агентских циклов.

> **Пример:** «Запусти Play Mode тесты сборки Combat и сделай скриншот Game View
> при падении.»

### Группы инструментов и скилы

По умолчанию поверхность «тонкая»; домены включаются по запросу. Скилы проекта
учат агентов циклу mutate → gate → fix.

> **Пример:** «Сбрось группы инструментов, затем активируй только `core` и
> `gate-and-verify`.»

### CI, базовые линии и регрессия

Сканирование проекта, verify-baseline и падение автоматизации при новых
блокирующих проблемах — live или через batch/CLI.

> **Пример:** «Создай verify-baseline для проекта и сообщи о новых блокирующих
> проблемах с прошлого baseline.»

### Unity Hub Pro

Опциональное настольное приложение для управления проектами, мастера AI Setup и
сопровождающих действий — MCP-путь работает и без него.

> **Пример:** кнопка **AI** в строке проекта Hub (см.
> [Unity Hub Pro](docs/unity-hub-pro.md), англ.).

### Безопасные изменения со шлюзом

Изменения проходят `checkpoint → mutate → validate → delta`, с регрессионными
проверками и точечными исправлениями — агент может остановиться до того, как
«успешное» изменение сломает проект.

> **Пользователь:** Удали этот префаб.  
> **Агент:** Проверяю влияние удаления `Enemy`…  
> **Шлюз:** Удаление даст новые missing references на `Level1` и `SpawnPoint`.  
> **Агент:** Unity Open MCP показал эту регрессию в превью шлюза. Я **не** удаляю
> префаб без вашего подтверждения — эти сцены сломаются. Удалить и затем
> починить ссылки, или оставить как есть?

Больше примеров промптов: [docs/api/mcp-tools.md](docs/api/mcp-tools.md#example-prompts)
(англ.). Полный каталог и контракты — в
[docs/api/mcp-tools.md](docs/api/mcp-tools.md) (англ.).

## Быстрая установка

Требуется **Unity 2022.3 LTS или новее**.

1. **Вручную:** отредактируйте пакеты и конфигурацию MCP-клиента самостоятельно с
   помощью [Ручной установки](docs/ru/setup/manual-setup.md).
2. **Unity Hub Pro:** используйте UI-поток из
   [Установки через мастер](docs/ru/setup/wizard-setup.md).
3. **Локальный чекаут:** соберите и запустите репозиторий с помощью
   [Установки для разработки](docs/ru/setup/development-setup.md).
4. **Экспериментально — ИИ-агент:** вставьте этот промпт в ваш ИИ-клиент
   (Cursor, Claude, …). Для предсказуемой установки лучше Manual или Wizard.

```text
Установите Unity Open MCP в этот проект Unity, точно следуя
https://raw.githubusercontent.com/AlexeyPerov/Unity-Open-MCP/master/docs/setup/agent-setup.md
(загрузите процедуру заново; не импровизируйте по памяти).
Сначала прочитайте version.json из того же репозитория и используйте ТОЛЬКО эту
версию для всех привязок — никогда не выдумывайте и не вспоминайте старый 0.x.y.
Копируйте SKILL.md через curl/cp; не переписывайте его и не вызывайте generate_skill.
Выполняйте каждый шаг агента самостоятельно; останавливайтесь и сообщайте мне,
только когда требуется действие пользователя (USER ACTION).
Если этот монорепозиторий уже открыт локально, прочитайте docs/setup/agent-setup.md
и version.json с диска, а не загружайте по сети.
```

Полная процедура: [Установка агентом](docs/ru/setup/agent-setup.md).

## Документация

Для пользователей:

- [Индекс API](docs/api.md) (англ.) — контракты MCP, моста, ресурсов, маршрутизации и автоматизации.
- [Расширения](docs/extensions.md) (англ.) — встроенные домены, зависимости и активация групп инструментов.
- [Устранение неполадок](docs/troubleshooting.md) (англ.) — диагностика подключения и восстановления.
- [Политика диалогов](docs/dialog-policy.md) (англ.) — обработка стартовых модальных окон и автоматизация.
- [Скилы](docs/skills.md) (англ.) — агентские плейбуки, устанавливаемые в проекты Unity.
- [Совместимость версий](docs/versioning.md) (англ.) — согласование версий и восстановление при рассинхроне.

Для контрибьюторов:

- [Архитектура](docs/architecture.md) (англ.) — границы репозитория и поток выполнения.
- [Соглашения по коду](docs/code-conventions.md) (англ.) — неочевидные C# контракты.

> Хотите посмотреть другие MCP-решения? Смотрите [сравнение MCP-инструментов для
> Unity](docs/mcp-tools-comparison.md) (англ.) — параллельная матрица возможностей
> Unity Open MCP и других MCP-инструментов / ИИ-ассистентов в этой области.

> Примечание: помимо этого README и документов установки в `docs/ru/setup/`,
> остальная документация пока доступна только на английском.

## Unity Hub Pro

Unity Hub Pro — это настольное приложение-компаньон для Unity Open MCP. Оно помогает
управлять проектами, запускать мастер AI Setup и вести сопровождающие рабочие процессы
из единого интерфейса.
[Подробности в документации (англ.).](docs/unity-hub-pro.md)

## Контрибьюция

- Перед созданием issue или pull request прочитайте
  [CONTRIBUTING.md](CONTRIBUTING.md) (англ.).
- [Устранение неполадок для контрибьюторов](docs/troubleshooting-contributors.md)
  (англ.) охватывает локальные тесты, сбои моста и автоматизации.
- [Validation Suite](validation-suite/README.md) (англ.) — приложение для
  управляемой ручной валидации; поставляется с запускаемыми наборами сценариев.
- [Версионирование и релизы для мейнтейнеров](docs/contributing/versioning.md)
  (англ.) — синхронизация, теги и релизные процессы.

**Лицензия:** MIT — см. [LICENSE](LICENSE).
